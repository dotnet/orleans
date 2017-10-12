
using System;
using Orleans.Runtime;

namespace Orleans.ServiceBus
{
    /// <summary>
    /// Orleans ServiceBus error codes
    /// </summary>
    internal enum OrleansServiceBusErrorCode
    {
        /// <summary>
        /// Start of orlean servicebus errocodes
        /// </summary>
        ServiceBus = 1<<16,

        FailedPartitionRead = ServiceBus + 1,
        RetryReceiverInit   = ServiceBus + 2,
    }

    internal static class LoggerExtensions
    {
        internal static void Verbose(this Logger logger, OrleansServiceBusErrorCode errorCode, string format, params object[] args)
        {
            logger.Log((int) errorCode, Severity.Verbose, format, args, null);
        }

        internal static void Verbose2(this Logger logger, OrleansServiceBusErrorCode errorCode, string format, params object[] args)
        {
            logger.Log((int) errorCode, Severity.Verbose2, format, args, null);
        }

        internal static void Verbose3(this Logger logger, OrleansServiceBusErrorCode errorCode, string format, params object[] args)
        {
            logger.Log((int) errorCode, Severity.Verbose3, format, args, null);
        }

        internal static void Info(this Logger logger, OrleansServiceBusErrorCode errorCode, string format, params object[] args)
        {
            logger.Log((int) errorCode, Severity.Info, format, args, null);
        }

        internal static void Warn(this Logger logger, OrleansServiceBusErrorCode errorCode, string format, params object[] args)
        {
            logger.Log((int) errorCode, Severity.Warning, format, args, null);
        }

        internal static void Warn(this Logger logger, OrleansServiceBusErrorCode errorCode, string message, Exception exception)
        {
            logger.Log((int) errorCode, Severity.Warning, message, new object[] {}, exception);
        }

        internal static void Error(this Logger logger, OrleansServiceBusErrorCode errorCode, string message, Exception exception = null)
        {
            logger.Log((int) errorCode, Severity.Error, message, new object[] {}, exception);
        }
    }
}
