using System;
using System.IO;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace Server.Commands {
	[Command("delete_dir")]
	public class DeleteDirCommand:ICommand {
		
		public CommandResult Execute(LoggerFactory loggerFactory, Dictionary<string, string> args) {
			if (args == null) {
				return CommandResult.Fail("No arguments provided!");
			}
			var path = args.Get("path");
			if (string.IsNullOrEmpty(path)) {
				return CommandResult.Fail("No path provided!");
			}
			var recursive = args.Get("recursive");
			var ifExist = args.Get("if_exist");
			try {
				var ifExistValue = !string.IsNullOrEmpty(ifExist) && bool.Parse(ifExist);
				if ( !Directory.Exists(path) ) {
					return ifExistValue ?
						CommandResult.Fail($"Directory \"{path}\" does not exists!") :
						CommandResult.Success();
				}
				var recursiveValue = !string.IsNullOrEmpty(recursive) && bool.Parse(recursive);
				Directory.Delete(path, recursiveValue);
				return CommandResult.Success($"Directory \"{path}\" deleted.");
			} catch (Exception e) {
				return CommandResult.Fail($"Can't delete directory at \"{path}\": \"{e}\"");
			}
		}
	}
}