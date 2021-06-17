using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.State
{
    internal class ReadWriteLock<TState>
       where TState : class, new()
    {
        private readonly TransactionalStateOptions options;
        private readonly TransactionQueue<TState> queue;
        private BatchWorker lockWorker;
        private BatchWorker storageWorker;
        private readonly ILogger logger;
        private readonly IActivationLifetime activationLifetime;

        // the linked list of lock groups
        // the head is the group that is currently holding the lock
        private LockGroup currentGroup = null;

        // cache the last known minimum so we don't have to recompute it as much
        private DateTime cachedMin = DateTime.MaxValue;
        private Guid cachedMinId;

        // group of non-conflicting transactions collectively acquiring/releasing the lock
        private class LockGroup : Dictionary<Guid, TransactionRecord<TState>>
        {
            public int FillCount;
            public List<Action> Tasks; // the tasks for executing the waiting operations
            public LockGroup Next; // queued-up transactions waiting to acquire lock
            public DateTime? Deadline;
            public void Reset()
            {
                FillCount = 0;
                Tasks = null;
                Deadline = null;
                Clear();
            }
        }

        public ReadWriteLock(
            IOptions<TransactionalStateOptions> options,
            TransactionQueue<TState> queue,
            BatchWorker storageWorker,
            ILogger logger,
            IActivationLifetime activationLifetime)
        {
            this.options = options.Value;
            this.queue = queue;
            this.storageWorker = storageWorker;
            this.logger = logger;
            this.activationLifetime = activationLifetime;
            this.lockWorker = new BatchWorkerFromDelegate(LockWork, this.activationLifetime.OnDeactivating);
        }

        public async Task<TResult> EnterLock<TResult>(Guid transactionId, DateTime priority,
                                   AccessCounter counter, bool isRead, Func<TResult> task)
        {
            bool rollbacksOccurred = false;
            List<Task> cleanup = new List<Task>();

            await this.queue.Ready();

            // search active transactions
            if (Find(transactionId, isRead, out var group, out var record))
            {
                // check if we lost some reads or writes already
                if (counter.Reads > record.NumberReads || counter.Writes > record.NumberWrites)
                {
                    throw new OrleansBrokenTransactionLockException(transactionId.ToString(), "when re-entering lock");
                }

                // check if the operation conflicts with other transactions in the group
                if (HasConflict(isRead, priority, transactionId, group, out var resolvable))
                {
                    if (!resolvable)
                    {
                        throw new OrleansTransactionLockUpgradeException(transactionId.ToString());
                    }
                    else
                    {
                        // rollback all conflicts
                        var conflicts = Conflicts(transactionId, group).ToList();

                        if (conflicts.Count > 0)
                        {
                            foreach (var r in conflicts)
                            {
                                cleanup.Add(Rollback(r, true));
                                rollbacksOccurred = true;
                            }
                        }
                    }
                }
            }
            else
            {
                // check if we were supposed to already hold this lock
                if (counter.Reads + counter.Writes > 0)
                {
                    throw new OrleansBrokenTransactionLockException(transactionId.ToString(), "when trying to re-enter lock");
                }

                // update the lock deadline
                if (group == currentGroup)
                {
                    group.Deadline = DateTime.UtcNow + this.options.LockTimeout;

                    if (logger.IsEnabled(LogLevel.Trace))
                        logger.Trace("set lock expiration at {Deadline}", group.Deadline.Value.ToString("o"));
                }

                // create a new record for this transaction
                record = new TransactionRecord<TState>()
                {
                    TransactionId = transactionId,
                    Priority = priority,
                    Deadline = DateTime.UtcNow + this.options.LockAcquireTimeout
                };

                group.Add(transactionId, record);
                group.FillCount++;

                if (logger.IsEnabled(LogLevel.Trace))
                {
                    if (group == currentGroup)
                        logger.Trace($"enter-lock {transactionId} fc={group.FillCount}");
                    else
                        logger.Trace($"enter-lock-queue {transactionId} fc={group.FillCount}");
                }
            }

            var result =
                new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Action completion = () =>
            {
                try
                {
                    result.TrySetResult(task());
                }
                catch (Exception exception)
                {
                    result.TrySetException(exception);
                }
            };

            if (group != currentGroup)
            {
                // task will be executed once its group acquires the lock

                if (group.Tasks == null)
                    group.Tasks = new List<Action>();

                group.Tasks.Add(completion);
            }
            else
            {
                // execute task right now
                completion();
            }

            if (isRead)
            {
                record.AddRead();
            }
            else
            {
                record.AddWrite();
            }

            if (rollbacksOccurred)
            {
                lockWorker.Notify();
            }
            else if (group.Deadline.HasValue)
            {
                lockWorker.Notify(group.Deadline.Value);
            }

            await Task.WhenAll(cleanup);
            return await result.Task;
        }

        public async Task<(TransactionalStatus Status, TransactionRecord<TState> State)> ValidateLock(Guid transactionId, AccessCounter accessCount)
        {
            if (currentGroup == null || !currentGroup.TryGetValue(transactionId, out TransactionRecord<TState> record))
            {
                return (TransactionalStatus.BrokenLock, new TransactionRecord<TState>());
            }
            else if (record.NumberReads != accessCount.Reads
                   || record.NumberWrites != accessCount.Writes)
            {
                await Rollback(transactionId, true);
                return (TransactionalStatus.LockValidationFailed, record);
            }
            else
            {
                return (TransactionalStatus.Ok, record);
            }
        }

        public void Notify()
        {
            this.lockWorker.Notify();
        }

        public bool TryGetRecord(Guid transactionId, out TransactionRecord<TState> record)
        {
            return this.currentGroup.TryGetValue(transactionId, out record);
        }

        public Task AbortExecutingTransactions(Exception exception)
        {
            if (currentGroup != null)
            {
                Task[] pending = currentGroup.Select(g => BreakLock(g.Key, g.Value, exception)).ToArray();
                currentGroup.Reset();
                return Task.WhenAll(pending);
            }
            return Task.CompletedTask;
        }

        private Task BreakLock(Guid transactionId, TransactionRecord<TState> entry, Exception exception)
        {
            if (logger.IsEnabled(LogLevel.Trace))
                logger.Trace("Break-lock for transaction {TransactionId}", transactionId);

            return this.queue.NotifyOfAbort(entry, TransactionalStatus.BrokenLock, exception);
        }

        public void AbortQueuedTransactions()
        {
            var pos = currentGroup?.Next;
            while (pos != null)
            {
                if (pos.Tasks != null)
                {
                    foreach (var t in pos.Tasks)
                    {
                        // running the task will abort the transaction because it is not in currentGroup
                        t();
                    }
                }
                pos.Clear();
                pos = pos.Next;
            }
            if (currentGroup != null)
                currentGroup.Next = null;
        }

        public async Task Rollback(Guid guid, bool notify)
        {
            // no-op if the transaction never happened or already rolled back
            if (currentGroup == null || !currentGroup.TryGetValue(guid, out var record))
            {
                return;
            }

            // remove record for this transaction
            currentGroup.Remove(guid);

            // notify remote listeners
            if (notify)
            {
                await this.queue.NotifyOfAbort(record, TransactionalStatus.BrokenLock, exception: null);
            }
        }

        private async Task LockWork()
        {
            // Stop pumping lock work if this activation is stopping/stopped.
            if (this.activationLifetime.OnDeactivating.IsCancellationRequested) return;
            using (this.activationLifetime.BlockDeactivation())
            {
                var now = DateTime.UtcNow;

                if (currentGroup != null)
                {
                    // check if there are any group members that are ready to exit the lock
                    if (currentGroup.Count > 0)
                    {
                        if (LockExits(out var single, out var multiple))
                        {
                            if (single != null)
                            {
                                await this.queue.EnqueueCommit(single);
                            }
                            else if (multiple != null)
                            {
                                foreach (var r in multiple)
                                {
                                    await this.queue.EnqueueCommit(r);
                                }
                            }

                            lockWorker.Notify();
                            storageWorker.Notify();
                        }

                        else if (currentGroup.Deadline.HasValue)
                        {
                            if (currentGroup.Deadline.Value < now)
                            {
                                // the lock group has timed out.
                                string txlist = string.Join(",", currentGroup.Keys.Select(g => g.ToString()));
                                TimeSpan late = now - currentGroup.Deadline.Value;
                                logger.LogWarning("Break-lock timeout for transactions {TransactionIds}. {Late}ms late", txlist, Math.Floor(late.TotalMilliseconds));
                                await AbortExecutingTransactions(exception: null);
                                lockWorker.Notify();
                            }
                            else
                            {
                                if (logger.IsEnabled(LogLevel.Trace))
                                    logger.Trace("recheck lock expiration at {Deadline}", currentGroup.Deadline.Value.ToString("o"));

                                // check again when the group expires
                                lockWorker.Notify(currentGroup.Deadline.Value);
                            }
                        }
                        else
                        {
                            string txlist = string.Join(",", currentGroup.Keys.Select(g => g.ToString()));
                            logger.LogWarning("Deadline not set for transactions {TransactionIds}", txlist);
                        }
                    }

                    else
                    {
                        // the lock is empty, a new group can enter
                        currentGroup = currentGroup.Next;

                        if (currentGroup != null)
                        {
                            currentGroup.Deadline = now + this.options.LockTimeout;

                            // discard expired waiters that have no chance to succeed
                            // because they have been waiting for the lock for a longer timespan than the 
                            // total transaction timeout
                            List<Guid> expiredWaiters = null;
                            foreach (var kvp in currentGroup)
                            {
                                if (now > kvp.Value.Deadline)
                                {
                                    if (expiredWaiters == null)
                                        expiredWaiters = new List<Guid>();
                                    expiredWaiters.Add(kvp.Key);

                                    if (logger.IsEnabled(LogLevel.Trace))
                                        logger.Trace($"expire-lock-waiter {kvp.Key}");
                                }
                            }

                            if (expiredWaiters != null)
                            {
                                foreach (var guid in expiredWaiters)
                                {
                                    currentGroup.Remove(guid);
                                }
                            }

                            if (logger.IsEnabled(LogLevel.Trace))
                            {
                                logger.Trace($"lock groupsize={currentGroup.Count} deadline={currentGroup.Deadline:o}");
                                foreach (var kvp in currentGroup)
                                    logger.Trace($"enter-lock {kvp.Key}");
                            }

                            // execute all the read and update tasks
                            if (currentGroup.Tasks != null)
                            {
                                foreach (var t in currentGroup.Tasks)
                                {
                                    t();
                                }
                            }

                            lockWorker.Notify();
                        }
                    }
                }
            }
        }
       
        private bool Find(Guid guid, bool isRead, out LockGroup group, out TransactionRecord<TState> record)
        {
            if (currentGroup == null)
            {
                group = currentGroup = new LockGroup();
                record = null;
                return false;
            }
            else
            {
                group = null;
                var pos = currentGroup;

                while (true)
                {
                    if (pos.TryGetValue(guid, out record))
                    {
                        group = pos;
                        return true;
                    }

                    // if we have not found a place to insert this op yet, and there is room, and no conflicts, use this one
                    if (group == null
                        && pos.FillCount < this.options.MaxLockGroupSize
                        && !HasConflict(isRead, DateTime.MaxValue, guid, pos, out _))
                    {
                        group = pos;
                    }

                    if (pos.Next == null) // we did not find this tx.
                    {
                        // add a new empty group to insert this tx, if we have not found one yet
                        if (group == null)
                        {
                            group = pos.Next = new LockGroup();
                        }

                        return false;
                    }

                    pos = pos.Next;
                }
            }
        }

        private bool HasConflict(bool isRead, DateTime priority, Guid transactionId, LockGroup group, out bool resolvable)
        {
            bool foundResolvableConflicts = false;

            foreach (var kvp in group)
            {
                if (kvp.Key != transactionId)
                {
                    if (isRead && kvp.Value.NumberWrites == 0)
                    {
                        continue;
                    }
                    else
                    {
                        if (priority > kvp.Value.Priority)
                        {
                            resolvable = false;
                            return true;
                        }
                        else
                        {
                            foundResolvableConflicts = true;
                        }
                    }
                }
            }

            resolvable = foundResolvableConflicts;
            return foundResolvableConflicts;
        }

        private IEnumerable<Guid> Conflicts(Guid transactionId, LockGroup group)
        {
            foreach (var kvp in group)
            {
                if (kvp.Key != transactionId)
                {
                    yield return kvp.Key;
                }
            }
        }

        private bool LockExits(out TransactionRecord<TState> single, out List<TransactionRecord<TState>> multiple)
        {
            single = null;
            multiple = null;

            // fast-path the one-element case
            if (currentGroup.Count == 1)
            {
                var kvp = currentGroup.First();
                if (kvp.Value.Role == CommitRole.NotYetDetermined) // has not received commit from TA
                {
                    return false;
                }
                else
                {
                    single = kvp.Value;

                    currentGroup.Remove(single.TransactionId);

                    if (logger.IsEnabled(LogLevel.Debug))
                        logger.Debug($"exit-lock {single.TransactionId} {single.Timestamp:o}");

                    return true;
                }
            }
            else
            {
                // find the current minimum, if we don't have a valid cache of it
                if (cachedMin == DateTime.MaxValue
                    || !currentGroup.TryGetValue(cachedMinId, out var record)
                    || record.Role != CommitRole.NotYetDetermined
                    || record.Timestamp != cachedMin)
                {
                    cachedMin = DateTime.MaxValue;
                    foreach (var kvp in currentGroup)
                    {
                        if (kvp.Value.Role == CommitRole.NotYetDetermined) // has not received commit from TA
                        {
                            if (cachedMin > kvp.Value.Timestamp)
                            {
                                cachedMin = kvp.Value.Timestamp;
                                cachedMinId = kvp.Key;
                            }
                        }
                    }
                }

                // find released entries
                foreach (var kvp in currentGroup)
                {
                    if (kvp.Value.Role != CommitRole.NotYetDetermined) // ready to commit
                    {
                        if (kvp.Value.Timestamp < cachedMin)
                        {
                            if (multiple == null)
                            {
                                multiple = new List<TransactionRecord<TState>>();
                            }
                            multiple.Add(kvp.Value);
                        }
                    }
                }

                if (multiple == null)
                {
                    return false;
                }
                else
                {
                    multiple.Sort(Comparer);

                    for (int i = 0; i < multiple.Count; i++)
                    {
                        currentGroup.Remove(multiple[i].TransactionId);

                        if (logger.IsEnabled(LogLevel.Debug))
                            logger.Debug($"exit-lock ({i}/{multiple.Count}) {multiple[i].TransactionId} {multiple[i].Timestamp:o}");
                    }

                    return true;
                }
            }
        }

        private static int Comparer(TransactionRecord<TState> a, TransactionRecord<TState> b)
        {
            return a.Timestamp.CompareTo(b.Timestamp);
        }
    }
}
