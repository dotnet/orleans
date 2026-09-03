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
    /// <summary>
    /// Configures a fault to inject during the next matching transaction phase.
    /// </summary>
    [GenerateSerializer]
    public class FaultInjectionControl
    {
        /// <summary>
        /// Gets or sets the transaction phase during which the fault is injected.
        /// </summary>
        [Id(0)]
        public TransactionFaultInjectPhase FaultInjectionPhase = TransactionFaultInjectPhase.None;

        /// <summary>
        /// Gets or sets the type of fault to inject.
        /// </summary>
        [Id(1)]
        public FaultInjectionType FaultInjectionType = FaultInjectionType.None;

        /// <summary>
        /// Clears the configured fault injection.
        /// </summary>
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

    /// <summary>
    /// Identifies a transaction phase where a fault can be injected.
    /// </summary>
    [GenerateSerializer]
    public enum TransactionFaultInjectPhase
    {
        /// <summary>
        /// No transaction phase is selected.
        /// </summary>
        None,

        /// <summary>
        /// Injects a fault after committing a read-only transaction.
        /// </summary>
        AfterCommitReadOnly,

        /// <summary>
        /// Injects a fault after preparing a transaction.
        /// </summary>
        AfterPrepare,

        /// <summary>
        /// Injects a fault after preparing and committing a transaction.
        /// </summary>
        AfterPrepareAndCommit,

        /// <summary>
        /// Injects a fault after aborting a transaction.
        /// </summary>
        AfterAbort,

        /// <summary>
        /// Injects a fault after receiving a prepared notification.
        /// </summary>
        AfterPrepared,

        /// <summary>
        /// Injects a fault after canceling a transaction.
        /// </summary>
        AfterCancel,

        /// <summary>
        /// Injects a fault after confirming a transaction.
        /// </summary>
        AfterConfirm,

        /// <summary>
        /// Injects a fault after processing a transaction ping.
        /// </summary>
        AfterPing,

        /// <summary>
        /// Injects a fault before confirming a transaction.
        /// </summary>
        BeforeConfirm,

        /// <summary>
        /// Injects a fault before preparing a transaction.
        /// </summary>
        BeforePrepare,

        /// <summary>
        /// Injects a fault before preparing and committing a transaction.
        /// </summary>
        BeforePrepareAndCommit
    }

    /// <summary>
    /// Identifies the fault to inject into a transaction.
    /// </summary>
    public enum FaultInjectionType
    {
<<<<<<< HEAD
        /// <summary>
        /// No fault is injected.
        /// </summary>
        None,

        /// <summary>
        /// Deactivates the grain after the selected transaction phase.
        /// </summary>
||||||| parent of 82a763ec4 (style: format solution whitespace)
        None, 
=======
        None,
>>>>>>> 82a763ec4 (style: format solution whitespace)
        Deactivation,

        /// <summary>
        /// Throws a storage exception before storing transactional state.
        /// </summary>
        ExceptionBeforeStore,

        /// <summary>
        /// Throws a storage exception after storing transactional state.
        /// </summary>
        ExceptionAfterStore,

        /// <summary>
        /// Throws a generic exception after storing transactional state.
        /// </summary>
        GenericExceptionAfterStore
    }

    /// <summary>
    /// Provides transactional state whose operations can inject controlled faults.
    /// </summary>
    /// <typeparam name="TState">The state type.</typeparam>
    public interface IFaultInjectionTransactionalState<TState> : ITransactionalState<TState> where TState : class, new()
    {
        /// <summary>
        /// Gets or sets the fault injection configuration.
        /// </summary>
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
            context.RegisterResourceFactory<ITransactionalResource>(stateName, () => new FaultInjectionTransactionalResource<TState>(this.faultInjector, FaultInjectionControl, new TransactionalResource<TState>(queue), context, logger, grainRuntime));

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
