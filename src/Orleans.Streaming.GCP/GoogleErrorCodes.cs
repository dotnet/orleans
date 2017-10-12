using Orleans.Runtime;
using System;

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
        internal static void Verbose(this Logger logger, GoogleErrorCode errorCode, string format, params object[] args)
        {
            logger.Log((int)errorCode, Severity.Verbose, format, args, null);
        }

        internal static void Verbose2(this Logger logger, GoogleErrorCode errorCode, string format, params object[] args)
        {
            logger.Log((int)errorCode, Severity.Verbose2, format, args, null);
        }

        internal static void Verbose3(this Logger logger, GoogleErrorCode errorCode, string format, params object[] args)
        {
            logger.Log((int)errorCode, Severity.Verbose3, format, args, null);
        }

        internal static void Info(this Logger logger, GoogleErrorCode errorCode, string format, params object[] args)
        {
            logger.Log((int)errorCode, Severity.Info, format, args, null);
        }

        internal static void Warn(this Logger logger, GoogleErrorCode errorCode, string format, params object[] args)
        {
            logger.Log((int)errorCode, Severity.Warning, format, args, null);
        }

        internal static void Warn(this Logger logger, GoogleErrorCode errorCode, string message, Exception exception)
        {
            logger.Log((int)errorCode, Severity.Warning, message, new object[] { }, exception);
        }

        internal static void Error(this Logger logger, GoogleErrorCode errorCode, string message, Exception exception = null)
        {
            logger.Log((int)errorCode, Severity.Error, message, new object[] { }, exception);
        }
    }
}
