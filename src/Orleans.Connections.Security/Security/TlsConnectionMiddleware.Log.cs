using System;
using Microsoft.Extensions.Logging;

namespace Orleans.Connections.Security
{
    internal partial class TlsServerConnectionMiddleware
    {
        [LoggerMessage(
            EventId = 2,
            Level = LogLevel.Warning,
            Message = "Authentication timed out"
        )]
        private static partial void LogWarningAuthenticationTimedOut(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 1,
            Level = LogLevel.Warning,
            Message = "Authentication failed"
        )]
        private static partial void LogWarningAuthenticationFailed(ILogger logger, Exception exception);
    }

    internal partial class TlsClientConnectionMiddleware
    {
        [LoggerMessage(
            EventId = 2,
            Level = LogLevel.Warning,
            Message = "Authentication timed out"
        )]
        private static partial void LogWarningAuthenticationTimedOut(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 1,
            Level = LogLevel.Warning,
            Message = "Authentication failed"
        )]
        private static partial void LogWarningAuthenticationFailed(ILogger logger, Exception exception);
    }
}
