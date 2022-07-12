using Microsoft.Extensions.Logging;
using Orleans.Reminders;
using System;

namespace Orleans.Runtime
{
    /// <summary>
    /// Extension methods which preserves legacy orleans log methods style
    /// </summary>
    public static class OrleansRemindersLoggerExtension
    {
        public static void Debug(this ILogger logger, RSErrorCode logCode, string format, params object[] args)
        {
            logger.LogDebug(new EventId((int)logCode), format, args);
        }

        public static void Debug(this ILogger logger, RSErrorCode logCode, string message)
        {
            logger.LogDebug(new EventId((int)logCode), message);
        }

        public static void Trace(this ILogger logger, RSErrorCode logCode, string format, params object[] args)
        {
            logger.LogTrace(new EventId((int)logCode), format, args);
        }

        public static void Trace(this ILogger logger, RSErrorCode logCode, string message)
        {
            logger.LogTrace(new EventId((int)logCode), message);
        }

        public static void Info(this ILogger logger, RSErrorCode logCode, string format, params object[] args)
        {
            logger.LogInformation(new EventId((int)logCode), format, args);
        }

        public static void Info(this ILogger logger, RSErrorCode logCode, string message)
        {
            logger.LogInformation(new EventId((int)logCode), message);
        }

        public static void Warn(this ILogger logger, RSErrorCode logCode, string format, params object[] args)
        {
            logger.LogWarning(new EventId((int)logCode), format, args);
        }

        public static void Warn(this ILogger logger, RSErrorCode logCode, string message, Exception exception = null)
        {
            logger.LogWarning(new EventId((int)logCode), exception, message);
        }

        public static void Error(this ILogger logger, RSErrorCode logCode, string message, Exception exception = null)
        {
            logger.LogError(new EventId((int)logCode), exception, message);
        }
    }
}