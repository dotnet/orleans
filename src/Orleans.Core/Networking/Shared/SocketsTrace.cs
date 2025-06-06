using System;
using Microsoft.Extensions.Logging;

namespace Orleans.Networking.Shared
{
    internal partial class SocketsTrace : ISocketsTrace
    {
        // ConnectionRead: Reserved: 3
        private readonly ILogger _logger;

        public SocketsTrace(ILogger logger)
        {
            _logger = logger;
        }

        public void ConnectionRead(string connectionId, int count)
        {
            // Don't log for now since this could be *too* verbose.
            // Reserved: Event ID 3
        }

        [LoggerMessage(
            EventId = 6,
            Level = LogLevel.Debug,
            Message = @"Connection id ""{ConnectionId}"" received FIN."
        )]
        public partial void ConnectionReadFin(string connectionId);

        [LoggerMessage(
            EventId = 7,
            Level = LogLevel.Debug,
            Message = @"Connection id ""{ConnectionId}"" sending FIN because: ""{Reason}"""
        )]
        public partial void ConnectionWriteFin(string connectionId, string reason);

        public void ConnectionWrite(string connectionId, int count)
        {
            // Don't log for now since this could be *too* verbose.
            // Reserved: Event ID 11
        }

        public void ConnectionWriteCallback(string connectionId, int status)
        {
            // Don't log for now since this could be *too* verbose.
            // Reserved: Event ID 12
        }

        [LoggerMessage(
            EventId = 13,
            Level = LogLevel.Debug,
            Message = @"Connection id ""{ConnectionId}"" sending FIN."
        )]
        public partial void ConnectionError(string connectionId, Exception ex);

        [LoggerMessage(
            EventId = 19,
            Level = LogLevel.Debug,
            Message = @"Connection id ""{ConnectionId}"" reset."
        )]
        public partial void ConnectionReset(string connectionId);

        [LoggerMessage(
            EventId = 4,
            Level = LogLevel.Debug,
            Message = @"Connection id ""{ConnectionId}"" paused."
        )]
        public partial void ConnectionPause(string connectionId);

        [LoggerMessage(
            EventId = 5,
            Level = LogLevel.Debug,
            Message = @"Connection id ""{ConnectionId}"" resumed."
        )]
        public partial void ConnectionResume(string connectionId);

        public IDisposable BeginScope<TState>(TState state) => _logger.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => _logger.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            => _logger.Log(logLevel, eventId, state, exception, formatter);
    }
}
