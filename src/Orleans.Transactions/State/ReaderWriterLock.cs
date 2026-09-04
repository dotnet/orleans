using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.Diagnostics;

namespace Orleans.Transactions.State
{
    internal partial class ReadWriteLock<TState>
       where TState : class, new()
    {
        private readonly TransactionalStateOptions options;
        private readonly TransactionQueue<TState> queue;
        private readonly BatchWorker lockWorker;
        private readonly BatchWorker storageWorker;
        private readonly ILogger logger;
        private readonly IActivationLifetime activationLifetime;

        // the linked list of lock groups
        // the head is the group that is currently holding the lock
        private LockGroup? currentGroup;

        // cache the last known minimum so we don't have to recompute it as much
        private DateTime cachedMin = DateTime.MaxValue;
        private Guid cachedMinId;

        // group of non-conflicting transactions collectively acquiring/releasing the lock
        private class LockGroup : Dictionary<Guid, TransactionRecord<TState>>
        {
            public int FillCount;
            public List<PendingOperation>? PendingOperations;
            public LockGroup? Next; // queued-up transactions waiting to acquire lock
            public DateTime? Deadline;
            public void Reset()
            {
                FillCount = 0;
                PendingOperations = null;
                Deadline = null;
                Clear();
            }
        }

        private readonly record struct PendingOperation(Guid TransactionId, Action Execute, Action Abort);

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

        public async Task<TResult> EnterLock<TResult>(
            Guid transactionId,
            DateTime priority,
            TimeSpan transactionTimeout,
            AccessCounter counter,
            bool isRead,
            bool exclusiveLock,
            Func<TResult> task)
        {
            bool rollbacksOccurred = false;
            List<Task> cleanup = new List<Task>();

            await this.queue.Ready(transactionId);

            // search active transactions
            if (Find(transactionId, isRead && !exclusiveLock, out var group, out var record))
            {
                // check if we lost some reads or writes already
                if (counter.Reads > record.NumberReads || counter.Writes > record.NumberWrites)
                {
                    throw new OrleansBrokenTransactionLockException(transactionId.ToString(), "when re-entering lock");
                }

                // check if the operation conflicts with other transactions in the group
                if (HasConflict(isRead && !exclusiveLock, priority, transactionId, group, out var resolvable))
                {
                    if (!resolvable)
                    {
                        Rollback(transactionId);
                        lockWorker.Notify();
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
                                cleanup.Add(Rollback(r, true, TransactionDiagnosticEvents.LockBreakReason.Conflict));
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

                var now = DateTime.UtcNow;
                var lockTimeout = GetEffectiveLockTimeout(transactionTimeout, this.options.LockTimeout);

                // update the lock deadline
                if (group == currentGroup)
                {
                    var deadline = AddTimeout(now, lockTimeout);
                    if (!group.Deadline.HasValue || group.Deadline.Value < deadline)
                    {
                        group.Deadline = deadline;
                    }
                    LogTraceSetLockExpiration(new(group.Deadline));
                }

                // create a new record for this transaction
                record = new TransactionRecord<TState>()
                {
                    TransactionId = transactionId,
                    Priority = priority,
                    Deadline = AddTimeout(now, this.options.LockAcquireTimeout),
                    LockTimeout = lockTimeout
                };

                group.Add(transactionId, record);
                group.FillCount++;

                if (group == currentGroup)
                    LogTraceEnterLock(transactionId, group.FillCount);
                else
                    LogTraceEnterLockQueue(transactionId, group.FillCount);
            }

            var result =
                new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            void completion()
            {
                try
                {
                    result.TrySetResult(task());
                }
                catch (Exception exception)
                {
                    result.TrySetException(exception);
                }
            }
            void abort() => result.TrySetException(new OrleansCascadingAbortException(transactionId.ToString()));

            if (group != currentGroup)
            {
                // task will be executed once its group acquires the lock

                if (group.PendingOperations == null)
                    group.PendingOperations = new List<PendingOperation>();

                group.PendingOperations.Add(new PendingOperation(transactionId, completion, abort));
            }
            else
            {
                // execute task right now
                completion();
            }

            if (!isRead || exclusiveLock)
            {
                record.IsExclusiveLock = true;
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
            if (currentGroup == null || !currentGroup.TryGetValue(transactionId, out TransactionRecord<TState>? record))
            {
                return (TransactionalStatus.BrokenLock, new TransactionRecord<TState> { TransactionId = transactionId });
            }
            else if (record.NumberReads != accessCount.Reads
                   || record.NumberWrites != accessCount.Writes)
            {
                await Rollback(transactionId, true, TransactionDiagnosticEvents.LockBreakReason.ValidationFailure);
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

        public bool TryGetRecord(Guid transactionId, [NotNullWhen(true)] out TransactionRecord<TState>? record)
        {
            return this.currentGroup!.TryGetValue(transactionId, out record);
        }

        public Task AbortExecutingTransactions(
            Exception? exception,
            TransactionDiagnosticEvents.LockBreakReason reason = TransactionDiagnosticEvents.LockBreakReason.TransactionAbort)
        {
            if (currentGroup != null)
            {
                Task[] pending = currentGroup.Select(g => BreakLock(g.Key, g.Value, exception, reason)).ToArray();
                currentGroup.Reset();
                return Task.WhenAll(pending);
            }
            return Task.CompletedTask;
        }

        private Task BreakLock(
            Guid transactionId,
            TransactionRecord<TState> entry,
            Exception? exception,
            TransactionDiagnosticEvents.LockBreakReason reason)
        {
            LogTraceBreakLock(transactionId);
            TransactionDiagnosticEvents.EmitLockBroken(
                queue.Resource,
                transactionId,
                reason,
                queue.DiagnosticIdentity);
            return this.queue.NotifyOfAbort(entry, TransactionalStatus.BrokenLock, exception);
        }

        public void AbortQueuedTransactions()
        {
            var pos = currentGroup?.Next;
            while (pos != null)
            {
                if (pos.PendingOperations != null)
                {
                    foreach (var operation in pos.PendingOperations)
                    {
                        operation.Abort();
                    }
                    pos.PendingOperations = null;
                }
                pos.Clear();
                pos = pos.Next;
            }
            if (currentGroup != null)
                currentGroup.Next = null;
        }

        public void Rollback(Guid guid)
        {
            TryRemove(guid, out _);
        }

        public Task Rollback(Guid guid, bool notify, TransactionDiagnosticEvents.LockBreakReason reason)
        {
            // no-op if the transaction never happened or already rolled back
            if (!TryRemove(guid, out var record))
            {
                return Task.CompletedTask;
            }

            // notify remote listeners
            if (!notify)
            {
                return Task.CompletedTask;
            }

            TransactionDiagnosticEvents.EmitLockBroken(
                queue.Resource,
                guid,
                reason,
                queue.DiagnosticIdentity);
            return queue.NotifyOfAbort(record, TransactionalStatus.BrokenLock, exception: null);
        }

        private bool TryRemove(Guid transactionId, [NotNullWhen(true)] out TransactionRecord<TState>? record)
        {
            var group = currentGroup;
            while (group != null)
            {
                if (group.Remove(transactionId, out record))
                {
                    if (group != currentGroup)
                    {
                        AbortPendingOperations(group, transactionId);
                    }

                    return true;
                }

                group = group.Next;
            }

            record = null;
            return false;
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
                                TimeSpan late = now - currentGroup.Deadline.Value;
                                LogTraceBreakLockTimeout(new(currentGroup.Keys), Math.Floor(late.TotalMilliseconds));
                                foreach (var transactionId in currentGroup.Keys)
                                {
                                    TransactionDiagnosticEvents.EmitLockExpired(
                                        queue.Resource,
                                        transactionId,
                                        currentGroup.Deadline.Value,
                                        now,
                                        TransactionDiagnosticEvents.LockExpirationKind.HeldLock,
                                        queue.DiagnosticIdentity);
                                }
                                await AbortExecutingTransactions(
                                    exception: null,
                                    reason: TransactionDiagnosticEvents.LockBreakReason.Expired);
                                lockWorker.Notify();
                            }
                            else
                            {
                                LogTraceRecheckLockExpiration(new(currentGroup.Deadline));

                                // check again when the group expires
                                lockWorker.Notify(currentGroup.Deadline.Value);
                            }
                        }
                        else
                        {
                            LogWarningDeadlineNotSet(new(currentGroup.Keys));
                        }
                    }

                    else
                    {
                        // the lock is empty, a new group can enter
                        currentGroup = currentGroup.Next;

                        if (currentGroup != null)
                        {
                            // discard expired waiters that have no chance to succeed
                            // because they have been waiting for the lock for a longer timespan than the
                            // total transaction timeout
                            var expiredWaiters = currentGroup
                                .Where(kvp => now > kvp.Value.Deadline)
                                .Select(kvp => kvp.Key)
                                .ToList();
                            foreach (var transactionId in expiredWaiters)
                            {
                                var deadline = currentGroup[transactionId].Deadline;
                                TransactionDiagnosticEvents.EmitLockExpired(
                                    queue.Resource,
                                    transactionId,
                                    deadline,
                                    now,
                                    TransactionDiagnosticEvents.LockExpirationKind.QueuedWaiter,
                                    queue.DiagnosticIdentity);
                                currentGroup.Remove(transactionId);
                                AbortPendingOperations(currentGroup, transactionId);
                                LogTraceExpireLockWaiter(transactionId);
                            }

                            currentGroup.Deadline = currentGroup.Count == 0
                                ? null
                                : AddTimeout(now, currentGroup.Values.Max(record => record.LockTimeout));

                            LogTraceLockGroupSize(currentGroup.Count, new(currentGroup.Deadline));
                            if (logger.IsEnabled(LogLevel.Trace))
                            {
                                foreach (var kvp in currentGroup)
                                    LogTraceEnterLockKey(kvp.Key);
                            }

                            // execute all the read and update tasks
                            if (currentGroup.PendingOperations != null)
                            {
                                var pendingOperations = currentGroup.PendingOperations;
                                currentGroup.PendingOperations = null;
                                foreach (var operation in pendingOperations)
                                {
                                    operation.Execute();
                                }
                            }

                            lockWorker.Notify();
                        }
                    }
                }
            }
        }

        internal DateTime? CurrentGroupDeadline => currentGroup?.Deadline;

        internal static TimeSpan GetEffectiveLockTimeout(TimeSpan transactionTimeout, TimeSpan configuredLockTimeout)
            => transactionTimeout > configuredLockTimeout ? transactionTimeout : configuredLockTimeout;

        private static DateTime AddTimeout(DateTime now, TimeSpan timeout)
            => timeout >= DateTime.MaxValue - now ? DateTime.MaxValue : now + timeout;

        private static void AbortPendingOperations(LockGroup group, Guid transactionId)
        {
            if (group.PendingOperations == null)
            {
                return;
            }

            for (var i = group.PendingOperations.Count - 1; i >= 0; i--)
            {
                var operation = group.PendingOperations[i];
                if (operation.TransactionId == transactionId)
                {
                    group.PendingOperations.RemoveAt(i);
                    operation.Abort();
                }
            }

            if (group.PendingOperations.Count == 0)
            {
                group.PendingOperations = null;
            }
        }

        private bool Find(Guid guid, bool isRead, out LockGroup group, [NotNullWhen(true)] out TransactionRecord<TState>? record)
        {
            if (currentGroup == null)
            {
                group = currentGroup = new LockGroup();
                record = null;
                return false;
            }
            else
            {
                group = null!;
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

        private static bool HasConflict(bool isRead, DateTime priority, Guid transactionId, LockGroup group, out bool resolvable)
        {
            bool foundResolvableConflicts = false;

            foreach (var kvp in group)
            {
                if (kvp.Key != transactionId)
                {
                    if (isRead && kvp.Value.NumberWrites == 0 && !kvp.Value.IsExclusiveLock)
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

        private static IEnumerable<Guid> Conflicts(Guid transactionId, LockGroup group)
        {
            foreach (var kvp in group)
            {
                if (kvp.Key != transactionId)
                {
                    yield return kvp.Key;
                }
            }
        }

        private bool LockExits(out TransactionRecord<TState>? single, out List<TransactionRecord<TState>>? multiple)
        {
            single = null;
            multiple = null;

            // fast-path the one-element case
            if (currentGroup!.Count == 1)
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
                    LogDebugExitLock(single.TransactionId, new(single.Timestamp));
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
                        LogDebugExitLockProgress(i, multiple.Count, multiple[i].TransactionId, new(multiple[i].Timestamp));
                    }

                    return true;
                }
            }
        }

        private static int Comparer(TransactionRecord<TState> a, TransactionRecord<TState> b)
        {
            return a.Timestamp.CompareTo(b.Timestamp);
        }

        private readonly struct DateTimeLogRecord(DateTime? ts)
        {
            public override string ToString() => ts?.ToString("o") ?? "none";
        }

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Set lock expiration at {Deadline}"
        )]
        private partial void LogTraceSetLockExpiration(DateTimeLogRecord deadline);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Enter-lock {TransactionId} Fill count={FillCount}"
        )]
        private partial void LogTraceEnterLock(Guid transactionId, int fillCount);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Enter-lock-queue {TransactionId} Fill count={FillCount}"
        )]
        private partial void LogTraceEnterLockQueue(Guid transactionId, int fillCount);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Break-lock for transaction {TransactionId}"
        )]
        private partial void LogTraceBreakLock(Guid transactionId);

        private readonly struct TransactionIdsLogRecord(IEnumerable<Guid> guids)
        {
            public override string ToString() => string.Join(",", guids);
        }

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Break-lock timeout for transactions {TransactionIds}. {Late}ms late"
        )]
        private partial void LogTraceBreakLockTimeout(TransactionIdsLogRecord transactionIds, double late);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Recheck lock expiration at {Deadline}"
        )]
        private partial void LogTraceRecheckLockExpiration(DateTimeLogRecord deadline);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Deadline not set for transactions {TransactionIds}"
        )]
        private partial void LogWarningDeadlineNotSet(TransactionIdsLogRecord transactionIds);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Expire-lock-waiter {Key}"
        )]
        private partial void LogTraceExpireLockWaiter(Guid key);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Lock group size={Count} deadline={Deadline}"
        )]
        private partial void LogTraceLockGroupSize(int count, DateTimeLogRecord deadline);

        // "Enter-lock {Key}"
        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Enter-lock {Key}"
        )]
        private partial void LogTraceEnterLockKey(Guid key);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Exit-lock {TransactionId} {Timestamp}"
        )]
        private partial void LogDebugExitLock(Guid transactionId, DateTimeLogRecord timestamp);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Exit-lock ({Current}/{Count}) {TransactionId} {Timestamp}"
        )]
        private partial void LogDebugExitLockProgress(int current, int count, Guid transactionId, DateTimeLogRecord timestamp);
    }
}
