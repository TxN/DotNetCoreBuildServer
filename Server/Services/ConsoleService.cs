using System;
using Server.BuildConfig;
using Server.Controllers;
using Server.Runtime;
using Server.Views;
using Microsoft.Extensions.Logging;

namespace Server.Services {
	public class ConsoleService : IService, IContextService {

		readonly RequestContext _context = new RequestContext("Console");

		public RequestContext Context {
			get {
				return _context;
			}
		}

		LoggerFactory _loggerFactory;
		MessageFormat _messageFormat;

		ConsoleServerController _controller;
		ConsoleServerView       _view;

		public ConsoleService(LoggerFactory loggerFactory, MessageFormat messageFormat) {
			_loggerFactory = loggerFactory;
			_messageFormat = messageFormat;
		}

		public bool TryInit(BuildServer server, Project project) {
			_controller = new ConsoleServerController(_loggerFactory, _context, server);
			_view       = new ConsoleServerView(_loggerFactory, _context, server, _messageFormat);
			_loggerFactory.CreateLogger<ConsoleService>().LogDebug("ConsoleService: initialized");
			return true;
		}

		public void Process() {
			while (_view.Alive) {
				_controller.SendRequest(_context, Console.ReadLine());
			}
		}
	}
}