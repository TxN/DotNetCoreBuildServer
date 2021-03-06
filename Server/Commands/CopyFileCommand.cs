using System;
using System.IO;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace Server.Commands {
	[Command("copy_file")]
	public class CopyFileCommand:ICommand {
		
		public CommandResult Execute(LoggerFactory loggerFactory, Dictionary<string, string> args) {
			if (args == null) {
				return CommandResult.Fail("No arguments provided!");
			}
			var fromPath = args.Get("from");
			var toPath = args.Get("to");
			if (string.IsNullOrEmpty(fromPath) || string.IsNullOrEmpty(toPath)) {
				return CommandResult.Fail("No pathes provided!");
			}
			var ifExist = args.Get("if_exist");
			try {
				var ifExistValue = string.IsNullOrEmpty(ifExist) || bool.Parse(ifExist);
				if ( !File.Exists(fromPath) ) {
					return ifExistValue ?
						CommandResult.Fail($"File \"{fromPath}\" does not exists!") :
						CommandResult.Success();
				}
				File.Copy(fromPath, toPath, true);
				return CommandResult.Success($"File copied from \"{fromPath}\" to \"{toPath}\".");
			} catch (Exception e) {
				return CommandResult.Fail($"Can't copy file from \"{fromPath}\" to \"{toPath}\": \"{e}\"");
			}
		}
	}
}