using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions
{
    /// <summary>
    /// This class represents the durable Transaction Log.
    /// Orleans Transaction Log has 2 types of entries:
    ///     1- StartRecord: There is exactly 1 entry of this type in the log. It logs the
    ///         number of started transactions so far.
    ///     2- CommitRecord: An entry is appended to the log when a transaction commits.
    ///     
    /// Usage:
    /// The log can be in 2 modes.
    ///     1- When first initialized the log is in Recovery Mode. In this mode the client calls
    ///         GetFirstCommitRecord followed by a sequence of GetNextCommitRecord() calls to 
    ///         retrieve the log entries. Finally the client calls EndRecovery().
    ///     2- The log becomes in Append Mode after the call to EndRecovery().
    ///         This is the normal mode of operation in which the caller can modify the log by
    ///         appending entries and removing entries that are no longer necessary.
    /// </summary>
    internal class TransactionLog
    {
        private readonly Factory<Task<ITransactionLogStorage>> storageFactory;
        private ITransactionLogStorage transactionLogStorage;

        private TransactionLogOperatingMode currentLogMode;

        private long lastStartRecordValue;

        public TransactionLog(Factory<Task<ITransactionLogStorage>> storageFactory)
        {
            this.storageFactory = storageFactory;

            currentLogMode = TransactionLogOperatingMode.Uninitialized;
        }

        /// <summary>
        /// Initialize the log (in Recovery Mode). This method must be called before any other method
        /// is called on the log.
        /// </summary>
        /// <returns></returns>
        public async Task Initialize()
        {
            currentLogMode = TransactionLogOperatingMode.RecoveryMode;

            transactionLogStorage = await storageFactory();
        }

        /// <summary>
        /// Gets the first CommitRecord in the log.
        /// </summary>
        /// <returns>
        /// The CommitRecord with the lowest LSN in the log, or null if there is none.
        /// </returns>
        public Task<CommitRecord> GetFirstCommitRecord()
        {
            ThrowIfNotInMode(TransactionLogOperatingMode.RecoveryMode);

            return transactionLogStorage.GetFirstCommitRecord();
        }

        /// <summary>
        /// Returns the CommitRecord with LSN following the LSN of record returned by the last
        /// GetFirstcommitRecord() or GetNextCommitRecord() call.
        /// </summary>
        /// <returns>
        /// The next CommitRecord, or null if there is none.
        /// </returns>
        public Task<CommitRecord> GetNextCommitRecord()
        {
            ThrowIfNotInMode(TransactionLogOperatingMode.RecoveryMode);

            return transactionLogStorage.GetNextCommitRecord();
        }

        /// <summary>
        /// Exit recovery and enter Append Mode.
        /// </summary>
        public Task EndRecovery()
        {
            ThrowIfNotInMode(TransactionLogOperatingMode.RecoveryMode);

            currentLogMode = TransactionLogOperatingMode.AppendMode;

            return Task.CompletedTask;
        }

        public async Task<long> GetStartRecord()
        {
            ThrowIfNotInMode(TransactionLogOperatingMode.AppendMode);

            lastStartRecordValue = await transactionLogStorage.GetStartRecord();

            return lastStartRecordValue;
        }

        public Task UpdateStartRecord(long transactionId)
        {
            ThrowIfNotInMode(TransactionLogOperatingMode.AppendMode);

            if (transactionId > lastStartRecordValue)
            {
                return transactionLogStorage.UpdateStartRecord(transactionId);
            }

            throw new InvalidOperationException($"UpdateStartRecord was called in an invalid state. TransactionId: {transactionId}, lastStartRecordValue: {lastStartRecordValue}.");
        }

        /// <summary>
        /// Append the given records to the log in order
        /// </summary>
        /// <param name="commitRecords">Commit Records</param>
        /// <remarks>
        /// If an exception is thrown it is possible that a prefix of the records are persisted
        /// to the log.
        /// </remarks>
        public Task Append(IEnumerable<CommitRecord> commitRecords)
        {
            ThrowIfNotInMode(TransactionLogOperatingMode.AppendMode);


            return transactionLogStorage.Append(commitRecords);
        }

        public Task TruncateLog(long lsn)
        {
            ThrowIfNotInMode(TransactionLogOperatingMode.AppendMode);

            return transactionLogStorage.TruncateLog(lsn);
        }

        private void ThrowIfNotInMode(TransactionLogOperatingMode expectedLogMode)
        {
            if (currentLogMode != expectedLogMode)
            {
                new InvalidOperationException($"Log has to be in {expectedLogMode} mode, but it is in {currentLogMode} mode.");
            }
        }

        private enum TransactionLogOperatingMode
        {
            Uninitialized = 0,
            RecoveryMode,
            AppendMode
        };
    }
}
