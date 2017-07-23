﻿using System;
using System.Collections.Generic;
using System.Linq;
using Server.BuildConfig;
using Server.Runtime;

namespace Server.Controllers {
	public class BaseServerController {
		
		protected BuildServer Server = null;

		readonly Dictionary<string, Action<RequestArgs>> _handlers = 
			new Dictionary<string, Action<RequestArgs>>();
		
		protected BaseServerController(BuildServer server) {
			AddHandler("help",   RequestHelp);
			AddHandler("status", RequestStatus);
			AddHandler("build",  StartBuild);
			AddHandler("stop",   StopServer);
			Server = server;
		}
		
		void AddHandler(string name, Action<RequestArgs> handler) {
			_handlers.Add(name, handler);
		}

		void AddHandler(string name, Action handler) {
			_handlers.Add(name, (_) => handler.Invoke());
		}

		void RequestHelp() {
			Server.RequestHelp();
		}
		
		void RequestStatus() {
			Server.RequestStatus();
		}
		
		protected void StartBuild(RequestArgs args) {
			if ((args == null) || (args.Count == 0)) {
				Server.RaiseCommonError("StartBuild: No arguments!");
				return;
			}
			var buildName = args[0];
			Build build = null;
			if (!Server.Builds.TryGetValue(buildName, out build)) {
				Server.RaiseCommonError("StartBuild: Wrong build name!");
				return;
			}
			if (args.Count - 1 < build.Args.Count) {
				Server.RaiseCommonError(
					$"StartBuild: build required {build.Args.Count} args, but {args.Count - 1} args is provided!");
				return;
			}
			Server.InitBuild(build);
			var buildArgs = args.Skip(1).ToArray();
			Server.StartBuild(buildArgs);
		}

		protected void StopServer() {
			Server.StopServer();
		}
		
		protected ServerRequest ConvertMessage(string message) {
			var allParts = message.Split(' ');
			if (allParts.Length <= 0) {
				return ServerRequest.Empty;
			}
			var request = allParts[0];
			var requestArgs = new RequestArgs(allParts.Skip(1));
			return new ServerRequest(request, requestArgs);
		}
		
		protected void Call(ServerRequest request) {
			if (!request.IsValid) {
				_handlers["help"]?.Invoke(request.Args);
				return;
			}
			var handler = _handlers.Get(request.Request);
			handler?.Invoke(request.Args);
		}
	}
}