using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.State;

namespace Orleans.Transactions.Tests.DeactivatingInjection
{
    // enum is a value type, so we need a reference type to pass TransactionDeactivationPhase from DeactivationTransactionalState facet to its transaction manager
    // and transaction resource parts
    internal class TransactionDeactivationPhaseReference
    {
        public TransactionDeactivationPhase DeactivationPhase = TransactionDeactivationPhase.None;
    }

    public enum TransactionDeactivationPhase
    {
        None,
        AfterCommitReadOnly,
        AfterPrepare,
        AfterPrepareAndCommit,
        AfterAbort,
        AfterPrepared,
        AfterCancel,
        AfterConfirm,
        AfterPing
    }

    public interface IDeactivationTransactionalState<TState> : ITransactionalState<TState> where TState : class, new()
    {
        TransactionDeactivationPhase DeactivationPhase { get; set; }
    }

    internal class DeactivationTransactionalState<TState> : IDeactivationTransactionalState<TState>, ILifecycleParticipant<IGrainLifecycle>
        where TState : class, new()
    {
        private readonly IGrainRuntime grainRuntime;
        private readonly TransactionalState<TState> txState;
        private readonly ILogger logger;
        private readonly IGrainActivationContext context;
        private TransactionDeactivationPhaseReference deactivationPhaseReference;
        public TransactionDeactivationPhase DeactivationPhase {
            get =>  deactivationPhaseReference.DeactivationPhase;
            set => deactivationPhaseReference.DeactivationPhase = value; 
        }
        public string CurrentTransactionId => this.txState.CurrentTransactionId;
        public DeactivationTransactionalState(TransactionalState<TState> txState, IGrainActivationContext activationContext, IGrainRuntime grainRuntime, ILogger<DeactivationTransactionalState<TState>> logger)
        {
            this.grainRuntime = grainRuntime;
            this.txState = txState;
            this.logger = logger;
            this.context = activationContext;
            this.deactivationPhaseReference = new TransactionDeactivationPhaseReference();
        }

        public void Participate(IGrainLifecycle lifecycle)
        {
            lifecycle.Subscribe<DeactivationTransactionalState<TState>>(GrainLifecycleStage.SetupState,
                (ct) => this.txState.OnSetupState(ct, this.SetupResourceFactory));
        }

        internal void SetupResourceFactory(IGrainActivationContext context, string stateName, TransactionQueue<TState> queue)
        {
            // Add resources factory to the grain context
            context.RegisterResourceFactory<ITransactionalResource>(stateName, () => new DeactivationTransactionalResource<TState>(deactivationPhaseReference, new TransactionalResource<TState>(queue), context, logger,  grainRuntime));

            // Add tm factory to the grain context
            context.RegisterResourceFactory<ITransactionManager>(stateName, () => new DeactivationTransactionTransactionManager<TState>(deactivationPhaseReference, new TransactionManager<TState>(queue), context, logger, grainRuntime));
        }

        public Task<TResult> PerformRead<TResult>(Func<TState, TResult> readFunction)
        {
            return this.txState.PerformRead(readFunction);
        }

        public Task<TResult> PerformUpdate<TResult>(Func<TState, TResult> updateFunction)
        {
            return this.txState.PerformUpdate(updateFunction);
        }
    }
}
