using System;
using Microsoft.Extensions.Logging;
namespace Orleans.Providers.GCP
{
    internal enum GoogleErrorCode
    {
        GoogleErrorCodeBase = 1 << 24,
        Initializing = GoogleErrorCodeBase + 1,
        DeleteTopic = GoogleErrorCodeBase + 2,
        PublishMessage = GoogleErrorCodeBase + 3,
        GetMessages = GoogleErrorCodeBase + 4,
        DeleteMessage = GoogleErrorCodeBase + 5,
        AcknowledgeMessage = GoogleErrorCodeBase + 6
    }

    internal static class LoggerExtensions
    {
        internal static void Debug(this ILogger logger, GoogleErrorCode errorCode, string format, params object[] args)
        {
            logger.LogDebug((int)errorCode, format, args);
        }

        internal static void Trace(this ILogger logger, GoogleErrorCode errorCode, string format, params object[] args)
        {
            logger.LogTrace((int)errorCode, format, args);
        }

        internal static void Info(this ILogger logger, GoogleErrorCode errorCode, string format, params object[] args)
        {
            logger.LogInformation((int)errorCode, format, args);
        }

        internal static void Warn(this ILogger logger, GoogleErrorCode errorCode, string format, params object[] args)
        {
            logger.LogWarning((int)errorCode, format, args);
        }

        internal static void Warn(this ILogger logger, GoogleErrorCode errorCode, string message, Exception exception)
        {
            logger.LogWarning((int)errorCode, exception, message);
        }

        internal static void Error(this ILogger logger, GoogleErrorCode errorCode, string message, Exception exception = null)
        {
            logger.LogError((int)errorCode, exception, message);
        }
    }
}
