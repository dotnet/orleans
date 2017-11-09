
using System;
using Orleans.Runtime;
using Microsoft.Extensions.Logging;

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
        
        // transaction manager errors
        TransactionManagerErrorCodes = OrleansTransactions + 1000,
        TransactionManager_GroupCommitError = TransactionManagerErrorCodes,
        TransactionManager_CheckpointError = TransactionManager_GroupCommitError + 1,
        TransactionManager_TransactionLogMaintenanceError = TransactionManager_CheckpointError + 1,
        TransactionManager_TransactionLogTruncationError = TransactionManager_TransactionLogMaintenanceError + 1,
    }

    internal static class LoggerExtensions
    {
        internal static void Debug(this ILogger logger, OrleansTransactionsErrorCode errorCode, string format, params object[] args)
        {
            logger.Debug((int)errorCode, format, args, null);
        }

        internal static void Trace(this ILogger logger, OrleansTransactionsErrorCode errorCode, string format, params object[] args)
        {
            logger.Trace((int)errorCode, format, args, null);
        }

        internal static void Info(this ILogger logger, OrleansTransactionsErrorCode errorCode, string format, params object[] args)
        {
            logger.Info((int)errorCode, format, args, null);
        }

        internal static void Warn(this ILogger logger, OrleansTransactionsErrorCode errorCode, string format, params object[] args)
        {
            logger.Warn((int)errorCode, format, args, null);
        }

        internal static void Warn(this ILogger logger, OrleansTransactionsErrorCode errorCode, string message, Exception exception)
        {
            logger.Warn((int)errorCode,  message, new object[] { }, exception);
        }

        internal static void Error(this ILogger logger, OrleansTransactionsErrorCode errorCode, string message, Exception exception = null)
        {
            logger.Error((int)errorCode, message, exception);
        }
    }
}
