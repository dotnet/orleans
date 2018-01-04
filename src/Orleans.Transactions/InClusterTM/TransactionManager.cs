using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Threading;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;
using Orleans.Runtime.Configuration;

namespace Orleans.Transactions
{ 
    public class TransactionManager : ITransactionManager
    {
        private const int MaxCheckpointBatchSize = 200;
        private static readonly TimeSpan DefaultLogMaintenanceInterval = TimeSpan.FromSeconds(1);

        private readonly TransactionsOptions options;
        private readonly TransactionLog transactionLog;
        private readonly ActiveTransactionsTracker activeTransactionsTracker;
        private readonly TimeSpan logMaintenanceInterval;

        // Index of transactions by transactionId.
        private readonly ConcurrentDictionary<long, Transaction> transactionsTable;

        // Transactions that have validated dependencies.
        private readonly ConcurrentQueue<Transaction> dependencyQueue;

        // Transactions that are waiting on group commit.
        private readonly ConcurrentQueue<Tuple<CommitRecord, Transaction>> groupCommitQueue;

        // Queue of committed transactions in commit order.
        private readonly ConcurrentQueue<Transaction> checkpointQueue;
        private readonly Queue<Transaction> checkpointRetryQueue = new Queue<Transaction>(MaxCheckpointBatchSize);

        private readonly InterlockedExchangeLock dependencyLock;
        private readonly InterlockedExchangeLock commitLock;
        private readonly InterlockedExchangeLock checkpointLock;
        private readonly Dictionary<ITransactionalResource, long> resources;
        private readonly List<Transaction> transactions;

        private long checkpointedLSN;

        protected readonly ILogger logger;
        private bool IsRunning;
        private Task transactionLogMaintenanceTask;
        private TransactionManagerMetrics metrics;
        public TransactionManager(TransactionLog transactionLog, IOptions<TransactionsOptions> configOption, ILoggerFactory loggerFactory, ITelemetryProducer telemetryProducer,
            Factory<NodeConfiguration> getNodeConfig, TimeSpan? logMaintenanceInterval = null)
        {
            this.transactionLog = transactionLog;
            this.options = configOption.Value;
            this.logger = loggerFactory.CreateLogger<TransactionManager>();
            this.logMaintenanceInterval = logMaintenanceInterval ?? DefaultLogMaintenanceInterval;

            activeTransactionsTracker = new ActiveTransactionsTracker(configOption, this.transactionLog, loggerFactory);

            transactionsTable = new ConcurrentDictionary<long, Transaction>(2, 1000000);

            dependencyQueue = new ConcurrentQueue<Transaction>();
            groupCommitQueue = new ConcurrentQueue<Tuple<CommitRecord, Transaction>>();
            checkpointQueue = new ConcurrentQueue<Transaction>();

            this.dependencyLock = new InterlockedExchangeLock();
            this.commitLock = new InterlockedExchangeLock();
            this.checkpointLock = new InterlockedExchangeLock();
            this.resources = new Dictionary<ITransactionalResource, long>();
            this.transactions = new List<Transaction>();
            this.metrics =
                new TransactionManagerMetrics(telemetryProducer, getNodeConfig().StatisticsMetricsTableWriteInterval);
            this.checkpointedLSN = 0;
            this.IsRunning = false;
        }

        #region ITransactionManager

        public async Task StartAsync()
        {
            await transactionLog.Initialize();
            CommitRecord record = await transactionLog.GetFirstCommitRecord();
            long prevLSN = 0;
            while (record != null)
            {
                Transaction tx = new Transaction(record.TransactionId)
                {
                    State = TransactionState.Committed,
                    LSN = record.LSN,
                    Info = new TransactionInfo(record.TransactionId)
                };

                if (prevLSN == 0)
                {
                    checkpointedLSN = record.LSN - 1;
                }
                prevLSN = record.LSN;

                foreach (var resource in record.Resources)
                {
                    tx.Info.WriteSet.Add(resource, 1);
                }

                transactionsTable[record.TransactionId] = tx;
                checkpointQueue.Enqueue(tx);
                this.SignalCheckpointEnqueued();

                record = await transactionLog.GetNextCommitRecord();
            }

            await transactionLog.EndRecovery();
            var maxAllocatedTransactionId = await transactionLog.GetStartRecord();

            activeTransactionsTracker.Start(maxAllocatedTransactionId);

            this.BeginDependencyCompletionLoop();
            this.BeginGroupCommitLoop();
            this.BeginCheckpointLoop();

            this.IsRunning = true;
            this.transactionLogMaintenanceTask = MaintainTransactionLog();
            this.transactionLogMaintenanceTask.Ignore(); // protect agains unhandled exception in unexpected cases.
        }

        public async Task StopAsync()
        {
            this.IsRunning = false;
            if (this.transactionLogMaintenanceTask != null)
            {
                await this.transactionLogMaintenanceTask;
            }
            this.activeTransactionsTracker.Dispose();
        }

        public long StartTransaction(TimeSpan timeout)
        {
            this.metrics.StartTransactionRequestCounter++;
            var transactionId = activeTransactionsTracker.GetNewTransactionId();
            Transaction tx = new Transaction(transactionId)
            {
                State = TransactionState.Started,
                ExpirationTime = DateTime.UtcNow.Ticks + timeout.Ticks,
            };

            transactionsTable[transactionId] = tx;

            return tx.TransactionId;
        }

        public void AbortTransaction(long transactionId, OrleansTransactionAbortedException reason)
        {
            this.metrics.AbortTransactionRequestCounter++;
            this.metrics.AbortedTransactionCounter++;
            if(this.logger.IsEnabled(LogLevel.Debug)) this.logger.LogDebug($"Abort transaction {transactionId} due to reason {reason}");
            if (transactionsTable.TryGetValue(transactionId, out Transaction tx))
            {
                bool justAborted = false;

                lock (tx)
                {
                    if (tx.State == TransactionState.Started ||
                        tx.State == TransactionState.PendingDependency)
                    {
                        tx.State = TransactionState.Aborted;
                        justAborted = true;
                    }
                }

                if (justAborted)
                {
                    foreach (var waiting in tx.WaitingTransactions)
                    {
                        var cascading = new OrleansCascadingAbortException(waiting.Info.TransactionId.ToString(), tx.TransactionId.ToString());
                        AbortTransaction(waiting.Info.TransactionId, cascading);
                    }

                    tx.CompletionTimeUtc = DateTime.UtcNow;
                    tx.AbortingException = reason;
                }
            }
        }

        public void CommitTransaction(TransactionInfo transactionInfo)
        {
            this.metrics.CommitTransactionRequestCounter++;
            if (transactionsTable.TryGetValue(transactionInfo.TransactionId, out Transaction tx))
            {
                bool abort = false;
                long cascadingDependentId = 0;

                bool pending = false;
                bool signal = false;
                lock (tx)
                {
                    if (tx.State == TransactionState.Started)
                    {
                        tx.Info = transactionInfo;

                        // Check our dependent transactions.
                        // - If all dependent transactions committed, put transaction in validating queue 
                        //   (dependencyQueue)
                        // - If at least one dependent transaction aborted, abort.
                        // - If at least one dependent transaction is still pending, put in pending queue
                        //   (dependentTx.WaitingTransactions)
                        foreach (var dependentId in tx.Info.DependentTransactions)
                        {
                            // Transaction does not exist in the transaction table; 
                            // therefore, presumed abort.
                            if (!transactionsTable.TryGetValue(dependentId, out Transaction dependentTx))
                            {
                                abort = true;
                                if(this.logger.IsEnabled(LogLevel.Debug)) this.logger.LogDebug($"Will abort transaction {transactionInfo.TransactionId} because it doesn't exist in the transaction table");
                                cascadingDependentId = dependentId;
                                this.metrics.AbortedTransactionDueToMissingInfoInTransactionTableCounter++;
                                break;
                            }

                            // NOTE: our deadlock prevention mechanism ensures that we are acquiring
                            // the locks in proper order and there is no risk of deadlock.
                            lock (dependentTx)
                            {
                                // Dependent transactions has aborted; therefore, abort.
                                if (dependentTx.State == TransactionState.Aborted)
                                {
                                    abort = true;
                                    if(this.logger.IsEnabled(LogLevel.Debug)) this.logger.LogDebug($"Will abort transaction {transactionInfo.TransactionId} because one of its dependent transaction {dependentTx.TransactionId} has aborted");
                                    cascadingDependentId = dependentId;
                                    this.metrics.AbortedTransactionDueToDependencyCounter++;
                                    break;
                                }

                                // Dependent transaction is still executing or has a pending dependency.
                                if (dependentTx.State == TransactionState.Started ||
                                    dependentTx.State == TransactionState.PendingDependency)
                                {
                                    pending = true;
                                    dependentTx.WaitingTransactions.Add(tx);
                                    tx.PendingCount++;
                                }
                            }
                        }

                        if (abort)
                        {
                            AbortTransaction(transactionInfo.TransactionId, new OrleansCascadingAbortException(transactionInfo.TransactionId.ToString(), cascadingDependentId.ToString()));
                        }
                        else if (pending)
                        {
                            tx.State = TransactionState.PendingDependency;
                        }
                        else
                        {
                            tx.State = TransactionState.Validated;
                            dependencyQueue.Enqueue(tx);
                            signal = true;
                        }
                    }

                }
                if (signal)
                {
                    this.SignalDependencyEnqueued();
                }
            }
            else
            {
                // Don't have a record of the transaction any more so presumably it's aborted.
                throw new OrleansTransactionAbortedException(transactionInfo.TransactionId.ToString(), "Transaction presumed to be aborted");
            }
        }

        public TransactionStatus GetTransactionStatus(long transactionId, out OrleansTransactionAbortedException abortingException)
        {
            abortingException = null;
            if (transactionsTable.TryGetValue(transactionId, out Transaction tx))
            {
                if (tx.State == TransactionState.Aborted)
                {
                    lock (tx)
                    {
                        abortingException = tx.AbortingException;
                    }
                    return TransactionStatus.Aborted;
                }
                else if (tx.State == TransactionState.Committed || tx.State == TransactionState.Checkpointed)
                {
                    return TransactionStatus.Committed;
                }
                else
                {
                    return TransactionStatus.InProgress;
                }
            }
            return TransactionStatus.Unknown;
        }

        public long GetReadOnlyTransactionId()
        {
            long readId = activeTransactionsTracker.GetSmallestActiveTransactionId();
            if (readId > 0)
            {
                readId--;
            }
            return readId;
        }

        #endregion

        private void BeginDependencyCompletionLoop()
        {
            BeginDependencyCompletionLoopAsync().Ignore();
        }

        private void BeginGroupCommitLoop()
        {
            BeginGroupCommitLoopAsync().Ignore();
        }

        private void BeginCheckpointLoop()
        {
            BeginCheckpointLoopAsync().Ignore();
        }

        private void SignalDependencyEnqueued()
        {
            BeginDependencyCompletionLoop();
        }

        private void SignalGroupCommitEnqueued()
        {
            BeginGroupCommitLoop();
        }

        private void SignalCheckpointEnqueued()
        {
            BeginCheckpointLoop();
        }

        private async Task BeginDependencyCompletionLoopAsync()
        {
            bool gotLock = false;
            try
            {
                if (!(gotLock = dependencyLock.TryGetLock()))
                {
                    return;
                }

                while (this.CheckDependenciesCompleted())
                {
                    // force yield thread
                    await Task.Delay(TimeSpan.FromTicks(1));
                }
            }
            finally
            {
                if (gotLock)
                    dependencyLock.ReleaseLock();
            }
        }

        private async Task BeginGroupCommitLoopAsync()
        {
            bool gotLock = false;
            try
            {
                if (!(gotLock = commitLock.TryGetLock()))
                {
                    return;
                }

                while (await this.GroupCommit())
                {
                    // force yield thread
                    await Task.Delay(TimeSpan.FromTicks(1));
                }
            }
            finally
            {
                if (gotLock)
                    commitLock.ReleaseLock();
            }
        }

        private async Task BeginCheckpointLoopAsync()
        {
            bool gotLock = false;
            try
            {
                if (!(gotLock = checkpointLock.TryGetLock()))
                {
                    return;
                }

                while (await this.Checkpoint(resources, transactions))
                {
                }
            }
            finally
            {
                if (gotLock)
                    checkpointLock.ReleaseLock();
            }
        }

        private bool CheckDependenciesCompleted()
        {
            bool processed = false;
            while (dependencyQueue.TryDequeue(out Transaction tx))
            {
                processed = true;
                CommitRecord commitRecord = new CommitRecord
                {
                    TransactionId = tx.TransactionId
                };

                foreach (var resource in tx.Info.WriteSet.Keys)
                {
                    commitRecord.Resources.Add(resource);
                }
                groupCommitQueue.Enqueue(new Tuple<CommitRecord, Transaction>(commitRecord, tx));
                this.SignalGroupCommitEnqueued();

                // We don't need to hold the transaction lock any more to access
                // the WaitingTransactions set, since nothing can be added to it
                // after this point.

                // If a transaction is waiting on us, decrement their waiting count;
                //  - if they are no longer waiting, mark as validated.
                // TODO: Can't we clear WaitingTransactions here and no longer track it?
                foreach (var waiting in tx.WaitingTransactions)
                {
                    bool signal = false;
                    lock (waiting)
                    {
                        if (waiting.State != TransactionState.Aborted)
                        {
                            waiting.PendingCount--;

                            if (waiting.PendingCount == 0)
                            {
                                waiting.State = TransactionState.Validated;
                                dependencyQueue.Enqueue(waiting);
                                signal = true;
                            }
                        }
                    }
                    if (signal)
                    {
                        this.SignalDependencyEnqueued();
                    }
                }
            }

            return processed;
        }

        private async Task<bool> GroupCommit()
        {
            bool processed = false;
            int batchSize = groupCommitQueue.Count;
            List<CommitRecord> records = new List<CommitRecord>(batchSize);
            List<Transaction> transactions = new List<Transaction>(batchSize);
            while (batchSize > 0)
            {
                processed = true;
                if (groupCommitQueue.TryDequeue(out Tuple<CommitRecord, Transaction> t))
                {
                    records.Add(t.Item1);
                    transactions.Add(t.Item2);
                    batchSize--;
                }
                else
                {
                    break;
                }

            }

            try
            {
                await transactionLog.Append(records);
            }
            catch (Exception e)
            {
                this.logger.Error(OrleansTransactionsErrorCode.TransactionManager_GroupCommitError, "Group Commit error", e);
                // Failure to get an acknowledgment of the commits from the log (e.g. timeout exception)
                // will put the transactions in doubt. We crash and let this be handled in recovery.
                // TODO: handle other exceptions more gracefuly
                throw;

            }

            for (int i = 0; i < transactions.Count; i++)
            {
                var transaction = transactions[i];
                lock (transaction)
                {
                    transaction.State = TransactionState.Committed;
                    transaction.LSN = records[i].LSN;
                    transaction.CompletionTimeUtc = DateTime.UtcNow;
                }
                checkpointQueue.Enqueue(transaction);
                this.SignalCheckpointEnqueued();
            }

            return processed;
        }

        private async Task<bool> Checkpoint(Dictionary<ITransactionalResource, long> resources, List<Transaction> transactions)
        {
            // Rather than continue processing forever, only process the number of transactions which were waiting at invocation time.
            var total = this.checkpointRetryQueue.Count + checkpointQueue.Count;

            // The maximum number of transactions checkpointed in each batch.
            int batchSize = Math.Min(total, MaxCheckpointBatchSize);
            long lsn = 0;

            try
            {
                var processed = 0;
                while (processed < total && (this.checkpointRetryQueue.Count > 0 || !checkpointQueue.IsEmpty))
                {
                    resources.Clear();
                    transactions.Clear();

                    // Take a batch of transactions to checkpoint.
                    var currentBatchSize = 0;
                    while (currentBatchSize < batchSize)
                    {
                        currentBatchSize++;
                        Transaction tx;

                        // If some previous operation had failed, retry it before proceeding with new work.
                        if (this.checkpointRetryQueue.Count > 0) tx = this.checkpointRetryQueue.Dequeue();
                        else if (!checkpointQueue.TryDequeue(out tx)) break;

                        foreach (var resource in tx.Info.WriteSet.Keys)
                        {
                            resources[resource] = tx.Info.TransactionId;
                        }

                        lsn = Math.Max(lsn, tx.LSN);
                        transactions.Add(tx);
                    }

                    processed += currentBatchSize;

                    // If the transaction involved writes, send a commit notification to each resource which performed
                    // a write and wait for acknowledgement.
                    if (resources.Count > 0)
                    {
                        // Send commit notifications to all of the resources involved.
                        var completion = new MultiCompletionSource(resources.Count);
                        foreach (var resource in resources)
                        {
                            NotifyResourceOfCommit(resource.Key, resource.Value, completion);
                        }

                        // Wait for the commit notifications to be acknowledged by the resources.
                        await completion.Task;
                    }

                    // Mark the transactions as checkpointed.
                    foreach (var tx in transactions)
                    {
                        lock (tx)
                        {
                            tx.State = TransactionState.Checkpointed;
                            tx.HighestActiveTransactionIdAtCheckpoint = activeTransactionsTracker.GetHighestActiveTransactionId();
                        }
                    }

                    // Allow the transaction log to be truncated.
                    this.checkpointedLSN = lsn;
                }
            }
            catch (Exception e)
            {
                // Retry all failed checkpoint operations.
                foreach (var tx in transactions) this.checkpointRetryQueue.Enqueue(tx);

                this.logger.Error(OrleansTransactionsErrorCode.TransactionManager_CheckpointError, "Failure during checkpoint", e);
                throw;
            }

            return total > 0;
        }

        private static void NotifyResourceOfCommit(ITransactionalResource resource, long transaction, MultiCompletionSource completionSource)
        {
            resource.Commit(transaction)
                    .ContinueWith(
                        (result, state) =>
                        {
                            var completion = (MultiCompletionSource)state;
                            if (result.Exception != null) completion.SetException(result.Exception);
                            else completion.SetOneResult();
                        },
                        completionSource,
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
        }

        private async Task MaintainTransactionLog()
        {
            while(this.IsRunning)
            {
                try
                {
                    await TransactionLogMaintenance();
                } catch(Exception ex)
                {
                    this.logger.Error(OrleansTransactionsErrorCode.TransactionManager_TransactionLogMaintenanceError, $"Error while maintaining transaction log.", ex);
                }
                this.metrics.TryReportMetrics();
                await Task.Delay(this.logMaintenanceInterval);
            }
        }

        private async Task TransactionLogMaintenance()
        {
            //
            // Truncate log
            //
            if (checkpointedLSN > 0)
            {
                try
                {
                    await transactionLog.TruncateLog(checkpointedLSN - 1);
                }
                catch (Exception e)
                {
                    this.logger.Error(OrleansTransactionsErrorCode.TransactionManager_TransactionLogTruncationError, $"Failed to truncate log. LSN: {checkpointedLSN}", e);
                }
            }

            //
            // Timeout expired transactions
            //
            long now = DateTime.UtcNow.Ticks;
            foreach (var txRecord in transactionsTable)
            {
                if (txRecord.Value.State == TransactionState.Started &&
                    txRecord.Value.ExpirationTime < now)
                {
                    AbortTransaction(txRecord.Key, new OrleansTransactionTimeoutException(txRecord.Key.ToString()));
                }
            }

            //
            // Find the oldest active transaction
            //
            long lowestActiveId = activeTransactionsTracker.GetSmallestActiveTransactionId();
            long highestActiveId = activeTransactionsTracker.GetHighestActiveTransactionId();
            while (lowestActiveId <= highestActiveId)
            {

                if (transactionsTable.TryGetValue(lowestActiveId, out Transaction tx))
                {
                    if (tx.State != TransactionState.Aborted &&
                        tx.State != TransactionState.Checkpointed)
                    {
                        break;
                    }
                }

                lowestActiveId++;
                activeTransactionsTracker.PopSmallestActiveTransactionId();
            }

            //
            // Remove transactions that we no longer need to keep a record of from transactions table.
            // a transaction is presumed to be aborted if we try to look it up and it does not exist in the
            // table.
            //
            foreach (var txRecord in transactionsTable)
            {
                if (txRecord.Value.State == TransactionState.Aborted &&
                    txRecord.Value.CompletionTimeUtc + this.options.TransactionRecordPreservationDuration < DateTime.UtcNow)
                {
                    transactionsTable.TryRemove(txRecord.Key, out Transaction temp);
                }
                else if (txRecord.Value.State == TransactionState.Checkpointed)
                {
                    lock (txRecord.Value)
                    {
                        if (txRecord.Value.HighestActiveTransactionIdAtCheckpoint < activeTransactionsTracker.GetSmallestActiveTransactionId() &&
                            txRecord.Value.CompletionTimeUtc + this.options.TransactionRecordPreservationDuration < DateTime.UtcNow)
                        {
                            // The oldest active transaction started after this transaction was checkpointed
                            // so no in progress transaction is going to take a dependency on this transaction
                            // which means we can safely forget about it.

                            // When receiving an arbitrarily delayed message, a transaction that may have committed
                            // will appear to have aborted, causing the delayed transaction to abort.
                            transactionsTable.TryRemove(txRecord.Key, out Transaction temp);
                        }
                    }
                }
            }
        }
    }
}
