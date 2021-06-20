using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.State;

namespace Orleans.Transactions.TestKit
{
    [GenerateSerializer]
    public class FaultInjectionControl
    {
        [Id(0)]
        public TransactionFaultInjectPhase FaultInjectionPhase = TransactionFaultInjectPhase.None;

        [Id(1)]
        public FaultInjectionType FaultInjectionType = FaultInjectionType.None;

        public void Reset()
        {
            this.FaultInjectionType = FaultInjectionType.None;
            this.FaultInjectionPhase = TransactionFaultInjectPhase.None;
        }
    }

    [GenerateSerializer]
    public enum TransactionFaultInjectPhase
    {
        None,
        //deactivation injection phase
        AfterCommitReadOnly,
        AfterPrepare,
        AfterPrepareAndCommit,
        AfterAbort,
        AfterPrepared,
        AfterCancel,
        AfterConfirm,
        AfterPing,

        //storage exception injection phase
        BeforeConfirm,
        BeforePrepare,
        BeforePrepareAndCommit
    }

    public enum FaultInjectionType
    {
        None, 
        Deactivation,
        ExceptionBeforeStore,
        ExceptionAfterStore
    }

    public interface IFaultInjectionTransactionalState<TState> : ITransactionalState<TState> where TState : class, new()
    {
        FaultInjectionControl FaultInjectionControl { get; set; }
    }

    internal class FaultInjectionTransactionalState<TState> : IFaultInjectionTransactionalState<TState>, ILifecycleParticipant<IGrainLifecycle>
        where TState : class, new()
    {
        private readonly IGrainRuntime grainRuntime;
        private readonly TransactionalState<TState> txState;
        private readonly ILogger logger;
        public FaultInjectionControl FaultInjectionControl { get; set; }
        private IControlledTransactionFaultInjector faultInjector;
        public string CurrentTransactionId => this.txState.CurrentTransactionId;
        public FaultInjectionTransactionalState(TransactionalState<TState> txState, IControlledTransactionFaultInjector faultInjector, IGrainRuntime grainRuntime, ILogger<FaultInjectionTransactionalState<TState>> logger)
        {
            this.grainRuntime = grainRuntime;
            this.txState = txState;
            this.logger = logger;
            this.FaultInjectionControl = new FaultInjectionControl();
            this.faultInjector = faultInjector;
        }

        public void Participate(IGrainLifecycle lifecycle)
        {
            lifecycle.Subscribe<FaultInjectionTransactionalState<TState>>(GrainLifecycleStage.SetupState,
                (ct) => this.txState.OnSetupState(ct, this.SetupResourceFactory));
        }

        internal void SetupResourceFactory(IGrainContext context, string stateName, TransactionQueue<TState> queue)
        {
            // Add resources factory to the grain context
            context.RegisterResourceFactory<ITransactionalResource>(stateName, () => new FaultInjectionTransactionalResource<TState>(this.faultInjector, FaultInjectionControl, new TransactionalResource<TState>(queue), context, logger,  grainRuntime));

            // Add tm factory to the grain context
            context.RegisterResourceFactory<ITransactionManager>(stateName, () => new FaultInjectionTransactionManager<TState>(this.faultInjector, FaultInjectionControl, new TransactionManager<TState>(queue), context, logger, grainRuntime));
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
