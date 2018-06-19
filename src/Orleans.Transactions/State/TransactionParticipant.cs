using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions;
using Orleans.Storage;

namespace Orleans.Transactions
{
    /// <summary>
    /// Stateful facet that respects Orleans transaction semantics
    /// </summary>
    /// <typeparam name="TState"></typeparam>
    public partial class TransactionalState<TState> : ITransactionalState<TState>, ITransactionParticipant, ILifecycleParticipant<IGrainLifecycle>
        where TState : class, new()
    {
        private bool detectReentrancy;

 
        #region commit queue operations

        private async Task StorageWork()
        {
            try
            {
                if (problemFlag != TransactionalStatus.Ok)
                {
                    logger.Debug($"restoring state after status={problemFlag}");

                    await Restore();
                }
                else
                {
                    // count committable entries at the bottom of the commit queue
                    int committableEntries = 0;
                    while (committableEntries < commitQueue.Count && commitQueue[committableEntries].ReadyToCommit)
                    {
                        committableEntries++;
                    }

                    // process all committable entries, assembling a storage batch
                    if (committableEntries > 0)
                    {
                        // process all committable entries, adding storage events to the storage batch
                        CollectEventsForBatch(committableEntries);

                        if (logger.IsEnabled(LogLevel.Debug))
                        {
                            var r = commitQueue.Count > committableEntries ? commitQueue[committableEntries].ToString() : "";
                            logger.Debug($"batchcommit={committableEntries} leave={commitQueue.Count - committableEntries} {r}");
                        }
                    }
                    else
                    {
                        // send or re-send messages and detect timeouts
                        CheckProgressOfCommitQueue();
                    }

                    // store the current storage batch, if it is not empty
                    StorageBatch<TState> batchBeingSentToStorage = null;
                    if (storageBatch.BatchSize > 0)
                    {
                        // get the next batch in place so it can be filled while we store the old one
                        batchBeingSentToStorage = storageBatch;                  
                        storageBatch = new StorageBatch<TState>(batchBeingSentToStorage);

                        // perform the actual store, and record the e-tag
                        storageBatch.ETag = await batchBeingSentToStorage.Store(storage);
                    }

                    if (committableEntries > 0)
                    {
                        // update stable state
                        var lastCommittedEntry = commitQueue[committableEntries - 1];
                        stableState = lastCommittedEntry.State;
                        stableSequenceNumber = lastCommittedEntry.SequenceNumber;

                        // remove committed entries from commit queue
                        commitQueue.RemoveFromFront(committableEntries);
                        storageWorker.Notify();  // we have to re-check for work
                    }

                    if (batchBeingSentToStorage != null)
                    { 
                        batchBeingSentToStorage.RunFollowUpActions();
                        storageWorker.Notify();  // we have to re-check for work
                    }
                 }
                return;
            }
            catch (InconsistentStateException e)
            {
                logger.Warn(888, $"reload from storage triggered by e-tag mismatch {e}");

                problemFlag = TransactionalStatus.StorageConflict;
            }
            catch (Exception e)
            {
                logger.Warn(888, $"exception in storageWorker", e);

                problemFlag = TransactionalStatus.UnknownException;
            }

            // after exceptions, we try again, but with limits
            if (++failCounter < 10)
            {
                await Task.Delay(100);

                // this restarts the worker, which sees the problem flag and recovers.
                storageWorker.Notify();
            }
            else
            {
                // bail out
                logger.Warn(999, $"storageWorker is bailing out");
            }
        }

        private void CheckProgressOfCommitQueue()
        {
            if (commitQueue.Count > 0)
            {
                var bottom = commitQueue[0];
                var now = DateTime.UtcNow;

                switch (bottom.Role)
                {
                    case CommitRole.LocalCommit:
                        {
                            // check for timeout periodically
                            if (bottom.WaitingSince + PrepareTimeout <= now)
                            {
                                AbortCommits(TransactionalStatus.PrepareTimeout);
                                lockWorker.Notify();
                            }
                            else
                            {
                                storageWorker.Notify(bottom.WaitingSince + PrepareTimeout);
                                if (logger.IsEnabled(LogLevel.Trace))
                                    logger.Trace($"{commitQueue.Count} waiting on: LocalCommitEntry {bottom.Timestamp:o} WaitCount={bottom.WaitCount}");
                            }
                            break;
                        }

                    case CommitRole.RemoteCommit:
                        {
                            if (bottom.PrepareIsPersisted && !bottom.LastSent.HasValue)
                            {
                                // send PreparedMessage to remote TM
                                bottom.TransactionManager.Prepared(bottom.TransactionId, bottom.Timestamp, thisParticipant, TransactionalStatus.Ok).Ignore();                                
                                    
                                bottom.LastSent = now;

                                if (bottom.IsReadOnly)
                                {
                                    storageWorker.Notify(); // we are ready to batch now
                                }
                                else
                                {
                                    storageWorker.Notify(bottom.LastSent.Value + RemoteTransactionPingFrequency);
                                }
                            }
                            else if (!bottom.IsReadOnly && bottom.LastSent.HasValue)
                            {
                                // send ping messages periodically to reactivate crashed TMs

                                if (bottom.LastSent + RemoteTransactionPingFrequency <= now)
                                {
                                    bottom.TransactionManager.Ping(bottom.TransactionId, bottom.Timestamp, thisParticipant);
                                    bottom.LastSent = now;
                                }
                                else
                                {
                                    storageWorker.Notify(bottom.LastSent.Value + RemoteTransactionPingFrequency);
                                }
                            }

                            if (logger.IsEnabled(LogLevel.Trace))
                                logger.Trace($"{commitQueue.Count} waiting on: RemoteCommitEntry {bottom.Timestamp:o} IsReadOnly={bottom.IsReadOnly} PrepareIsPersisted={bottom.PrepareIsPersisted} LastSent={bottom.LastSent}");

                            break;
                        }

                    default:
                        {
                            logger.LogError(777, "internal error: impossible case {CommitRole}", bottom.Role);
                            throw new NotSupportedException($"{bottom.Role} is not a supported CommitRole.");
                        }
                }
            }
        }

        private void CollectEventsForBatch(int batchsize)
        {
            // collect events for batch
            for (int i = 0; i < batchsize; i++)
            {
                var entry = commitQueue[i];

                switch (entry.Role)
                {
                    case CommitRole.LocalCommit:
                        {
                            storageBatch.Prepare(entry.SequenceNumber, entry.TransactionId, entry.Timestamp, entry.TransactionManager, entry.State);
                            storageBatch.Commit(entry.TransactionId, entry.Timestamp, entry.WriteParticipants);
                            storageBatch.Confirm(entry.SequenceNumber);

                            // after store, send response back to TA
                            storageBatch.FollowUpAction(() =>
                            {
                                if (logger.IsEnabled(LogLevel.Trace))
                                {
                                    logger.Trace($"committed {entry.Timestamp:o}");
                                }
                                entry.PromiseForTA.TrySetResult(TransactionalStatus.Ok);
                            });

                            if (entry.WriteParticipants.Count > 1)
                            {
                                // after committing, we need to run a task to confirm and collect
                                storageBatch.FollowUpAction(() =>
                                {
                                    confirmationTasks.Add(entry.TransactionId, entry);
                                    confirmationWorker.Notify();
                                });
                            }
                            else
                            {
                                // there are no remote write participants to notify, so we can finish it all in one shot
                                storageBatch.Collect(entry.TransactionId);
                            }
                            break;
                        }

                    case CommitRole.RemoteCommit:
                        {
                            if (entry.ConfirmationResponsePromise == null)
                            {
                                // this is a read-only participant that has sent
                                // its prepared message.
                                // So we are really done and need not store or do anything.
                            }
                            else
                            {
                                // we must confirm in storage, and then respond to TM so it can collect
                                storageBatch.Confirm(entry.SequenceNumber);
                                storageBatch.FollowUpAction(() =>
                                {
                                    entry.ConfirmationResponsePromise.TrySetResult(true);
                                });
                            }

                            break;
                        }

                    case CommitRole.ReadOnly:
                        {
                            // we are a participant of a read-only transaction. Must store timestamp and then respond.
                            storageBatch.Read(entry.Timestamp);
                            storageBatch.FollowUpAction(() =>
                            {
                                entry.PromiseForTA.TrySetResult(TransactionalStatus.Ok);
                            });

                            break;
                        }

                    default:
                        {
                            logger.LogError(777, "internal error: impossible case {CommitRole}", entry.Role);
                            throw new NotSupportedException($"{entry.Role} is not a supported CommitRole.");
                        }
                }
            }
        }

        private void GetMostRecentState(out TState state, out long sequenceNumber)
        {
            if (commitQueue.Count == 0)
            {
                state = stableState;
                sequenceNumber = stableSequenceNumber;
            }
            else
            {
                var record = commitQueue.Last;
                state = record.State;
                sequenceNumber = record.SequenceNumber;
            }
        }

        private int CountBatchableOperationsAtTopOfCommitQueue()
        {
            int count = 0;
            int pos = commitQueue.Count - 1;
            while (pos >= 0 && commitQueue[pos].Batchable)
            {
                pos--;
                count++;
            }
            return count;
        }

        private void AbortCommits(TransactionalStatus status, int from = 0)
        {
            // emtpy the back of the commit queue, starting at specified position
            for (int i = from; i < commitQueue.Count; i++)
            {
                NotifyOfAbort(commitQueue[i], i == from ? status : TransactionalStatus.CascadingAbort);
            }
            commitQueue.RemoveFromBack(commitQueue.Count - from);

            AbortExecutingTransactions("due to cascading abort");
        }

        private void NotifyOfAbort(TransactionRecord<TState> entry, TransactionalStatus status)
        {
            switch (entry.Role)
            {
                case CommitRole.NotYetDetermined:
                    {
                        // cannot notify anyone. TA will detect broken lock during prepare.
                        break;
                    }
                case CommitRole.RemoteCommit:
                    {
                        if (logger.IsEnabled(LogLevel.Trace))
                            logger.Trace($"aborting RemoteCommitEntry {entry.Timestamp:o} status={status}");

                        if (entry.LastSent.HasValue)
                            return; // cannot abort anymore if we already sent prepare-ok message

                        entry.TransactionManager.Prepared(entry.TransactionId, entry.Timestamp, thisParticipant, status).Ignore();
                        break;
                    }
                case CommitRole.LocalCommit:
                    {
                        if (logger.IsEnabled(LogLevel.Trace))
                            logger.Trace($"aborting LocalCommitEntry {entry.Timestamp:o} status={status}");

                        // reply to transaction agent
                        entry.PromiseForTA.TrySetResult(status);

                        // tell remote participants
                        foreach (var p in entry.WriteParticipants)
                            if (p != thisParticipant)
                            {
                                 p.Cancel(entry.TransactionId, entry.Timestamp, status).Ignore();
                            }

                        break;
                    }
                case CommitRole.ReadOnly:
                    {
                        if (logger.IsEnabled(LogLevel.Trace))
                            logger.Trace($"aborting ReadEntry {entry.Timestamp:o} status={status}");

                        // reply to transaction agent
                        entry.PromiseForTA.TrySetResult(status);

                        break;
                    }
                default:
                    {
                        logger.LogError(777, "internal error: impossible case {CommitRole}", entry.Role);
                        throw new NotSupportedException($"{entry.Role} is not a supported CommitRole.");
                    }
            }
        }

        #endregion

        #region ITransactionalState<TState>

        /// <inheritdoc/>
        public Task<TResult> PerformUpdate<TResult>(Func<TState, TResult> updateAction)
        {
            if (updateAction == null) throw new ArgumentNullException(nameof(updateAction));
            if (detectReentrancy)
            {
                throw new LockRecursionException("cannot perform an update operation from within another operation");
            }

            var info = (TransactionInfo)TransactionContext.GetRequiredTransactionInfo<TransactionInfo>();

            if (logger.IsEnabled(LogLevel.Trace))
                logger.Trace($"StartWrite {info}");

            if (info.IsReadOnly)
            {
                throw new OrleansReadOnlyViolatedException(info.Id);
            }

            info.Participants.TryGetValue(thisParticipant, out var recordedaccesses);

            return EnterLock<TResult>(info.TransactionId, info.Priority, recordedaccesses, false,
                new Task<TResult>(() =>
                {
                    // check if we expired while waiting
                    if (!currentGroup.TryGetValue(info.TransactionId, out var record))
                    {
                        throw new OrleansTransactionLockAcquireTimeoutException(info.TransactionId.ToString());
                    }

                    // merge the current clock into the transaction time stamp
                    record.Timestamp = this.clock.MergeUtcNow(info.TimeStamp);

                    // link to the latest state
                    if (record.State == null)
                    {
                        GetMostRecentState(out record.State, out record.SequenceNumber);
                    }

                    // if this is the first write, make a deep copy of the state
                    if (!record.HasCopiedState)
                    {
                        record.State = copier.DeepCopy(record.State);
                        record.SequenceNumber++;
                        record.HasCopiedState = true;
                    }

                    if (logger.IsEnabled(LogLevel.Debug))
                        logger.Debug($"update-lock write v{record.SequenceNumber} {record.TransactionId} {record.Timestamp:o}");

                    // record this write in the transaction info data structure
                    info.RecordWrite(thisParticipant, record.Timestamp);

                    // record this participant as a TM candidate
                    if (info.TMCandidate != thisParticipant)
                    {
                        int batchsize = CountBatchableOperationsAtTopOfCommitQueue();
                        if (info.TMCandidate == null || batchsize > info.TMBatchSize)
                        {
                            info.TMCandidate = thisParticipant;
                            info.TMBatchSize = batchsize;
                        }
                    }

                    // perform the write
                    TResult result = default(TResult);
                    try
                    {
                        detectReentrancy = true;

                        if (updateAction != null)
                        {
                            result = updateAction(record.State);
                        }
                        return result;
                    }
                    finally
                    {
                        if (logger.IsEnabled(LogLevel.Trace))
                            logger.Trace($"EndWrite {info} {result} {record.State}");

                        detectReentrancy = false;
                    }
                }
            ));
        }


        public string CurrentTransactionId => TransactionContext.GetRequiredTransactionInfo<TransactionInfo>().Id;

        /// <summary>
        /// Read the current state.
        /// </summary>
        public Task<TResult> PerformRead<TResult>(Func<TState, TResult> operation)
        {
            if (detectReentrancy)
            {
                throw new LockRecursionException("cannot perform a read operation from within another operation");
            }

            var info = (TransactionInfo)TransactionContext.GetRequiredTransactionInfo<TransactionInfo>();

            if (logger.IsEnabled(LogLevel.Trace))
                logger.Trace($"StartRead {info}");

            info.Participants.TryGetValue(thisParticipant, out var recordedaccesses);

            // schedule read access to happen under the lock
            return EnterLock<TResult>(info.TransactionId, info.Priority, recordedaccesses, true,
                 new Task<TResult>(() =>
                 {
                     // check if our record is gone because we expired while waiting
                     if (!currentGroup.TryGetValue(info.TransactionId, out var record))
                     {
                         throw new OrleansTransactionLockAcquireTimeoutException(info.TransactionId.ToString());
                     }

                     // merge the current clock into the transaction time stamp
                     record.Timestamp = this.clock.MergeUtcNow(info.TimeStamp);

                     if (record.State == null)
                     {
                         GetMostRecentState(out record.State, out record.SequenceNumber);
                     }

                     if (logger.IsEnabled(LogLevel.Debug))
                         logger.Debug($"update-lock read v{record.SequenceNumber} {record.TransactionId} {record.Timestamp:o}");

                     // record this read in the transaction info data structure
                     info.RecordRead(thisParticipant, record.Timestamp);

                     // perform the read 
                     TResult result = default(TResult);
                     try
                     {
                         detectReentrancy = true;

                         result = operation(record.State);
                     }
                     finally
                     {
                         if (logger.IsEnabled(LogLevel.Trace))
                             logger.Trace($"EndRead {info} {result} {record.State}");

                         detectReentrancy = false;
                     }

                     return result;
                 }));
        }
        #endregion

        #region ITransactionParticipant


        /// <summary>
        /// called for transactions that do not enter prepare phase
        /// </summary>
        public Task Abort(Guid transactionId)
        {
            // release the lock
            Rollback(transactionId, "abort message from TA", false);

            lockWorker.Notify();

            return Task.CompletedTask; // one-way, no response
        }


        public Task Prepare(Guid transactionId, AccessCounter accessCount, DateTime timeStamp, ITransactionParticipant transactionManager)
        {
            var valid = ValidateLock(transactionId, accessCount, out var status, out var record);

            record.Timestamp = timeStamp;
            record.Role = CommitRole.RemoteCommit; // we are not the TM
            record.TransactionManager = transactionManager;
            record.LastSent = null;
            record.PrepareIsPersisted = false;

            if (!valid)
            {
                NotifyOfAbort(record, status);
            }
            else
            {
                this.clock.Merge(record.Timestamp);
            }

            lockWorker.Notify();
            return Task.CompletedTask; // one-way, no response
        }

        public Task<TransactionalStatus> PrepareAndCommit(Guid transactionId, AccessCounter accessCount, DateTime timeStamp, List<ITransactionParticipant> writeParticipants, int totalParticipants)
        {
            // validate the lock
            var valid = ValidateLock(transactionId, accessCount, out var status, out var record);

            record.Timestamp = timeStamp;
            record.Role = CommitRole.LocalCommit; // we are the TM
            record.WaitCount = totalParticipants - 1;
            record.WaitingSince = DateTime.UtcNow;
            record.WriteParticipants = writeParticipants;
            record.PromiseForTA = new TaskCompletionSource<TransactionalStatus>();

            if (!valid)
            {
                NotifyOfAbort(record, status);
            }
            else
            {
                clock.Merge(record.Timestamp);
            }

            lockWorker.Notify();
            return record.PromiseForTA.Task;
        }

        public Task<TransactionalStatus> CommitReadOnly(Guid transactionId, AccessCounter accessCount, DateTime timeStamp)
        {
            // validate the lock
            var valid = ValidateLock(transactionId, accessCount, out var status, out var record);

            record.Timestamp = timeStamp;
            record.Role = CommitRole.ReadOnly;
            record.PromiseForTA = new TaskCompletionSource<TransactionalStatus>();

            if (!valid)
            {
                NotifyOfAbort(record, status);
            }
            else
            {
                this.clock.Merge(record.Timestamp);
            }

            lockWorker.Notify();
            return record.PromiseForTA.Task;
        }

        public Task Prepared(Guid transactionId, DateTime timeStamp, ITransactionParticipant participant, TransactionalStatus status)
        {
            var pos = commitQueue.Find(transactionId, timeStamp);

            if (pos != -1)
            {
                var localEntry = commitQueue[pos];

                if (localEntry.Role != CommitRole.LocalCommit)
                {
                    logger.Error(666, $"transaction abort due to internal error in {nameof(Prepared)}: Wrong commit type");
                    throw new InvalidOperationException($"Wrong commit type: {localEntry.Role}");
                }

                if (status == TransactionalStatus.Ok)
                {
                    localEntry.WaitCount--;

                    storageWorker.Notify();
                }
                else
                {
                    AbortCommits(status, pos);

                    lockWorker.Notify();
                }
            }
            else
            {
                // this message has arrived ahead of the commit request - we need to remember it

                if (unprocessedPreparedMessages == null)
                    unprocessedPreparedMessages = new Dictionary<DateTime, PMessages>();

                PMessages info;
                if (!unprocessedPreparedMessages.TryGetValue(timeStamp, out info))
                {
                    unprocessedPreparedMessages[timeStamp] = info = new PMessages();
                }
                if (status == TransactionalStatus.Ok)
                {
                    info.Count++;
                }
                else
                {
                    info.Status = status;
                }

                // TODO fix memory leak if corresponding commit messages never arrive
            }

            return Task.CompletedTask; // one-way, no response
        }

        public async Task Confirm(Guid transactionId, DateTime timeStamp)
        {
            // find in queue
            var pos = commitQueue.Find(transactionId, timeStamp);

            if (pos == -1)
                return; // must have already been confirmed

            var remoteEntry = commitQueue[pos];

                if (remoteEntry.Role != CommitRole.RemoteCommit)
                {
                    logger.Error(666, $"internal error in {nameof(Prepared)}: wrong commit type");
                    throw new InvalidOperationException($"Wrong commit type: {remoteEntry.Role}");
                }

                // setting this field makes this entry ready for batching

                remoteEntry.ConfirmationResponsePromise = new TaskCompletionSource<bool>();

                storageWorker.Notify();

                // now we wait for the batch to finish

                await remoteEntry.ConfirmationResponsePromise.Task;
        }

        public Task Cancel(Guid transactionId, DateTime timeStamp, TransactionalStatus status)
        {
            // find in queue
            var pos = commitQueue.Find(transactionId, timeStamp);

            if (pos == -1)
                return Task.CompletedTask; // must have already been cancelled

            storageBatch.Cancel(commitQueue[pos].SequenceNumber);

            AbortCommits(status, pos);

            storageWorker.Notify();

            lockWorker.Notify();

            return Task.CompletedTask;
        }

        public Task Ping(Guid transactionId, DateTime timeStamp, ITransactionParticipant participant)
        {
            if (commitQueue.Find(transactionId, timeStamp) != -1)
            {
                // no need to take special action now - the transaction is still
                // in the commit queue and its status is not yet determined.
                // confirmation or cancellation will be sent after committing or aborting.

                storageWorker.Notify(); // just in case the worker fell asleep or something
            }
            else
            {
                if (confirmationTasks.TryGetValue(transactionId, out var record))
                {
                    // re-send now
                    record.LastSent = null;
                    confirmationWorker.Notify();
                }
                else
                {
                    // we never heard of this transaction - so it must have aborted
                    participant.Cancel(transactionId, timeStamp, TransactionalStatus.PresumedAbort);
                }
            }
            return Task.CompletedTask; // one-way, no response
        }

         #endregion

        private void EnqueueCommit(TransactionRecord<TState> record)
        {
            try
            {
                if (logger.IsEnabled(LogLevel.Trace))
                    logger.Trace($"start two-phase-commit {record.TransactionId} {record.Timestamp:o}");

                commitQueue.Add(record);

                // additional actions for each commit type
                switch (record.Role)
                {
                    case CommitRole.ReadOnly:
                        {
                            // no extra actions needed
                            break;
                        }

                    case CommitRole.LocalCommit:
                        {
                            // process prepared messages received ahead of time
                            if (unprocessedPreparedMessages != null
                                && unprocessedPreparedMessages.TryGetValue(record.Timestamp, out var info))
                            {
                                if (info.Status == TransactionalStatus.Ok)
                                {
                                    record.WaitCount -= info.Count;
                                }
                                else
                                {
                                    AbortCommits(info.Status, commitQueue.Count - 1);

                                    lockWorker.Notify();
                                }
                                unprocessedPreparedMessages.Remove(record.Timestamp);
                            }
                            break;
                        }

                    case CommitRole.RemoteCommit:
                        {
                            // optimization: can immediately proceed if dependency is implied
                            bool behindRemoteEntryBySameTM =
                                commitQueue.Count >= 2
                                && commitQueue[commitQueue.Count - 2] is TransactionRecord<TState> rce
                                && rce.Role == CommitRole.RemoteCommit
                                && rce.TransactionManager.Equals(record.TransactionManager);

                            if (record.NumberWrites > 0)
                            {
                                storageBatch.Prepare(record.SequenceNumber, record.TransactionId, record.Timestamp, record.TransactionManager, record.State);
                            }
                            else
                            {
                                storageBatch.Read(record.Timestamp);
                            }

                            storageBatch.FollowUpAction(() =>
                            {
                                if (logger.IsEnabled(LogLevel.Trace))
                                {
                                    logger.Trace($"prepared {record.TransactionId} {record.Timestamp:o}");
                                }

                                record.PrepareIsPersisted = true;

                                if (behindRemoteEntryBySameTM)
                                {
                                    // can send prepared message immediately after persisting prepare record
                                    record.TransactionManager.Prepared(record.TransactionId, record.Timestamp, thisParticipant, TransactionalStatus.Ok).Ignore();
                                    record.LastSent = DateTime.UtcNow;
                                }
                            });
                            break;
                        }

                    default:
                        {
                            logger.LogError(777, "internal error: impossible case {CommitRole}", record.Role);
                            throw new NotSupportedException($"{record.Role} is not a supported CommitRole.");
                        }
                }
            }
            catch (Exception e)
            {
                logger.Error(666, $"transaction abort due to internal error in {nameof(EnqueueCommit)}: ", e);
                NotifyOfAbort(record, TransactionalStatus.UnknownException);
            }
        }

        private Task ConfirmationWork()
        {
            var sendlist = confirmationTasks.Where(r => !r.Value.LastConfirmationAttempt.HasValue
              || r.Value.LastConfirmationAttempt + ConfirmationRetryDelay < DateTime.UtcNow).ToList();

            foreach (var kvp in sendlist)
            {
                ConfirmationTask(kvp.Value).Ignore();
            }

            return Task.CompletedTask;
        }

        private async Task ConfirmationTask(TransactionRecord<TState> record)
        {
            try
            {
                var tasks = new List<Task>();

                record.LastConfirmationAttempt = DateTime.UtcNow;

                foreach (var p in record.WriteParticipants)
                {
                    if (p != thisParticipant)
                    {
                        tasks.Add(p.Confirm(record.TransactionId, record.Timestamp));
                    }
                }

                await Task.WhenAll(tasks);

                confirmationTasks.Remove(record.TransactionId);

                // all prepare records have been removed from all participants. 
                // Now we can remove the commit record.
                storageBatch.Collect(record.TransactionId);

                storageWorker.Notify();
            }
            catch (Exception e)
            {
                // we are giving up for now.
                // if pinged or reloaded from storage, we'll try again.
                logger.Warn(333, $"Could not notify/collect:", e);
            }
        }



    }
}
