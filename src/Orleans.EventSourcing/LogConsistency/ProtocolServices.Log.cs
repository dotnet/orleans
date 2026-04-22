using System;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.LogConsistency
{
    internal partial class ProtocolServices
    {
        [LoggerMessage(
            EventId = (int)ErrorCode.LogConsistency_ProtocolError,
            Level = LogLevel.Error,
            Message = "{GrainId} Protocol Error: {Message}"
        )]
        private static partial void LogErrorProtocol(ILogger logger, GrainId grainId, string message);

        [LoggerMessage(
            EventId = (int)ErrorCode.LogConsistency_ProtocolFatalError,
            Level = LogLevel.Error,
            Message = "{GrainId} Protocol Error: {Message}"
        )]
        private static partial void LogErrorProtocolFatal(ILogger logger, GrainId grainId, string message);

        [LoggerMessage(
            EventId = (int)ErrorCode.LogConsistency_CaughtException,
            Level = LogLevel.Error,
            Message = "{GrainId} exception caught at {Location}"
        )]
        private static partial void LogErrorCaughtException(ILogger logger, Exception exception, GrainId grainId, string location);

        [LoggerMessage(
            EventId = (int)ErrorCode.LogConsistency_UserCodeException,
            Level = LogLevel.Warning,
            Message = "{GrainId} exception caught in user code for {Callback}, called from {Location}"
        )]
        private static partial void LogWarningCaughtUserCodeException(ILogger logger, Exception exception, GrainId grainId, string callback, string location);
    }
}
