using System;
using System.IO;
using System.Collections.Generic;
using System.Xml.Serialization;
using Microsoft.Extensions.Logging;
using Server.BuildConfig;
using Server.Runtime;
using System.Text;
using System.Linq;
using Server.Services.Stats;

namespace Server.Services {
	public class StatService : IService {
		public string ContainerPath { get; private set; }

		ILogger _logger;

		BuildServer   _server;
		BuildProcess  _process;
		StatContainer _container;

		public StatService(string containerPath, LoggerFactory loggerFactory) {
			ContainerPath = containerPath;
			_logger = loggerFactory.CreateLogger<StatService>();
		}

		public bool TryInit(BuildServer server, Project project) {
			_server = server;
			_server.OnInitBuild += OnInitBuild;
			_server.AddCommand(this, "stats", "show stats about all builds", OnStatsRequested);
			LoadContainer();
			_logger.LogDebug($"Container: {_container.Builds.Count} builds");
			return true;
		}

		XmlSerializer CreateSerializer() {
			return new XmlSerializer(typeof(StatContainer));
		}

		void LoadContainer() {
			if ( !File.Exists(ContainerPath) ) {
				_container = new StatContainer();
				_logger.LogDebug($"Not found stat file at '{ContainerPath}', initialize as empty.");
				return;
			}
			var serializer = CreateSerializer();
			using ( var stream = new FileStream(ContainerPath, FileMode.OpenOrCreate) ) {
				try {
					_container = serializer.Deserialize(stream) as StatContainer;
				} catch (Exception e) {
					_logger.LogError($"Can't load stat file from '{ContainerPath}': \"{e}\"");
					_container = new StatContainer();
				}
			}
		}

		void SaveContainer() {
			_logger.LogDebug("SaveContainer");
			var serializer = CreateSerializer();
			using ( var stream = new FileStream(ContainerPath, FileMode.OpenOrCreate) ) {
				try {
					serializer.Serialize(stream, _container);
				} catch ( Exception e ) {
					_logger.LogError($"Can't save stat file to '{ContainerPath}': \"{e}\"");
				}
			}
		}

		private void OnInitBuild(BuildProcess process) {
			_logger.LogDebug("OnInitBuild");
			_process = process;
			process.BuildDone += OnBuildDone;
		}

		private void OnBuildDone() {
			if ( _process != null ) {
				_logger.LogDebug($"OnBuildDone: {_process.Name}: {_process.IsSuccess}");
				_process.BuildDone -= OnBuildDone;
				if ( _process.IsSuccess ) {
					AddBuildStat(_process);
					SaveContainer();
				}
			}
		}

		void AddBuildStat(BuildProcess process) {
			var stat = new BuildStat(process.Name, process.StartTime, process.WorkTime);
			foreach ( var task in process.Tasks ) {
				stat.Tasks.Add(new TaskStat(task.Node.Name, task.EndTime - task.StartTime));
			}
			_container.Builds.Add(stat);
		}

		void OnStatsRequested(RequestArgs args) {
			_logger.LogDebug($"OnStatsRequested ({args.Count})");
			var sb = new StringBuilder();
			string buildName = args.Count > 0 ? args[0] : null;
			sb.Append(buildName == null ? "Stats:\n" : $"Stats ({buildName}):\n");
			var table = new StatTable();
			FormatHeader(table);
			AppendBuildStats(_container.Builds, buildName, table);
			table.Append(sb);
			_server.RaiseCommonMessage(sb.ToString());
		}

		void FormatHeader(StatTable table) {
			table.AddNewRow("BUILD", "COUNT", "MIN", "MAX", "AVG", "LAST");
		}

		void AppendBuildStats(List<BuildStat> stats, string name, StatTable table) {
			if ( string.IsNullOrEmpty(name) ) {
				var builds = _server.FindBuilds();
				foreach ( var buildName in builds.Keys ) {
					var statsByName = stats.FindAll(s => IsSameName(s, buildName));
					_logger.LogDebug($"Stats for {buildName}: {statsByName.Count}");
					AppendBuildStats(statsByName, buildName, table);
				}
				return;
			}
			table.AddNewRow(name);
			if ( stats.Count > 0 ) {
				var min = stats.Min(s => s.Duration.TotalSeconds);
				var max = stats.Max(s => s.Duration.TotalSeconds);
				var avg = stats.Average(s => s.Duration.TotalSeconds);
				var last = stats.Last().Duration.TotalSeconds;
				table.AddToRow(stats.Count.ToString(), FormatSeconds(min), FormatSeconds(max), FormatSeconds(avg), FormatSeconds(last));
			}
		}

		string FormatSeconds(double value) {
			var ts = TimeSpan.FromSeconds(value);
			if (value < 60 * 60 ) {
				return ts.ToString(@"mm\:ss");
			} else if (value < 60 * 60 * 24 ) {
				return ts.ToString(@"hh\:mm\:ss");
			}
			return ts.ToString(@"dd\.hh\:mm\:ss");
		}

		static bool IsSameName(BuildStat stat, string name) {
			return stat.Name == name;
		}
	}
}