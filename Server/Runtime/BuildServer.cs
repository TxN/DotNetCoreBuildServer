using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Server.BuildConfig;
using Server.Commands;
using Server.Services;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Server.Runtime {
	public class BuildServer {

		public event Action<RequestContext>               OnStatusRequest;
		public event Action<RequestContext, string>       OnCommonMessage;
		public event Action<RequestContext>               OnHelpRequest;
		public event Action<RequestContext, BuildProcess> OnInitBuild;

		public event Action<string, bool> OnCommonError;
		public event Action               OnStop;
		public event Action<string>       LogFileChanged;

		public string         Name     { get; }
		public Project        Project  { get; private set; }
		public List<IService> Services { get; private set; }

		public CommandSetup Commands { get; private set; }

		public string ServiceName {
			get {
				var assembly = GetType().GetTypeInfo().Assembly;
				var name = assembly.GetName();
				return $"{name.Name} {name.Version}";
			}
		}
		
		string[]       _buildArgs;
		Build          _build;
		Thread         _thread;
		BuildProcess   _process;
		List<ICommand> _curCommands = new List<ICommand>();
		
		ConcurrentDictionary<string, string> _taskStates = new ConcurrentDictionary<string, string>();
		
		DateTime _curTime => DateTime.Now;

		CommandFactory _commandFactory;
		LoggerFactory  _loggerFactory;
		ILogger        _logger;
		
		public BuildServer(CommandFactory commandFactory, LoggerFactory loggerFactory, string name) {
			_commandFactory = commandFactory;
			_loggerFactory = loggerFactory;
			_logger = _loggerFactory.CreateLogger<BuildServer>();
			_logger.LogDebug($"ctor: name: \"{name}\"");
			Name = name;
			Commands = new CommandSetup();
		}

		static string ConvertToBuildName(FileSystemInfo file) {
			var ext = file.Extension;
			return file.Name.Substring(0, file.Name.Length - ext.Length);
		}
		
		public Dictionary<string, Build> FindBuilds() {
			var tempDict = new Dictionary<string, Build>();
			var buildsPath = Project.BuildsRoot;
			if (!Directory.Exists(buildsPath)) {
				return null;
			}
			var files = Directory.EnumerateFiles(buildsPath, "*.json");
			foreach (var filePath in files) {
				var file = new FileInfo(filePath);
				var name = ConvertToBuildName(file);
				var fullPath = file.FullName;
				try {
					var build = Build.Load(_loggerFactory, name, fullPath);
					tempDict.Add(name, build);
				} catch (Exception e) {
					RaiseCommonError($"Failed to load build at \"{fullPath}\": \"{e}\"", true);	
				}
			}
			var resultDict = new Dictionary<string, Build>();
			foreach (var buildPair in tempDict) {
				try {
					var build = buildPair.Value;
					ProcessSubBuilds(build, tempDict);
					ValidateBuild(build);
					resultDict.Add(buildPair.Key, build);
				} catch(Exception e) {
					RaiseCommonError($"Failed to process build \"{buildPair.Key}\" : \"{e}\"", true);
				}
			}
			return resultDict;
		}

		void ValidateBuild(Build build) {
			foreach (var node in build.Nodes) {
				ValidateNode(node);
			}
		}

		void ValidateNode(BuildNode node) {
			if (string.IsNullOrEmpty(node.Command) || !_commandFactory.ContainsHandler(node.Command)) {
				throw new CommandNotFoundException(node.Command);
			}
		}

		void ProcessSubBuilds(Build build, Dictionary<string, Build> builds) {
			var nodes = build.Nodes;
			for (int i = 0; i < nodes.Count; i++) {
				var node = build.Nodes[i];
				var subBuildNode = node as SubBuildNode;
				if (subBuildNode == null) {
					continue;
				}
				var subBuildName = subBuildNode.Name;
				_logger.LogDebug($"ProcessSubBuilds: Process sub build node: \"{subBuildName}\"");
				Build subBuild;
				if (!builds.TryGetValue(subBuildName, out subBuild)) {
					throw new SubBuildNotFoundException(subBuildName);
				}
				ProcessSubBuilds(subBuild, builds);
				nodes.RemoveAt(i);
				var subNodes = subBuild.Nodes;
				var newNodes = new List<BuildNode>();
				foreach (var subNode in subNodes) {
					var newArgs = new Dictionary<string, string>();
					foreach (var subBuildArg in subBuildNode.Args) {
						var sbKey = subBuildArg.Key;
						var sbValue = subBuildArg.Value;
						foreach (var subNodeArg in subNode.Args) {
							var subNodeValue = subNodeArg.Value;
							string newValue = null;
							if (!newArgs.ContainsKey(subNodeArg.Key)) {
								newValue = TryReplace(subNodeValue, sbKey, sbValue);
								newArgs.Add(subNodeArg.Key, newValue);
							} else {
								newValue = TryReplace(newArgs[subNodeArg.Key], sbKey, sbValue);
								newArgs[subNodeArg.Key] = newValue;
							}
							_logger.LogDebug(
								$"ProcessSubBuilds: Convert value: \"{subNodeValue}\" => \"\"{newValue}\"\"");
						}
					}
					if ( newArgs.Count == 0 ) {
						newArgs = subNode.Args;
					}
					newNodes.Add(new BuildNode(subNode.Name, subNode.Command, newArgs));
					_logger.LogDebug(
						$"ProcessSubBuilds: Converted node: \"{subNode.Name}\", args: {newArgs.Count}");
				}
				nodes.InsertRange(i, newNodes);
			}
		}
		
		public bool TryInitialize(out string errorMessage, List<IService> services, List<string> projectPathes) {
			_logger.LogDebug(
				$"TryInitialize: services: {services.Count()}, pathes: {projectPathes.Count}");
			try {
				Project = Project.Load(_loggerFactory, Name, projectPathes);
			} catch (Exception e) {
				errorMessage = $"Failed to parse project settings: \"{e}\"";
				return false;
			}
			InitServices(services, Project);
			if (FindBuilds() == null) {
				errorMessage = $"Failed to load builds directory!";
				return false;
			}
			errorMessage = string.Empty;
			return true;
		}
		
		void InitServices(IEnumerable<IService> services, Project project) {
			Services = new List<IService>();
			foreach (var service in services) {
				_logger.LogDebug($"InitServices: \"{service.GetType().Name}\"");
				var isInited = service.TryInit(this, project);
				_logger.LogDebug($"InitServices: isInited: {isInited}");
				if (isInited) {
					Services.Add(service);
				}
			}
		}
		
		bool IsValidBuildArg(string arg, string checkRegex) {
			if ( !string.IsNullOrWhiteSpace(checkRegex) ) {
				var regex = new Regex(checkRegex);
				return regex.IsMatch(arg);
			}
			return true;
		}

		public bool TryInitBuild(RequestContext context, Build build, string[] buildArgs) {
			if (_process != null) {
				RaiseCommonError("InitBuild: server is busy!", true);
				return false;
			}
			for ( var i = 0; i < buildArgs.Length; i++ ) {
				var curArg = buildArgs[i];
				var argName = build.Args.ElementAtOrDefault(i);
				var argCheck = build.Checks.ElementAtOrDefault(i);
				if ( !IsValidBuildArg(curArg, argCheck) ) {
					RaiseCommonError($"InitBuild: argument '{argName}' is invalid: '{curArg}' don't maches by '{argCheck}'!", true);
					return false;
				}
			}
			_logger.LogDebug($"InitBuild: \"{build.Name}\"");
			_build = build;
			_process = new BuildProcess(_loggerFactory, build);
			var convertedLogFile = ConvertArgValue(Project, _build, null, build.LogFile);
			LogFileChanged?.Invoke(convertedLogFile);
			OnInitBuild?.Invoke(context, _process);
			return true;
		}

		public void StartBuild(string[] args) {
			if (_process == null) {
				_logger.LogError("StartBuild: No build to start!");
				return;
			}
			if (_process.IsStarted) {
				_logger.LogError("StartBuild: Build already started!");
				return;
			}
			_logger.LogDebug($"StartBuild: args: {args.Length}");
			_buildArgs = args;
			_thread = new Thread(ProcessBuild);
			_thread.Start();
		}

		void ProcessBuild() {
			_logger.LogDebug("ProcessBuild");
			var nodes = _build.Nodes;
			_process.StartBuild(_curTime);
			if (nodes.Count == 0) {
				_logger.LogError("ProcessBuild: No build nodes!");
				_process.Abort(_curTime);
			}
			var tasks = InitTasks(nodes);
			foreach (var task in tasks) {
				_logger.LogDebug($"ProcessBuild: task: {tasks.IndexOf(task)}/{tasks.Count}");
				task.Start();
				task.Wait();
				var result = task.Result;
				if (!result) {
					_logger.LogDebug($"ProcessBuild: failed command!");
					_process.Abort(_curTime);
				}
				if (_process.IsAborted) {
					_logger.LogDebug($"ProcessBuild: aborted!");
					break;
				}
			}
			LogFileChanged?.Invoke(null);
			_buildArgs = null;
			_build     = null;
			_thread    = null;
			_process   = null;
			_taskStates = new ConcurrentDictionary<string, string>();
			_logger.LogDebug("ProcessBuild: cleared");
		}

		List<Task<bool>> InitTasks(List<BuildNode> nodes) {
			var tasks = new List<Task<bool>>();
			Dictionary<BuildNode, Task<bool>> parallelAccum = null;
			foreach ( var node in nodes ) {
				_logger.LogDebug($"InitTasks: node: \"{node.Name}\" (\"{node.Command}\")");
				var task = new Task<bool>(() => ProcessCommand(_build, _buildArgs, node));
				if ( node.IsParallel ) {
					if ( parallelAccum == null ) {
						parallelAccum = new Dictionary<BuildNode, Task<bool>>();
					}
					parallelAccum.Add(node, task);
				} else {
					ProcessParallelTask(ref parallelAccum, nodes, tasks);
					tasks.Add(task);
				}
			}
			ProcessParallelTask(ref parallelAccum, nodes, tasks);
			return tasks;
		}

		void ProcessParallelTask(ref Dictionary<BuildNode, Task<bool>> accum, List<BuildNode> nodes, List<Task<bool>> tasks) {
			if ( accum != null ) {
				tasks.Add(CreateParallelTask(accum, nodes));
				accum = null;
			}
		}

		Task<bool> CreateParallelTask(Dictionary<BuildNode, Task<bool>> accum, List<BuildNode> nodes) {
			return new Task<bool>(() => ParallelProcess(accum, nodes));
		}

		Dictionary<int, List<Task<bool>>> CreateQueuedTaskDict(Dictionary<BuildNode, Task<bool>> rawAccum) {
			var queuedTasks = new Dictionary<int, List<Task<bool>>>();
			foreach ( var nodeTask in rawAccum ) {
				var queue = nodeTask.Key.ParallelQueue;
				if ( !queuedTasks.ContainsKey(queue) ) {
					queuedTasks.Add(queue, new List<Task<bool>>());
				}
				queuedTasks[queue].Add(nodeTask.Value);
			}
			return queuedTasks;
		}

		bool SequenceProcess(List<Task<bool>> tasks) {
			foreach ( var task in tasks ) {
				_logger.LogDebug($"SequenceProcess: start task: {tasks.IndexOf(task) + 1}/{tasks.Count}");
				task.Start();
				task.Wait();
				_logger.LogDebug($"SequenceProcess: done task: {tasks.IndexOf(task) + 1}/{tasks.Count}: {task.Result}");
				if ( !task.Result ) {
					return false;
				}
			}
			return true;
		}

		bool ParallelProcess(Dictionary<BuildNode, Task<bool>> rawAccum, List<BuildNode> nodes) {
			var queuedTasks = CreateQueuedTaskDict(rawAccum);
			var accumTasks = new List<Task<bool>>();
			foreach ( var parallelPack in queuedTasks ) {
				if ( parallelPack.Key > 0 ) {
					_logger.LogDebug($"ParallelProcess: queue: {parallelPack.Key}, tasks: {parallelPack.Value.Count}");
					accumTasks.Add(new Task<bool>(() => SequenceProcess(parallelPack.Value)));
				} else {
					_logger.LogDebug($"ParallelProcess: non-queued tasks: {parallelPack.Value.Count}");
					accumTasks.AddRange(parallelPack.Value);
				}
			}
			foreach ( var task in accumTasks ) {
				task.Start();
			}
			Task.WaitAll(accumTasks.ToArray());
			bool result = true;
			foreach ( var task in accumTasks ) {
				result = result && task.Result;
			}
			return result;
		}

		string TryReplace(string message, string key, string value) {
			var keyFormat = string.Format("{{{0}}}", key);
			if (message.Contains(keyFormat)) {
				return message.Replace(keyFormat, value);
			}
			return message;
		}
		
		string ConvertArgValue(Project project, Build build, string[] buildArgs, string value) {
			if ( value == null ) {
				return null;
			}
			var result = value;
			foreach (var key in project.Keys) {
				result = TryReplace(result, key.Key, key.Value);
			}
			if (buildArgs != null) {
				for (var i = 0; i < build.Args.Count; i++) {
					var argName = build.Args[i];
					var argValue = buildArgs[i];
					result = TryReplace(result, argName, argValue);
				}
			}
			foreach (var state in _taskStates) {
				result = TryReplace(result, state.Key, state.Value);
			}
			_logger.LogDebug($"ConvertArgValue: \"{value}\" => \"{result}\"");
			return result;
		}

		public Dictionary<string, string> FindCurrentBuildArgs() {
			if ((_buildArgs == null) || (_build == null)) {
				return null;
			}
			var dict = new Dictionary<string, string>();
			for (int i = 0; i < _build.Args.Count; i++) {
				var argName = _build.Args[i];
				var argValue = _buildArgs[i];
				dict.Add(argName, argValue);
			}
			return dict;
		}
		
		Dictionary<string, string> CreateRuntimeArgs(Project project, Build build, string[] buildArgs, BuildNode node) {
			var dict = new Dictionary<string, string>();
			foreach (var arg in node.Args) {
				var value = ConvertArgValue(project, build, buildArgs, arg.Value);
				dict.Add(arg.Key, value);
			}
			return dict;
		}
		
		bool ProcessCommand(Build build, string[] buildArgs, BuildNode node) {
			_logger.LogDebug($"ProcessCommand: \"{node.Name}\" (\"{node.Command}\")");
			_process.StartTask(node);
			var command = _commandFactory.Create(node);
			lock ( _curCommands ) {
				_curCommands.Add(command);
			}
			_logger.LogDebug($"ProcessCommand: command is \"{command.GetType().Name}\"");
			var runtimeArgs = CreateRuntimeArgs(Project, build, buildArgs, node);
			_logger.LogDebug($"ProcessCommand: runtimeArgs is {runtimeArgs.Count}");
			var result = command.Execute(_loggerFactory, runtimeArgs);
			lock ( _curCommands ) {
				_curCommands.Remove(command);
			}
			_logger.LogDebug(
				$"ProcessCommand: result is [{result.IsSuccess}, \"{result.Message}\", \"{result.Result}\"]");
			_process.DoneTask(node, _curTime, result);
			AddTaskState(node.Name, result);
			return result.IsSuccess;
		}

		void AddTaskState(string taskName, CommandResult result) {
			AddTaskState(taskName, "message", result.Message);
			AddTaskState(taskName, "result", result.Result);
		}

		void AddTaskState(string taskName, string key, string value) {
			var fullKey = $"{taskName}:{key}";
			if ( _taskStates.ContainsKey(fullKey) ) {
				var curValue = string.Empty;
				var getSuccess = _taskStates.TryGetValue(fullKey, out curValue);
				var updateSuccess = _taskStates.TryUpdate(fullKey, value, curValue);
				_logger.LogDebug($"AddTaskState: Override \"{fullKey}\"=>\"{value}\" (get success: {getSuccess}, update success: {updateSuccess})");
			} else {
				var addSuccess = _taskStates.TryAdd(fullKey, value);
				_logger.LogDebug($"AddTaskState: Add \"{fullKey}\"=>\"{value}\" (add success: {addSuccess})");
			}
		}
		
		public void RequestStatus(RequestContext context) {
			_logger.LogDebug("RequestStatus");
			OnStatusRequest?.Invoke(context);
		}

		public void AbortBuild() {
			_logger.LogDebug($"AbortBuild: hasProcess: {_process != null}");
			var proc = _process;
			if(proc != null ) {
				_logger.LogDebug("AbortBuild: Abort running process");
				proc.Abort(_curTime);
			}
			foreach ( var command in _curCommands ) {
				if ( command is IAbortableCommand abortableCommand ) {
					_logger.LogDebug("AbortBuild: Abort running command");
					abortableCommand.Abort();
				}
			}
		}
		
		public void StopServer() {
			_logger.LogDebug($"StopServer: hasProcess: {_process != null}");
			AbortBuild();
			while (_process != null) {}
			OnStop?.Invoke();
			_logger.LogDebug("StopServer: done");
		}

		public void RequestHelp(RequestContext context) {
			_logger.LogDebug("RequestHelp");
			OnHelpRequest?.Invoke(context);
		}

		public void RaiseCommonError(string message, bool isFatal) {
			_logger.LogError($"RaiseCommonError: \"{message}\", isFatal: {isFatal}");
			OnCommonError?.Invoke(message, isFatal);
		}

		public void RaiseCommonMessage(RequestContext context, string message) {
			_logger.LogDebug($"RaiseCommonMessage: \"{message}\"");
			OnCommonMessage?.Invoke(context, message);
		}

		public void AddCommand(string name, string description, Action<RequestContext, RequestArgs> handler) {
			_logger.LogDebug($"AddHandler: \"{name}\" => \"{handler.GetMethodInfo().Name}\"");
			Commands.Add(name, new BuildCommand(description, handler));
		}

		public void AddCommand(string name, string description, Action<RequestContext> handler) {
			_logger.LogDebug($"AddHandler: \"{name}\" => \"{handler.GetMethodInfo().Name}\"");
			Commands.Add(name, new BuildCommand(description, (context, _) => handler.Invoke(context)));
		}

		public T FindService<T>() where T : class, IService {
			return Services.Find(s => s is T) as T;
		}

		public void AddBuildHandler(Action<string, RequestContext, RequestArgs> handler) {
			_logger.LogDebug($"AddBuildHandler: \"{handler.GetMethodInfo().Name}\"");
			Commands.AddBuildHandler(handler);
		}
	}
}