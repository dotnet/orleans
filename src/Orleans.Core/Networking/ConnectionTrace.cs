using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Orleans.Connections
{
    internal sealed class ConnectionTrace : DiagnosticListener, ILogger
    {
        private readonly ILogger _log;

        public ConnectionTrace(ILoggerFactory loggerFactory) : base(typeof(ConnectionTrace).FullName!)
        {
            _log = loggerFactory.CreateLogger("Orleans.Connections");
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return _log.BeginScope(state);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsEnabled(LogLevel logLevel)
        {
            return _log.IsEnabled(logLevel);
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _log.Log(logLevel, eventId, state, exception, formatter);
        }
    }
}
