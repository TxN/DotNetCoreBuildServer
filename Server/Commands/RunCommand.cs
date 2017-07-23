﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Server.Commands {
	[CommandAttribute("run")]
	public class RunCommand:ICommand {

		string _inMemoryLog = "";
		
		public CommandResult Execute(Dictionary<string, string> args) {
			if (args == null) {
				return CommandResult.Fail("No arguments provided!");
			}
			string path = null;
			args.TryGetValue("path", out path);
			if (string.IsNullOrEmpty(path)) {
				return CommandResult.Fail("No path provided!");
			}
			string commandArgs = null;
			args.TryGetValue("args", out commandArgs);
			string workDir = null;
			args.TryGetValue("work_dir", out workDir);
			string logFile = null;
			args.TryGetValue("log_file", out logFile);
			try {
				var startInfo = new ProcessStartInfo(path, commandArgs);
				if (!string.IsNullOrEmpty(workDir)) {
					startInfo.WorkingDirectory = workDir;
				}
				startInfo.RedirectStandardOutput = true;
				startInfo.RedirectStandardError = true;
				var process = new Process {
					StartInfo = startInfo
				};
				_inMemoryLog = "";
				process.Start();
				using (var logStream = OpenLogFile(logFile)) {
					ReadOutputAsync(process.StandardOutput, logStream);
					ReadOutputAsync(process.StandardError, logStream);
					process.WaitForExit();
				}
				
				string errorRegex = null;
				args.TryGetValue("error_regex", out errorRegex);
				string resultRegex = null;
				args.TryGetValue("result_regex", out resultRegex);
				if (!string.IsNullOrEmpty(logFile)) {
					var msg = $"Log saved to {logFile}.";
					var logContent = File.ReadAllText(logFile);
					var result = GetResultMessage(resultRegex, logContent);
					return CheckCommandResult(errorRegex, logContent, msg, result);
				} else {
					_inMemoryLog = _inMemoryLog.TrimEnd('\n');
					var msg = _inMemoryLog;
					var result = GetResultMessage(resultRegex, msg);
					return CheckCommandResult(errorRegex, msg, msg, result);
				}
			}
			catch (Exception e) {
				return CommandResult.Fail($"Failed to run process at \"{path}\": \"{e.ToString()}\"");
			}
		}

		string GetResultMessage(string resultRegex, string message) {
			if (!string.IsNullOrEmpty(resultRegex)) {
				var regex = new Regex(resultRegex);
				var match = regex.Match(message);
				var value = match.Value;
				return value;
			}
			return "";
		}
		
		FileStream OpenLogFile(string logFile) {
			if (logFile != null) {
				return File.OpenWrite(logFile);
			}
			return null;
		}
		
		async void ReadOutputAsync(StreamReader reader, FileStream logStream) {
			string line = await reader.ReadLineAsync();
			if (!string.IsNullOrEmpty(line)) {
				var endedLine = line + "\n";
				if (logStream != null) {
					var bytes = Encoding.UTF8.GetBytes(endedLine);
					logStream.Write(bytes, 0, bytes.Length);
				} else {
					_inMemoryLog += endedLine;
				}
				ReadOutputAsync(reader, logStream);
			}
		}

		bool ContainsError(string errorRegex, string message) {
			if (!string.IsNullOrEmpty(errorRegex)) {
				var regex = new Regex(errorRegex);
				return regex.IsMatch(message);
			}
			return false;
		}
		
		CommandResult CheckCommandResult(string errorRegex, string messageToCheck, string messageToShow, string result) {
			return
				ContainsError(errorRegex, messageToCheck) ?
					CommandResult.Fail(messageToShow) : 
					CommandResult.Success(messageToShow, result);
		}
	}
}