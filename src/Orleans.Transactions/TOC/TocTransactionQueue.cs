using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Timers.Internal;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.DeadlockDetection;
using Orleans.Transactions.State;

namespace Orleans.Transactions.TOC
{
    internal class TocTransactionQueue<TService> : TransactionQueue<TransactionCommitter<TService>.OperationState>
                where TService : class
    {
        private TService service;

        public TocTransactionQueue(
            TService service,
            IOptions<TransactionalStateOptions> options,
            ParticipantId resource,
            Action deactivate,
            ITransactionalStateStorage<TransactionCommitter<TService>.OperationState> storage,
            IClock clock,
            ILogger logger,
            ITimerManager timerManager,
            IActivationLifetime activationLifetime,
            ITransactionalLockObserver lockObserver)
            : base(options, resource, deactivate, storage, clock, logger, timerManager, activationLifetime, lockObserver)
        {
            this.service = service;
        }

        protected override void OnLocalCommit(TransactionRecord<TransactionCommitter<TService>.OperationState> entry)
        {
            base.storageBatch.AddStorePreCondition(() => entry.State.Operation.Commit(entry.TransactionId, this.service));
            base.OnLocalCommit(entry);
        }
    }
}
