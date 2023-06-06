#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Core;
using Orleans.Runtime;
using Orleans.Serialization.TypeSystem;

namespace Orleans
{
    /// <summary>
    /// The abstract base class for all grain classes.
    /// </summary>
    public abstract class Grain : IGrainBase, IAddressable
    {
        // Do not use this directly because we currently don't provide a way to inject it;
        // any interaction with it will result in non unit-testable code. Any behaviour that can be accessed
        // from within client code (including subclasses of this class), should be exposed through IGrainRuntime.
        // The better solution is to refactor this interface and make it injectable through the constructor.
        internal IGrainContext GrainContext { get; private set; }

        IGrainContext IGrainBase.GrainContext => GrainContext;

        public GrainReference GrainReference { get { return GrainContext.GrainReference; } }

        internal IGrainRuntime Runtime { get; }

        /// <summary>
        /// Gets an object which can be used to access other grains. Null if this grain is not associated with a Runtime, such as when created directly for unit testing.
        /// </summary>
        protected IGrainFactory GrainFactory => Runtime.GrainFactory;

        /// <summary>
        /// Gets the IServiceProvider managed by the runtime. Null if this grain is not associated with a Runtime, such as when created directly for unit testing.
        /// </summary>
        protected internal IServiceProvider ServiceProvider => GrainContext?.ActivationServices ?? Runtime?.ServiceProvider!;

        internal GrainId GrainId => GrainContext.GrainId;

        /// <summary>
        /// This constructor should never be invoked. We expose it so that client code (subclasses of Grain) do not have to add a constructor.
        /// Client code should use the GrainFactory property to get a reference to a Grain.
        /// </summary>
        protected Grain() : this(RuntimeContext.Current, grainRuntime: null)
        {}

        /// <summary>
        /// Grain implementers do NOT have to expose this constructor but can choose to do so.
        /// This constructor is particularly useful for unit testing where test code can create a Grain and replace
        /// the IGrainIdentity and IGrainRuntime with test doubles (mocks/stubs).
        /// </summary>
        protected Grain(IGrainContext grainContext, IGrainRuntime? grainRuntime = null)
        {
            GrainContext = grainContext;
            Runtime = grainRuntime ?? grainContext?.ActivationServices.GetService<IGrainRuntime>()!;
        }

        /// <summary>
        /// String representation of grain's SiloIdentity including type and primary key.
        /// </summary>
        public string IdentityString => GrainId.ToString();

        /// <summary>
        /// A unique identifier for the current silo.
        /// There is no semantic content to this string, but it may be useful for logging.
        /// </summary>
        public string RuntimeIdentity => Runtime?.SiloIdentity ?? string.Empty;

        /// <summary>
        /// Registers a timer to send periodic callbacks to this grain.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This timer will not prevent the current grain from being deactivated.
        /// If the grain is deactivated, then the timer will be discarded.
        /// </para>
        /// <para>
        /// Until the Task returned from the asyncCallback is resolved,
        /// the next timer tick will not be scheduled.
        /// That is to say, timer callbacks never interleave their turns.
        /// </para>
        /// <para>
        /// The timer may be stopped at any time by calling the <c>Dispose</c> method
        /// on the timer handle returned from this call.
        /// </para>
        /// <para>
        /// Any exceptions thrown by or faulted Task's returned from the asyncCallback
        /// will be logged, but will not prevent the next timer tick from being queued.
        /// </para>
        /// </remarks>
        /// <param name="asyncCallback">Callback function to be invoked when timer ticks.</param>
        /// <param name="state">State object that will be passed as argument when calling the asyncCallback.</param>
        /// <param name="dueTime">Due time for first timer tick.</param>
        /// <param name="period">Period of subsequent timer ticks.</param>
        /// <returns>Handle for this Timer.</returns>
        /// <seealso cref="IDisposable"/>
        protected IDisposable RegisterTimer(Func<object, Task> asyncCallback, object state, TimeSpan dueTime, TimeSpan period)
        {
            if (asyncCallback == null)
                throw new ArgumentNullException(nameof(asyncCallback));

            EnsureRuntime();
            return Runtime.TimerRegistry.RegisterTimer(GrainContext ?? RuntimeContext.Current, asyncCallback, state, dueTime, period);
        }

        /// <summary>
        /// Deactivate this activation of the grain after the current grain method call is completed.
        /// This call will mark this activation of the current grain to be deactivated and removed at the end of the current method.
        /// The next call to this grain will result in a different activation to be used, which typical means a new activation will be created automatically by the runtime.
        /// </summary>
        protected void DeactivateOnIdle()
        {
            EnsureRuntime();
            Runtime.DeactivateOnIdle(GrainContext ?? RuntimeContext.Current);
        }

        /// <summary>
        /// Starts an attempt to migrating this instance to another location.
        /// Migration captures the current <see cref="RequestContext"/>, making it available to the activation's placement director so that it can consider it when selecting a new location.
        /// Migration will occur asynchronously, when no requests are executing, and will not occur if the activation's placement director does not select an alternative location.
        /// </summary>
        protected void MigrateOnIdle()
        {
            EnsureRuntime();
            ((IGrainBase)this).MigrateOnIdle();
        }

        /// <summary>
        /// Delay Deactivation of this activation at least for the specified time duration.
        /// A positive <c>timeSpan</c> value means “prevent GC of this activation for that time span”.
        /// A negative <c>timeSpan</c> value means “cancel the previous setting of the DelayDeactivation call and make this activation behave based on the regular Activation Garbage Collection settings”.
        /// DeactivateOnIdle method would undo / override any current “keep alive” setting,
        /// making this grain immediately available for deactivation.
        /// </summary>
        protected void DelayDeactivation(TimeSpan timeSpan)
        {
            EnsureRuntime();
            Runtime.DelayDeactivation(GrainContext ?? RuntimeContext.Current, timeSpan);
        }

        /// <summary>
        /// This method is called at the end of the process of activating a grain.
        /// It is called before any messages have been dispatched to the grain.
        /// For grains with declared persistent state, this method is called after the State property has been populated.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token which signals when activation is being canceled.</param>
        public virtual Task OnActivateAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        /// <summary>
        /// This method is called at the beginning of the process of deactivating a grain.
        /// </summary>
        /// <param name="reason">The reason for deactivation. Informational only.</param>
        /// <param name="cancellationToken">A cancellation token which signals when deactivation should complete promptly.</param>
        public virtual Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken) => Task.CompletedTask;

        private void EnsureRuntime()
        {
            if (Runtime == null)
            {
                throw new InvalidOperationException("Grain was created outside of the Orleans creation process and no runtime was specified.");
            }
        }
    }

    /// <summary>
    /// Base class for a Grain with declared persistent state.
    /// </summary>
    /// <typeparam name="TGrainState">The class of the persistent state object</typeparam>
    public class Grain<TGrainState> : Grain
    {
        /// <summary>
        /// The underlying state storage.
        /// </summary>
        private IStorage<TGrainState>? _storage;

        /// <summary>
        /// Initializes a new instance of the <see cref="Grain{TGrainState}"/> class.
        /// </summary>
        /// <remarks>
        /// This constructor should never be invoked. We expose it so that client code (subclasses of this class) do not have to add a constructor.
        /// Client code should use the GrainFactory to get a reference to a Grain.
        /// </remarks>
        protected Grain()
        {
            var observer = new LifecycleObserver(this);
            var lifecycle = RuntimeContext.Current.ObservableLifecycle;
            lifecycle.AddMigrationParticipant(observer);
            lifecycle.Subscribe(RuntimeTypeNameFormatter.Format(GetType()), GrainLifecycleStage.SetupState, observer);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Grain{TGrainState}"/> class.
        /// </summary>
        /// <param name="storage">
        /// The storage implementation.
        /// </param>
        /// <remarks>
        /// Grain implementers do NOT have to expose this constructor but can choose to do so.
        /// This constructor is particularly useful for unit testing where test code can create a Grain and replace
        /// the IGrainIdentity, IGrainRuntime and State with test doubles (mocks/stubs).
        /// </remarks>
        protected Grain(IStorage<TGrainState> storage)
        {
            _storage = storage;
        }

        /// <summary>
        /// Gets or sets the grain state.
        /// </summary>
        protected TGrainState State
        {
            get => _storage!.State;
            set => _storage!.State = value;
        }

        /// <summary>
        /// Clears the current grain state data from backing store.
        /// </summary>
        /// <returns>
        /// A <see cref="Task"/> representing the operation.
        /// </returns>
        protected virtual Task ClearStateAsync() => _storage!.ClearStateAsync();

        /// <summary>
        /// Write the current grain state data into the backing store.
        /// </summary>
        /// <returns>
        /// A <see cref="Task"/> representing the operation.
        /// </returns>
        protected virtual Task WriteStateAsync() => _storage!.WriteStateAsync();

        /// <summary>
        /// Reads grain state from backing store, updating <see cref="State"/>.
        /// </summary>
        /// <remarks>
        /// Any previous contents of the grain state data will be overwritten.
        /// </remarks>
        /// <returns>
        /// A <see cref="Task"/> representing the operation.
        /// </returns>
        protected virtual Task ReadStateAsync() => _storage!.ReadStateAsync();

        private class LifecycleObserver : ILifecycleObserver, IGrainMigrationParticipant
        {
            private readonly Grain<TGrainState> _grain;

            public LifecycleObserver(Grain<TGrainState> grain) => _grain = grain;

            private void SetupStorage() => _grain._storage ??= _grain.Runtime.GetStorage<TGrainState>(_grain.GrainContext);

            public void OnDehydrate(IDehydrationContext dehydrationContext) => (_grain._storage as IGrainMigrationParticipant)?.OnDehydrate(dehydrationContext);

            public void OnRehydrate(IRehydrationContext rehydrationContext)
            {
                SetupStorage();
                (_grain._storage as IGrainMigrationParticipant)?.OnRehydrate(rehydrationContext);
            }

            public Task OnStart(CancellationToken cancellationToken = default)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return Task.CompletedTask;
                }

                // Avoid reading the state if it is already present because of rehydration
                if (_grain._storage?.Etag is not null)
                {
                    return Task.CompletedTask;
                }

                SetupStorage();
                return _grain.ReadStateAsync();
            }

            public Task OnStop(CancellationToken cancellationToken = default) => Task.CompletedTask;
        }
    }
}
