using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;
using Orleans.Storage;
using Orleans.Configuration;
using Orleans.Timers.Internal;

namespace Orleans.Transactions.State
{
    internal class TransactionQueue<TState>
        where TState : class, new()
    {
        private readonly TransactionalStateOptions options;
        private readonly ParticipantId resource;
        private readonly Action deactivate;
        private readonly ITransactionalStateStorage<TState> storage;
        private readonly BatchWorker storageWorker;
        protected readonly ILogger logger;
        private readonly IActivationLifetime activationLifetime;
        private readonly ConfirmationWorker<TState> confirmationWorker;
        private CommitQueue<TState> commitQueue;
        private Task readyTask;

        protected StorageBatch<TState> storageBatch;

        private int failCounter;

        // collection tasks
        private Dictionary<DateTime, PreparedMessages> unprocessedPreparedMessages;
        private class PreparedMessages
        {
            public PreparedMessages(TransactionalStatus status)
            {
                this.Status = status;
            }
            public int Count;
            public TransactionalStatus Status;
        }

        private TState stableState;
        private long stableSequenceNumber;
        public ReadWriteLock<TState> RWLock { get; }
        public CausalClock Clock { get; }

        public TransactionQueue(
            IOptions<TransactionalStateOptions> options,
            ParticipantId resource,
            Action deactivate,
            ITransactionalStateStorage<TState> storage,
            IClock clock,
            ILogger logger,
            ITimerManager timerManager,
            IActivationLifetime activationLifetime)
        {
            this.options = options.Value;
            this.resource = resource;
            this.deactivate = deactivate;
            this.storage = storage;
            this.Clock = new CausalClock(clock);
            this.logger = logger;
            this.activationLifetime = activationLifetime;
            this.storageWorker = new BatchWorkerFromDelegate(StorageWork, this.activationLifetime.OnDeactivating);
            this.RWLock = new ReadWriteLock<TState>(options, this, this.storageWorker, logger, activationLifetime);
            this.confirmationWorker = new ConfirmationWorker<TState>(options, this.resource, this.storageWorker, () => this.storageBatch, this.logger, timerManager, activationLifetime);
            this.unprocessedPreparedMessages = new Dictionary<DateTime, PreparedMessages>();
            this.commitQueue = new CommitQueue<TState>();
            this.readyTask = Task.CompletedTask;
        }

        public async Task EnqueueCommit(TransactionRecord<TState> record)
        {
            try
            {
                if (logger.IsEnabled(LogLevel.Trace))
                    logger.LogTrace("Start two-phase-commit {TransactionId} {Timestamp}", record.TransactionId, record.Timestamp.ToString("O"));

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
                            if (unprocessedPreparedMessages.TryGetValue(record.Timestamp, out PreparedMessages info))
                            {
                                if (info.Status == TransactionalStatus.Ok)
                                {
                                    record.WaitCount -= info.Count;
                                }
                                else
                                {
                                    await AbortCommits(info.Status, commitQueue.Count - 1);

                                    this.RWLock.Notify();
                                }
                                unprocessedPreparedMessages.Remove(record.Timestamp);
                            }
                            break;
                        }

                    case CommitRole.RemoteCommit:
                        {

                            // optimization: can immediately proceed if dependency is implied
                            bool behindRemoteEntryBySameTM = false;
                                /* disabled - jbragg - TODO - revisit
                                commitQueue.Count >= 2
                                && commitQueue[commitQueue.Count - 2] is TransactionRecord<TState> rce
                                && rce.Role == CommitRole.RemoteCommit
                                && rce.TransactionManager.Equals(record.TransactionManager);
                                */

                            if (record.NumberWrites > 0)
                            {
                                this.storageBatch.Prepare(record.SequenceNumber, record.TransactionId, record.Timestamp, record.TransactionManager, record.State);
                            }
                            else
                            {
                                this.storageBatch.Read(record.Timestamp);
                            }

                            this.storageBatch.FollowUpAction(() =>
                            {
                                if (logger.IsEnabled(LogLevel.Trace))
                                {
                                    logger.LogTrace("Persisted {Record}", record);
                                }

                                record.PrepareIsPersisted = true;

                                if (behindRemoteEntryBySameTM)
                                {
                                    if (logger.IsEnabled(LogLevel.Trace))
                                    {
                                        logger.LogTrace("Sending immediate prepared {Record}", record);
                                    }
                                    // can send prepared message immediately after persisting prepare record
                                    record.TransactionManager.Reference.AsReference<ITransactionManagerExtension>()
                                          .Prepared(record.TransactionManager.Name, record.TransactionId, record.Timestamp, this.resource, TransactionalStatus.Ok)
                                          .Ignore();
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
            catch (Exception exception)
            {
                logger.LogError(exception, $"Transaction abort due to internal error in {nameof(EnqueueCommit)}", exception);
                await NotifyOfAbort(record, TransactionalStatus.UnknownException, exception);
            }
        }

        public async Task NotifyOfPrepared(Guid transactionId, DateTime timeStamp, TransactionalStatus status)
        {
            var pos = commitQueue.Find(transactionId, timeStamp);
            if (logger.IsEnabled(LogLevel.Trace))
                logger.LogTrace("NotifyOfPrepared - TransactionId:{TransactionId} Timestamp:{Timestamp}, TransactionalStatus{TransactionalStatus}", transactionId, timeStamp, status);

            if (pos != -1)
            {

                var localEntry = commitQueue[pos];

                if (localEntry.Role != CommitRole.LocalCommit)
                {
                    logger.LogError($"Transaction abort due to internal error in {nameof(NotifyOfPrepared)}: Wrong commit type");
                    throw new InvalidOperationException($"Wrong commit type: {localEntry.Role}");
                }

                if (status == TransactionalStatus.Ok)
                {
                    localEntry.WaitCount--;

                    storageWorker.Notify();
                }
                else
                {
                    await AbortCommits(status, pos);

                    this.RWLock.Notify();
                }
            }
            else
            {
                // this message has arrived ahead of the commit request - we need to remember it
                if (!this.unprocessedPreparedMessages.TryGetValue(timeStamp, out PreparedMessages info))
                {
                    this.unprocessedPreparedMessages[timeStamp] = info = new PreparedMessages(status);
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
        }

        public async Task NotifyOfPrepare(Guid transactionId, AccessCounter accessCount, DateTime timeStamp, ParticipantId transactionManager)
        {
            var locked = await this.RWLock.ValidateLock(transactionId, accessCount);
            var status = locked.Item1;
            var record = locked.Item2;
            var valid = status == TransactionalStatus.Ok;

            record.Timestamp = timeStamp;
            record.Role = CommitRole.RemoteCommit; // we are not the TM
            record.TransactionManager = transactionManager;
            record.LastSent = null;
            record.PrepareIsPersisted = false;

            if (!valid)
            {
                await this.NotifyOfAbort(record, status, exception: null);
            }
            else
            {
                this.Clock.Merge(record.Timestamp);
            }

            this.RWLock.Notify();
        }

        public async Task NotifyOfAbort(TransactionRecord<TState> entry, TransactionalStatus status, Exception exception)
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
                            logger.LogTrace("Aborting status={Status} {Entry}", status, entry);

                        entry.ConfirmationResponsePromise?.TrySetException(new OrleansException($"Confirm failed: Status {status}"));

                        if (entry.LastSent.HasValue)
                            return; // cannot abort anymore if we already sent prepare-ok message

                        if (logger.IsEnabled(LogLevel.Trace))
                            logger.LogTrace("Aborting via Prepared. Status={Status} Entry={Entry}", status, entry);

                        entry.TransactionManager.Reference.AsReference<ITransactionManagerExtension>()
                             .Prepared(entry.TransactionManager.Name, entry.TransactionId, entry.Timestamp, resource, status)
                             .Ignore();
                        break;
                    }
                case CommitRole.LocalCommit:
                    {
                        if (logger.IsEnabled(LogLevel.Trace))
                            logger.LogTrace("Aborting status={Status} {Entry}", status, entry);

                        try
                        {
                            // tell remote participants
                            await Task.WhenAll(entry.WriteParticipants
                                .Where(p => !p.Equals(resource))
                                .Select(p => p.Reference.AsReference<ITransactionalResourceExtension>()
                                     .Cancel(p.Name, entry.TransactionId, entry.Timestamp, status)));
                        }
                        catch(Exception ex)
                        {
                            this.logger.LogWarning(ex, "Failed to notify all transaction participants of cancellation.  TransactionId: {TransactionId}, Timestamp: {Timestamp}, Status: {Status}", entry.TransactionId, entry.Timestamp, status);
                        }

                        // reply to transaction agent
                        if (exception is not null)
                        {
                            entry.PromiseForTA.TrySetException(exception);
                        }
                        else
                        {
                            entry.PromiseForTA.TrySetResult(status);
                        }

                        break;
                    }
                case CommitRole.ReadOnly:
                    {
                        if (logger.IsEnabled(LogLevel.Trace))
                            logger.LogTrace("Aborting status={Status} {Entry}", status, entry);

                        // reply to transaction agent
                        if (exception is not null)
                        {
                            entry.PromiseForTA.TrySetException(exception);
                        }
                        else
                        {
                            entry.PromiseForTA.TrySetResult(status);
                        }

                        break;
                    }
                default:
                    {
                        logger.LogError(777, "internal error: impossible case {CommitRole}", entry.Role);
                        throw new NotSupportedException($"{entry.Role} is not a supported CommitRole.");
                    }
            }
        }

        public async Task NotifyOfPing(Guid transactionId, DateTime timeStamp, ParticipantId resource)
        {
            if (this.commitQueue.Find(transactionId, timeStamp) != -1)
            {
                // no need to take special action now - the transaction is still
                // in the commit queue and its status is not yet determined.
                // confirmation or cancellation will be sent after committing or aborting.

                if (logger.IsEnabled(LogLevel.Trace))
                    logger.LogTrace("Received ping for {TransactionId}, irrelevant (still processing)", transactionId);

                this.storageWorker.Notify(); // just in case the worker fell asleep or something
            }
            else
            {
                if (!this.confirmationWorker.IsConfirmed(transactionId))
                {
                    if (logger.IsEnabled(LogLevel.Trace))
                        logger.LogTrace("Received ping for {TransactionId}, unknown - presumed abort", transactionId);

                    // we never heard of this transaction - so it must have aborted
                    await resource.Reference.AsReference<ITransactionalResourceExtension>()
                            .Cancel(resource.Name, transactionId, timeStamp, TransactionalStatus.PresumedAbort);
                }
            }
        }

        public async Task NotifyOfConfirm(Guid transactionId, DateTime timeStamp)
        {
            if (logger.IsEnabled(LogLevel.Trace))
                logger.LogTrace("NotifyOfConfirm: {TransactionId} {TimeStamp}", transactionId, timeStamp);

            // find in queue
            var pos = commitQueue.Find(transactionId, timeStamp);

            if (pos == -1)
                return; // must have already been confirmed

            var remoteEntry = commitQueue[pos];

            if (remoteEntry.Role != CommitRole.RemoteCommit)
            {
                logger.LogError($"Internal error in {nameof(NotifyOfConfirm)}: wrong commit type");
                throw new InvalidOperationException($"Wrong commit type: {remoteEntry.Role}");
            }

            // setting this field makes this entry ready for batching

            remoteEntry.ConfirmationResponsePromise = remoteEntry.ConfirmationResponsePromise ?? new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            storageWorker.Notify();

            // now we wait for the batch to finish

            await remoteEntry.ConfirmationResponsePromise.Task;
        }

        public async Task NotifyOfCancel(Guid transactionId, DateTime timeStamp, TransactionalStatus status)
        {
            if (logger.IsEnabled(LogLevel.Trace))
                logger.LogTrace("{MethodName}. TransactionId: {TransactionId}, TimeStamp: {TimeStamp} Status: {TransactionalStatus}", nameof(NotifyOfCancel), transactionId, timeStamp, status);

            // find in queue
            var pos = commitQueue.Find(transactionId, timeStamp);

            if (pos == -1)
                return;

            this.storageBatch.Cancel(commitQueue[pos].SequenceNumber);

            await AbortCommits(status, pos);

            storageWorker.Notify();

            this.RWLock.Notify();
        }

        /// <summary>
        /// called on activation, and when recovering from storage conflicts or other exceptions.
        /// </summary>
        public async Task NotifyOfRestore()
        {
            try
            {
                await Ready();
            }
            finally
            {
                this.readyTask = Restore();
            }
            await this.readyTask;
        }

        /// <summary>
        /// Ensures queue is ready to process requests.
        /// </summary>
        /// <returns></returns>
        public Task Ready()
        {
            return this.readyTask;
        }

        private async Task Restore()
        {
            TransactionalStorageLoadResponse<TState> loadresponse = await storage.Load();

            this.storageBatch = new StorageBatch<TState>(loadresponse);

            this.stableState = loadresponse.CommittedState;
            this.stableSequenceNumber = loadresponse.CommittedSequenceId;

            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(
                    "Load v{StableSequenceNumber} {PendingStatesCount}p {CommitRecordsCount}c",
                    this.stableSequenceNumber,
                    loadresponse.PendingStates.Count,
                    storageBatch.MetaData.CommitRecords.Count);
            }

            // ensure clock is consistent with loaded state
            this.Clock.Merge(storageBatch.MetaData.TimeStamp);

            // resume prepared transactions (not TM)
            foreach (var pr in loadresponse.PendingStates.OrderBy(ps => ps.TimeStamp))
            {
                if (pr.SequenceId > loadresponse.CommittedSequenceId && pr.TransactionManager.Reference != null)
                {
                    if (logger.IsEnabled(LogLevel.Debug))
                        logger.LogDebug("Recover two-phase-commit {TransactionId}", pr.TransactionId);

                    ParticipantId tm = pr.TransactionManager;

                    commitQueue.Add(new TransactionRecord<TState>()
                    {
                        Role = CommitRole.RemoteCommit,
                        TransactionId = Guid.Parse(pr.TransactionId),
                        Timestamp = pr.TimeStamp,
                        State = pr.State,
                        SequenceNumber = pr.SequenceId,
                        TransactionManager = tm,
                        PrepareIsPersisted = true,
                        LastSent = default(DateTime),
                        ConfirmationResponsePromise = null,
                        NumberWrites = 1 // was a writing transaction
                    });
                    this.stableSequenceNumber = pr.SequenceId;
                }
            }

            // resume committed transactions (on TM)
            foreach (var kvp in storageBatch.MetaData.CommitRecords)
            {
                if (logger.IsEnabled(LogLevel.Debug))
                    logger.LogDebug(
                        "Recover commit confirmation {Key}",
                        kvp.Key);
                this.confirmationWorker.Add(kvp.Key, kvp.Value.Timestamp, kvp.Value.WriteParticipants);
            }

            // check for work
            this.storageWorker.Notify();
            this.RWLock.Notify();
        }

        public void GetMostRecentState(out TState state, out long sequenceNumber)
        {
            if (commitQueue.Count == 0)
            {
                state = this.stableState;
                sequenceNumber = this.stableSequenceNumber;
            }
            else
            {
                var record = commitQueue.Last;
                state = record.State;
                sequenceNumber = record.SequenceNumber;
            }
        }

        public int BatchableOperationsCount()
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

        private async Task StorageWork()
        {
            // Stop if this activation is stopping/stopped.
            if (this.activationLifetime.OnDeactivating.IsCancellationRequested) return;

            using (this.activationLifetime.BlockDeactivation())
            {
                try
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
                            var recordString = commitQueue.Count > committableEntries ? commitQueue[committableEntries].ToString() : "";
                            logger.LogDebug(
                                "BatchCommit: {CommittableEntries} Leave: {UncommittableEntries}, Record: {Record}",
                                committableEntries,
                                commitQueue.Count - committableEntries,
                                recordString);
                        }
                    }
                    else
                    {
                        // send or re-send messages and detect timeouts
                        await CheckProgressOfCommitQueue();
                    }

                    // store the current storage batch, if it is not empty
                    StorageBatch<TState> batchBeingSentToStorage = null;
                    if (this.storageBatch.BatchSize > 0)
                    {
                        // get the next batch in place so it can be filled while we store the old one
                        batchBeingSentToStorage = this.storageBatch;
                        this.storageBatch = new StorageBatch<TState>(batchBeingSentToStorage);

                        try
                        {
                            if (await batchBeingSentToStorage.CheckStorePreConditions())
                            {
                                // perform the actual store, and record the e-tag
                                this.storageBatch.ETag = await batchBeingSentToStorage.Store(storage);
                                failCounter = 0;
                            }
                            else
                            {
                                logger.LogWarning("Store pre conditions not met.");
                                await AbortAndRestore(TransactionalStatus.CommitFailure, exception: null);
                                return;
                            }
                        }
                        catch (InconsistentStateException exception)
                        {
                            logger.LogWarning(888, exception, "Reload from storage triggered by e-tag mismatch.");
                            await AbortAndRestore(TransactionalStatus.StorageConflict, exception, true);
                            return;
                        }
                        catch (Exception exception)
                        {
                            logger.LogWarning(exception, "Storage exception in storage worker.");
                            await AbortAndRestore(TransactionalStatus.UnknownException, exception);
                            return;
                        }
                    }

                    if (committableEntries > 0)
                    {
                        // update stable state
                        var lastCommittedEntry = commitQueue[committableEntries - 1];
                        this.stableState = lastCommittedEntry.State;
                        this.stableSequenceNumber = lastCommittedEntry.SequenceNumber;
                        if (logger.IsEnabled(LogLevel.Trace))
                            logger.LogTrace("Stable state version: {StableSequenceNumber}", stableSequenceNumber);

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
                catch (Exception exception)
                {
                    logger.LogWarning(888, exception, "Exception in storageWorker.  Retry {FailCounter}", failCounter);
                    await AbortAndRestore(TransactionalStatus.UnknownException, exception);
                }
            }
        }

        private Task AbortAndRestore(TransactionalStatus status, Exception exception, bool force = false)
        {
            this.readyTask = Bail(status, exception, force);
            return this.readyTask;
        }

        private async Task Bail(TransactionalStatus status, Exception exception, bool force = false)
        {
            List<Task> pending = new List<Task>();
            pending.Add(RWLock.AbortExecutingTransactions(exception));
            this.RWLock.AbortQueuedTransactions();

            // abort all entries in the commit queue
            foreach (var entry in commitQueue.Elements)
            {
                pending.Add(NotifyOfAbort(entry, status, exception: exception));
            }

            commitQueue.Clear();

            await Task.WhenAll(pending);
            if (++failCounter >= 10 || force)
            {
                logger.LogDebug("StorageWorker triggering grain Deactivation");
                this.deactivate();
            }
            await this.Restore();
        }

        private async Task CheckProgressOfCommitQueue()
        {
            if (commitQueue.Count > 0)
            {
                var bottom = commitQueue[0];
                var now = DateTime.UtcNow;

                if (logger.IsEnabled(LogLevel.Trace))
                    logger.LogTrace("{CommitQueueSize} entries in queue waiting for bottom: {BottomEntry}", commitQueue.Count, bottom);

                switch (bottom.Role)
                {
                    case CommitRole.LocalCommit:
                        {
                            // check for timeout periodically
                            if (bottom.WaitingSince + this.options.PrepareTimeout <= now)
                            {
                                await AbortCommits(TransactionalStatus.PrepareTimeout);
                                this.RWLock.Notify();
                            }
                            else
                            {
                                storageWorker.Notify(bottom.WaitingSince + this.options.PrepareTimeout);
                            }
                            break;
                        }

                    case CommitRole.RemoteCommit:
                        {
                            if (bottom.PrepareIsPersisted && !bottom.LastSent.HasValue)
                            {
                                // send PreparedMessage to remote TM
                                bottom.TransactionManager.Reference.AsReference<ITransactionManagerExtension>()
                                      .Prepared(bottom.TransactionManager.Name, bottom.TransactionId, bottom.Timestamp, resource, TransactionalStatus.Ok)
                                      .Ignore();                                
                                    
                                bottom.LastSent = now;

                                if (logger.IsEnabled(LogLevel.Trace))
                                    logger.LogTrace("Sent Prepared {BottomEntry}", bottom);

                                if (bottom.IsReadOnly)
                                {
                                    storageWorker.Notify(); // we are ready to batch now
                                }
                                else
                                {
                                    storageWorker.Notify(bottom.LastSent.Value + this.options.RemoteTransactionPingFrequency);
                                }
                            }
                            else if (!bottom.IsReadOnly && bottom.LastSent.HasValue)
                            {
                                // send ping messages periodically to reactivate crashed TMs

                                if (bottom.LastSent + this.options.RemoteTransactionPingFrequency <= now)
                                {
                                    if (logger.IsEnabled(LogLevel.Trace))
                                        logger.LogTrace("Sent ping {BottomEntry}", bottom);
                                    bottom.TransactionManager.Reference.AsReference<ITransactionManagerExtension>()
                                          .Ping(bottom.TransactionManager.Name, bottom.TransactionId, bottom.Timestamp, resource).Ignore();
                                    bottom.LastSent = now;
                                }
                                storageWorker.Notify(bottom.LastSent.Value + this.options.RemoteTransactionPingFrequency);
                            }

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
                TransactionRecord<TState> entry = commitQueue[i];

                if (logger.IsEnabled(LogLevel.Trace))
                {
                    logger.LogTrace("Committing {Entry}", entry);
                }

                switch (entry.Role)
                {
                    case CommitRole.LocalCommit:
                        {
                            OnLocalCommit(entry);
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
                                this.storageBatch.Confirm(entry.SequenceNumber);
                                this.storageBatch.FollowUpAction(() =>
                                {
                                    entry.ConfirmationResponsePromise.TrySetResult(true);
                                    if (this.logger.IsEnabled(LogLevel.Trace))
                                    {
                                        this.logger.LogTrace(
                                            "Confirmed remote commit v{SequenceNumber}. TransactionId:{TransactionId} Timestamp:{Timestamp} TransactionManager:{TransactionManager}",
                                            entry.SequenceNumber,
                                            entry.TransactionId,
                                            entry.Timestamp,
                                            entry.TransactionManager);
                                    }
                                });
                            }

                            break;
                        }

                    case CommitRole.ReadOnly:
                        {
                            // we are a participant of a read-only transaction. Must store timestamp and then respond.
                            this.storageBatch.Read(entry.Timestamp);
                            this.storageBatch.FollowUpAction(() =>
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

        protected virtual void OnLocalCommit(TransactionRecord<TState> entry)
        {
            this.storageBatch.Prepare(entry.SequenceNumber, entry.TransactionId, entry.Timestamp, entry.TransactionManager, entry.State);
            this.storageBatch.Commit(entry.TransactionId, entry.Timestamp, entry.WriteParticipants);
            this.storageBatch.Confirm(entry.SequenceNumber);

            // after store, send response back to TA
            this.storageBatch.FollowUpAction(() =>
            {
                if (this.logger.IsEnabled(LogLevel.Trace))
                {
                    this.logger.LogTrace(
                        "Locally committed {TransactionId} {Timestamp}",
                        entry.TransactionId,
                        entry.Timestamp.ToString("O"));
                }
                entry.PromiseForTA.TrySetResult(TransactionalStatus.Ok);
            });

            if (entry.WriteParticipants.Count > 1)
            {
                // after committing, we need to run a task to confirm and collect
                this.storageBatch.FollowUpAction(() =>
                {
                    if (this.logger.IsEnabled(LogLevel.Trace))
                    {
                        this.logger.LogTrace(
                            "Adding confirmation to worker for {TransactionId} {Timestamp}",
                            entry.TransactionId,
                            entry.Timestamp.ToString("O"));
                    }
                    this.confirmationWorker.Add(entry.TransactionId, entry.Timestamp, entry.WriteParticipants);
                });
            }
            else
            {
                // there are no remote write participants to notify, so we can finish it all in one shot
                this.storageBatch.Collect(entry.TransactionId);
            }
        }

        private async Task AbortCommits(TransactionalStatus status, int from = 0)
        {
            List<Task> pending = new List<Task>();
            // emtpy the back of the commit queue, starting at specified position
            for (int i = from; i < commitQueue.Count; i++)
            {
                pending.Add(NotifyOfAbort(commitQueue[i], i == from ? status : TransactionalStatus.CascadingAbort, exception: null));
            }
            commitQueue.RemoveFromBack(commitQueue.Count - from);

            pending.Add(this.RWLock.AbortExecutingTransactions(exception: null));
            await Task.WhenAll(pending);
        }
    }
}
