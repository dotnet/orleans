using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.Development
{
    /// <summary>
    /// In memory transaction log - for test and development purposes.
    /// NOT FOR PRODUCTION USE.
    /// </summary>
    internal class InMemoryTransactionLogStorage : ITransactionLogStorage
    {
        private static readonly Task<CommitRecord> NullCommitRecordTask = Task.FromResult<CommitRecord>(null);

        private long startRecordValue;

        private readonly List<CommitRecord> log;

        private int lastLogRecordIndex;

        private long nextLogSequenceNumber;

        public InMemoryTransactionLogStorage()
        {
            log = new List<CommitRecord>();

            startRecordValue = 0;
            lastLogRecordIndex = 0;
        }

        public Task<CommitRecord> GetFirstCommitRecord()
        {
            if (log.Count == 0)
            {
                //
                // Initialize LSN here, to be semantically correct with other providers.
                //

                nextLogSequenceNumber = 1;

                return NullCommitRecordTask;
            }

            //
            // If the log has records, then this method should not get called for the in memory provider.
            //

            throw new InvalidOperationException($"GetFirstCommitRecord was called while the log already has {log.Count} records.");
        }

        public Task<CommitRecord> GetNextCommitRecord()
        {
            if (log.Count <= lastLogRecordIndex)
            {
                return NullCommitRecordTask;
            }

            nextLogSequenceNumber++;

            return Task.FromResult(log[lastLogRecordIndex++]);
        }

        public Task<long> GetStartRecord()
        {
            startRecordValue = 50000;

            return Task.FromResult(startRecordValue);
        }

        public Task UpdateStartRecord(long transactionId)
        {
            startRecordValue = transactionId;

            return Task.CompletedTask;
        }

        public Task Append(IEnumerable<CommitRecord> commitRecords)
        {
            lock (this)
            {
                foreach (var commitRecord in commitRecords)
                {
                    commitRecord.LSN = nextLogSequenceNumber++;
                }

                log.AddRange(commitRecords);
            }

            return Task.CompletedTask;
        }

        public Task TruncateLog(long lsn)
        {
            lock (this)
            {
                var itemsToRemove = 0;

                for (itemsToRemove = 0; itemsToRemove < log.Count; itemsToRemove++)
                {
                    if (log[itemsToRemove].LSN > lsn)
                    {
                        break;
                    }
                }

                log.RemoveRange(0, itemsToRemove);
            }

            return Task.CompletedTask;
        }

        public static Factory<Task<ITransactionLogStorage>> Create(IServiceProvider sp)
        {
            return () => Task.FromResult<ITransactionLogStorage>(new InMemoryTransactionLogStorage());
        }
    }
}
