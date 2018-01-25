using System;
using System.Threading;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

namespace Orleans.Transactions
{
    internal class ActiveTransactionsTracker : IDisposable
    {
        private readonly TransactionsOptions options;
        private readonly TransactionLog transactionLog;
        private readonly ILogger logger;
        private readonly object lockObj;
        private readonly Thread allocationThread;
        private readonly AutoResetEvent allocationEvent;

        private long smallestActiveTransactionId;
        private long highestActiveTransactionId;

        private long maxAllocatedTransactionId;
        private volatile bool disposed;

        public ActiveTransactionsTracker(IOptions<TransactionsOptions> configOption, TransactionLog transactionLog, ILoggerFactory loggerFactory)
        {
            this.options = configOption.Value;
            this.transactionLog = transactionLog;
            this.logger = loggerFactory.CreateLogger(nameof(ActiveTransactionsTracker));
            lockObj = new object();

            allocationEvent = new AutoResetEvent(true);
            allocationThread = new Thread(AllocateTransactionId)
            {
                IsBackground = true,
                Name = nameof(ActiveTransactionsTracker)
            };
        }

        public void Start(long initialTransactionId)
        {
            smallestActiveTransactionId = initialTransactionId + 1;
            highestActiveTransactionId = initialTransactionId;
            maxAllocatedTransactionId = initialTransactionId;

            allocationThread.Start();
        }

        public long GetNewTransactionId()
        {
            var id = Interlocked.Increment(ref highestActiveTransactionId);

            if (maxAllocatedTransactionId - highestActiveTransactionId <= options.AvailableTransactionIdThreshold)
            {
                // Signal the allocation thread to allocate more Ids
                allocationEvent.Set();
            }

            while (id > maxAllocatedTransactionId)
            {
                // Wait until the allocation thread catches up before returning.
                // This should never happen if we are pre-allocating fast enough.
                allocationEvent.Set();
                lock (lockObj)
                {
                }
            }

            return id;
        }

        public long GetSmallestActiveTransactionId()
        {
            // NOTE: this result is not strictly correct if there are NO active transactions
            // but for all purposes in which this is used it is still valid.
            // TODO: consider renaming this or handling the no active transactions case.
            return Interlocked.Read(ref smallestActiveTransactionId);
        }

        public long GetHighestActiveTransactionId()
        {
            // NOTE: this result is not strictly correct if there are NO active transactions
            // but for all purposes in which this is used it is still valid.
            // TODO: consider renaming this or handling the no active transactions case.
            lock (lockObj)
            {
                return Math.Min(highestActiveTransactionId, maxAllocatedTransactionId);
            }
        }


        public void PopSmallestActiveTransactionId()
        {
            Interlocked.Increment(ref smallestActiveTransactionId);
        }

        private void AllocateTransactionId(object args)
        {
            while (!this.disposed)
            {
                try
                {
                    allocationEvent.WaitOne();
                    if (this.disposed) return;

                    lock (lockObj)
                    {
                        if (maxAllocatedTransactionId - highestActiveTransactionId <= options.AvailableTransactionIdThreshold)
                        {
                            var batchSize = options.TransactionIdAllocationBatchSize;
                            transactionLog.UpdateStartRecord(maxAllocatedTransactionId + batchSize).GetAwaiter().GetResult();

                            maxAllocatedTransactionId += batchSize;
                        }
                    }
                }
                catch (ThreadAbortException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    this.logger.Warn(
                        OrleansTransactionsErrorCode.Transactions_IdAllocationFailed,
                        "Ignoring exception in " + nameof(this.AllocateTransactionId),
                        exception);
                }
            }
        }

        public void Dispose()
        {
            if (!this.disposed)
            {
                this.disposed = true;
                this.allocationEvent.Set();
                this.allocationEvent.Dispose();
            }
        }
    }
}
