using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.Messaging
{
    internal sealed class NetworkingTrace(ILoggerFactory loggerFactory) : DiagnosticListener(typeof(NetworkingTrace).FullName), ILogger
    {
        private readonly ILogger log = loggerFactory.CreateLogger(typeof(NetworkingTrace).FullName);

        public IDisposable BeginScope<TState>(TState state)
        {
            return this.log.BeginScope(state);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsEnabled(LogLevel logLevel)
        {
            return this.log.IsEnabled(logLevel);
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            this.log.Log(logLevel, eventId, state, exception, formatter);
        }
    }
}
