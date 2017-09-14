
using System;
using Orleans.Runtime;

namespace Orleans.Transactions
{
    /// <summary>
    /// Orleans ServiceBus error codes
    /// </summary>
    internal enum OrleansTransactionsErrorCode
    {
        /// <summary>
        /// Start of orlean servicebus errocodes
        /// </summary>
        OrleansTransactions = 1 << 17,

        Transactions_IdAllocationFailed = OrleansTransactions + 1,
        Transactions_PrepareFailed      = Transactions_IdAllocationFailed+1,
    }

    internal static class LoggerExtensions
    {
        internal static void Verbose(this Logger logger, OrleansTransactionsErrorCode errorCode, string format, params object[] args)
        {
            logger.Log((int)errorCode, Severity.Verbose, format, args, null);
        }

        internal static void Verbose2(this Logger logger, OrleansTransactionsErrorCode errorCode, string format, params object[] args)
        {
            logger.Log((int)errorCode, Severity.Verbose2, format, args, null);
        }

        internal static void Verbose3(this Logger logger, OrleansTransactionsErrorCode errorCode, string format, params object[] args)
        {
            logger.Log((int)errorCode, Severity.Verbose3, format, args, null);
        }

        internal static void Info(this Logger logger, OrleansTransactionsErrorCode errorCode, string format, params object[] args)
        {
            logger.Log((int)errorCode, Severity.Info, format, args, null);
        }

        internal static void Warn(this Logger logger, OrleansTransactionsErrorCode errorCode, string format, params object[] args)
        {
            logger.Log((int)errorCode, Severity.Warning, format, args, null);
        }

        internal static void Warn(this Logger logger, OrleansTransactionsErrorCode errorCode, string message, Exception exception)
        {
            logger.Log((int)errorCode, Severity.Warning, message, new object[] { }, exception);
        }

        internal static void Error(this Logger logger, OrleansTransactionsErrorCode errorCode, string message, Exception exception = null)
        {
            logger.Log((int)errorCode, Severity.Error, message, new object[] { }, exception);
        }
    }
}
