using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Timers.Internal;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.State
{
    internal class ConfirmationWorker<TState>
        where TState : class, new()
    {
        private readonly TransactionalStateOptions options;
        private readonly ParticipantId me;
        private readonly BatchWorker storageWorker;
        private readonly Func<StorageBatch<TState>> getStorageBatch;
        private readonly ILogger logger;
        private readonly ITimerManager timerManager;
        private readonly IActivationLifetime activationLifetime;
        private readonly HashSet<Guid> pending;

        public ConfirmationWorker(
            IOptions<TransactionalStateOptions> options,
            ParticipantId me,
            BatchWorker storageWorker,
            Func<StorageBatch<TState>> getStorageBatch,
            ILogger logger,
            ITimerManager timerManager,
            IActivationLifetime activationLifetime)
        {
            this.options = options.Value;
            this.me = me;
            this.storageWorker = storageWorker;
            this.getStorageBatch = getStorageBatch;
            this.logger = logger;
            this.timerManager = timerManager;
            this.activationLifetime = activationLifetime;
            this.pending = new HashSet<Guid>();
        }

        public void Add(Guid transactionId, DateTime timestamp, List<ParticipantId> participants)
        {
            if (!IsConfirmed(transactionId))
            {
                this.pending.Add(transactionId);
                SendConfirmation(transactionId, timestamp, participants).Ignore();
            }
        }

        public bool IsConfirmed(Guid transactionId)
        {
            return this.pending.Contains(transactionId);
        }

        private async Task SendConfirmation(Guid transactionId, DateTime timestamp, List<ParticipantId> participants)
        {
            await NotifyAll(transactionId, timestamp, participants);
            await Collect(transactionId);
        }

        private async Task NotifyAll(Guid transactionId, DateTime timestamp, List<ParticipantId> participants)
        {
            List<Confirmation> confirmations = participants
                    .Where(p => !p.Equals(this.me))
                    .Select(p => new Confirmation(
                        p,
                        transactionId,
                        timestamp,
                        () => p.Reference.AsReference<ITransactionalResourceExtension>()
                            .Confirm(p.Name, transactionId, timestamp),
                        this.logger))
                    .ToList();

            if (confirmations.Count == 0) return;

            // attempts to confirm all, will retry every ConfirmationRetryDelay until all succeed
            var ct = this.activationLifetime.OnDeactivating;

            bool hasPendingConfirmations = true;
            while (!ct.IsCancellationRequested && hasPendingConfirmations)
            {
                using (this.activationLifetime.BlockDeactivation())
                {
                    var confirmationResults = await Task.WhenAll(confirmations.Select(c => c.Confirmed()));
                    hasPendingConfirmations = false;
                    foreach (var confirmed in confirmationResults)
                    {
                        if (!confirmed)
                        {
                            hasPendingConfirmations = true;
                            await this.timerManager.Delay(this.options.ConfirmationRetryDelay, ct);
                            break;
                        }
                    }
                }
            }
        }

        // retries collect until it succeeds
        private async Task Collect(Guid transactionId)
        {
            var ct = this.activationLifetime.OnDeactivating;
            while (!ct.IsCancellationRequested)
            {
                using (this.activationLifetime.BlockDeactivation())
                {
                    if (await TryCollect(transactionId)) break;

                    await this.timerManager.Delay(this.options.ConfirmationRetryDelay, ct);
                }
            }
        }

        // attempt to clear transaction from commit log
        private async Task<bool> TryCollect(Guid transactionId)
        {
            try
            {
                var storeComplete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                // Now we can remove the commit record.
                StorageBatch<TState> storageBatch = getStorageBatch();
                storageBatch.Collect(transactionId);
                storageBatch.FollowUpAction(() =>
                {
                    if (this.logger.IsEnabled(LogLevel.Trace))
                    {
                        this.logger.LogTrace("Collection completed. TransactionId:{TransactionId}", transactionId);
                    }
                    this.pending.Remove(transactionId);
                    storeComplete.TrySetResult(true);
                });

                storageWorker.Notify();

                // wait for storage call, so we don't free spin
                return await storeComplete.Task;
            }
            catch(Exception ex)
            {
                this.logger.LogWarning(ex, "Error occured while cleaning up transaction {TransactionId} from commit log.  Will retry.", transactionId);
            }

            return false;
        }

        // Tracks the effort to notify a participant, will not call again once it succeeds.
        private struct Confirmation
        {
            private readonly ParticipantId participant;
            private readonly Guid transactionId;
            private readonly DateTime timestamp;
            private readonly Func<Task> call;
            private readonly ILogger logger;
            private Task pending;
            private bool complete;

            public Confirmation(ParticipantId paricipant, Guid transactionId, DateTime timestamp, Func<Task> call, ILogger logger)
            {
                this.participant = paricipant;
                this.transactionId = transactionId;
                this.timestamp = timestamp;
                this.call = call;
                this.logger = logger;
                this.pending = null;
                this.complete = false;
            }

            public async Task<bool> Confirmed()
            {
                if (this.complete) return this.complete;
                this.pending = this.pending ?? call();
                try
                {
                    await this.pending;
                    this.complete = true;
                }
                catch (Exception ex)
                {
                    this.pending = null;
                    logger.LogWarning(ex, "Confirmation of transaction {TransactionId} with timestamp {Timestamp} to participant {Participant} failed.  Retrying", this.transactionId, this.timestamp, this.participant);
                }
                return this.complete;
            }
        }
    }
}
