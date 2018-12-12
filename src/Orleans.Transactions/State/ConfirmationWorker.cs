using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Timers;
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
        private readonly HashSet<Guid> pending;

        public ConfirmationWorker(IOptions<TransactionalStateOptions> options, ParticipantId me, BatchWorker storageWorker, Func<StorageBatch<TState>> getStorageBatch, ILogger<ConfirmationWorker<TState>> logger, ITimerManager timerManager)
        {
            this.options = options.Value;
            this.me = me;
            this.storageWorker = storageWorker;
            this.getStorageBatch = getStorageBatch;
            this.logger = logger;
            this.timerManager = timerManager;
            this.pending = new HashSet<Guid>();
        }

        public void Add(Guid transactionId, DateTime timestamp, List<ParticipantId> participants)
        {
            if(!IsConfirmed(transactionId))
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

            // attempts to confirm all, will retry every ConfirmationRetryDelay until all succeed
            while ((await Task.WhenAll(confirmations.Select(c => c.Confirmed()))).Any(b => !b))
            {
               await this.timerManager.Delay(this.options.ConfirmationRetryDelay);
            }
        }

        // retries collect until it succeeds
        private async Task Collect(Guid transactionId)
        {
            while (!await TryCollect(transactionId))
            {
                await this.timerManager.Delay(this.options.ConfirmationRetryDelay);
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
                this.logger.LogWarning($"Error occured while cleaning up transaction {transactionId} from commit log.  Will retry.", ex);
            }

            return false;
        }

        // Tracks the effort to notify a participant, will not call again once it succeeds.
        private struct Confirmation
        {
            private readonly ParticipantId paricipant;
            private readonly Guid transactionId;
            private readonly DateTime timestamp;
            private readonly Func<Task> call;
            private readonly ILogger logger;
            private Task pending;
            private bool complete;

            public Confirmation(ParticipantId paricipant, Guid transactionId, DateTime timestamp, Func<Task> call, ILogger<ConfirmationWorker<TState>> logger)
            {
                this.paricipant = paricipant;
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
                } catch(Exception)
                {
                    this.pending = null;
                    logger.LogWarning($"Confirmation of transactation {transactionId} with timestamp {timestamp} to participant {paricipant} failed.  Retrying");
                }
                return this.complete;
            }
        }
    }
}
