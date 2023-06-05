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
            pending = new HashSet<Guid>();
        }

        public void Add(Guid transactionId, DateTime timestamp, List<ParticipantId> participants)
        {
            if (!IsConfirmed(transactionId))
            {
                pending.Add(transactionId);
                SendConfirmation(transactionId, timestamp, participants).Ignore();
            }
        }

        public bool IsConfirmed(Guid transactionId) => pending.Contains(transactionId);

        private async Task SendConfirmation(Guid transactionId, DateTime timestamp, List<ParticipantId> participants)
        {
            await NotifyAll(transactionId, timestamp, participants);
            await Collect(transactionId);
        }

        private async Task NotifyAll(Guid transactionId, DateTime timestamp, List<ParticipantId> participants)
        {
            var confirmations = participants
                    .Where(p => !p.Equals(me))
                    .Select(p => new Confirmation(
                        p,
                        transactionId,
                        timestamp,
                        () => p.Reference.AsReference<ITransactionalResourceExtension>()
                            .Confirm(p.Name, transactionId, timestamp),
                        logger))
                    .ToList();

            if (confirmations.Count == 0) return;

            // attempts to confirm all, will retry every ConfirmationRetryDelay until all succeed
            var ct = activationLifetime.OnDeactivating;

            var hasPendingConfirmations = true;
            while (!ct.IsCancellationRequested && hasPendingConfirmations)
            {
                using (activationLifetime.BlockDeactivation())
                {
                    var confirmationResults = await Task.WhenAll(confirmations.Select(c => c.Confirmed()));
                    hasPendingConfirmations = false;
                    foreach (var confirmed in confirmationResults)
                    {
                        if (!confirmed)
                        {
                            hasPendingConfirmations = true;
                            await timerManager.Delay(options.ConfirmationRetryDelay, ct);
                            break;
                        }
                    }
                }
            }
        }

        // retries collect until it succeeds
        private async Task Collect(Guid transactionId)
        {
            var ct = activationLifetime.OnDeactivating;
            while (!ct.IsCancellationRequested)
            {
                using (activationLifetime.BlockDeactivation())
                {
                    if (await TryCollect(transactionId)) break;

                    await timerManager.Delay(options.ConfirmationRetryDelay, ct);
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
                var storageBatch = getStorageBatch();
                storageBatch.Collect(transactionId);
                storageBatch.FollowUpAction(() =>
                {
                    if (logger.IsEnabled(LogLevel.Trace))
                    {
                        logger.LogTrace("Collection completed. TransactionId:{TransactionId}", transactionId);
                    }
                    pending.Remove(transactionId);
                    storeComplete.TrySetResult(true);
                });

                storageWorker.Notify();

                // wait for storage call, so we don't free spin
                return await storeComplete.Task;
            }
            catch(Exception ex)
            {
                logger.LogWarning(ex, "Error occured while cleaning up transaction {TransactionId} from commit log.  Will retry.", transactionId);
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
                participant = paricipant;
                this.transactionId = transactionId;
                this.timestamp = timestamp;
                this.call = call;
                this.logger = logger;
                pending = null;
                complete = false;
            }

            public async Task<bool> Confirmed()
            {
                if (complete) return complete;
                pending = pending ?? call();
                try
                {
                    await pending;
                    complete = true;
                }
                catch (Exception ex)
                {
                    pending = null;
                    logger.LogWarning(ex, "Confirmation of transaction {TransactionId} with timestamp {Timestamp} to participant {Participant} failed.  Retrying", transactionId, timestamp, participant);
                }
                return complete;
            }
        }
    }
}
