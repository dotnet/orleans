using System;
using Microsoft.Extensions.Logging;

namespace Orleans.Streaming.EventHubs
{
    /// <summary>
    /// Orleans ServiceBus error codes
    /// </summary>
    internal enum OrleansEventHubErrorCode
    {
        /// <summary>
        /// Start of orlean servicebus error codes
        /// </summary>
        ServiceBus = 1<<16,

        FailedPartitionRead = ServiceBus + 1,
        RetryReceiverInit   = ServiceBus + 2,
    }

    internal static class LoggerExtensions
    {
        internal static void Debug(this ILogger logger, OrleansEventHubErrorCode errorCode, string format, params object[] args)
        {
            logger.LogDebug((int)errorCode, format, args);
        }

        internal static void Trace(this ILogger logger, OrleansEventHubErrorCode errorCode, string format, params object[] args)
        {
            logger.LogTrace((int)errorCode, format, args);
        }

        internal static void Info(this ILogger logger, OrleansEventHubErrorCode errorCode, string format, params object[] args)
        {
            logger.LogInformation((int)errorCode, format, args);
        }

        internal static void Warn(this ILogger logger, OrleansEventHubErrorCode errorCode, string format, params object[] args)
        {
            logger.LogWarning((int)errorCode, format, args);
        }

        internal static void Warn(this ILogger logger, OrleansEventHubErrorCode errorCode, string message, Exception exception)
        {
            logger.LogWarning((int)errorCode, exception, message);
        }

        internal static void Error(this ILogger logger, OrleansEventHubErrorCode errorCode, string message, Exception exception = null)
        {
            logger.LogError((int)errorCode, exception, message);
        }
    }
}
