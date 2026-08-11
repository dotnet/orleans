using System;
using System.Collections.Immutable;
using System.Threading;
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

        internal bool TryConsume(TransactionFaultInjectPhase phase, out FaultInjectionType injectionType)
        {
            if (this.FaultInjectionPhase != phase)
            {
                injectionType = FaultInjectionType.None;
                return false;
            }

            injectionType = this.FaultInjectionType;
            this.Reset();
            return true;
        }
    }

    internal readonly record struct FaultInjectionEvent(
        GrainId GrainId,
        Guid TransactionId,
        TransactionFaultInjectPhase Phase,
        FaultInjectionType Type);

    internal static class FaultInjectionDiagnosticEvents
    {
        private static readonly object LockObj = new();
        private static ImmutableArray<Action<FaultInjectionEvent>> observers = [];

        public static IDisposable Subscribe(Action<FaultInjectionEvent> observer)
        {
            lock (LockObj)
            {
                observers = observers.Add(observer);
            }

            return new Subscription(observer);
        }

        public static void Emit(FaultInjectionEvent evt)
        {
            ImmutableArray<Action<FaultInjectionEvent>> snapshot;
            lock (LockObj)
            {
                snapshot = observers;
            }

            foreach (var observer in snapshot)
            {
                observer(evt);
            }
        }

        private sealed class Subscription(Action<FaultInjectionEvent> observer) : IDisposable
        {
            private Action<FaultInjectionEvent>? observer = observer;

            public void Dispose()
            {
                var value = Interlocked.Exchange(ref this.observer, null);
                if (value is null)
                {
                    return;
                }

                lock (LockObj)
                {
                    observers = observers.Remove(value);
                }
            }
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
        ExceptionAfterStore,
        GenericExceptionAfterStore
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
        private readonly IControlledTransactionFaultInjector faultInjector;
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
                (ct) => this.txState.OnSetupState(this.SetupResourceFactory, ct));
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
