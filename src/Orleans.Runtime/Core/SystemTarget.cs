using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.GrainReferences;
using Orleans.Runtime.Scheduler;
using Orleans.Serialization.Invocation;
using Orleans.Timers;

namespace Orleans.Runtime
{
    /// <summary>
    /// Base class for various system services, such as grain directory, reminder service, etc.
    /// Made public for GrainService to inherit from it.
    /// Can be turned to internal after a refactoring that would remove the inheritance relation.
    /// </summary>
    public abstract class SystemTarget : ISystemTarget, ISystemTargetBase, IGrainContext, IGrainExtensionBinder, ISpanFormattable, IDisposable, IGrainTimerRegistry
    {
        private readonly SystemTargetGrainId id;
        private readonly HashSet<IGrainTimer> _timers = [];
        private GrainReference selfReference;
        private Message running;
        private Dictionary<Type, object> _components = new Dictionary<Type, object>();

        /// <summary>Silo address of the system target.</summary>
        public SiloAddress Silo { get; }
        internal GrainAddress ActivationAddress { get; }

        internal ActivationId ActivationId { get; set; }
        private InsideRuntimeClient runtimeClient;
        private RuntimeMessagingTrace messagingTrace;
        private readonly ILogger logger;

        internal InsideRuntimeClient RuntimeClient
        {
            get
            {
                if (this.runtimeClient == null)
                    throw new OrleansException(
                        $"{nameof(this.RuntimeClient)} has not been set on {this.GetType()}. Most likely, this means that the system target was not registered.");
                return this.runtimeClient;
            }
            set { this.runtimeClient = value; }
        }

        /// <inheritdoc/>
        public GrainReference GrainReference => selfReference ??= this.RuntimeClient.ServiceProvider.GetRequiredService<GrainReferenceActivator>().CreateReference(this.id.GrainId, default);

        /// <inheritdoc/>
        public GrainId GrainId => this.id.GrainId;

        /// <inheritdoc/>
        object IGrainContext.GrainInstance => this;

        /// <inheritdoc/>
        ActivationId IGrainContext.ActivationId => this.ActivationId;

        /// <inheritdoc/>
        GrainAddress IGrainContext.Address => this.ActivationAddress;

        private RuntimeMessagingTrace MessagingTrace => this.messagingTrace ??= this.RuntimeClient.ServiceProvider.GetRequiredService<RuntimeMessagingTrace>();

        /// <summary>Only needed to make Reflection happy.</summary>
        protected SystemTarget()
        {
        }

        internal SystemTarget(GrainType grainType, SiloAddress siloAddress, ILoggerFactory loggerFactory)
            : this(SystemTargetGrainId.Create(grainType, siloAddress), siloAddress, loggerFactory)
        {
        }

        internal SystemTarget(SystemTargetGrainId grainId, SiloAddress silo, ILoggerFactory loggerFactory)
        {
            this.id = grainId;
            this.Silo = silo;
            this.ActivationId = ActivationId.GetDeterministic(grainId.GrainId);
            this.ActivationAddress = GrainAddress.GetAddress(this.Silo, this.id.GrainId, this.ActivationId);
            this.logger = loggerFactory.CreateLogger(this.GetType());

            if (!Constants.IsSingletonSystemTarget(GrainId.Type))
            {
                GrainInstruments.IncrementSystemTargetCounts(Constants.SystemTargetName(GrainId.Type));
            }
        }

        internal WorkItemGroup WorkItemGroup { get; set; }

        /// <inheritdoc />
        public IServiceProvider ActivationServices => this.RuntimeClient.ServiceProvider;

        /// <inheritdoc />
        IGrainLifecycle IGrainContext.ObservableLifecycle => throw new NotImplementedException("IGrainContext.ObservableLifecycle is not implemented by SystemTarget");

        /// <inheritdoc />
        public IWorkItemScheduler Scheduler => WorkItemGroup;

        /// <summary>
        /// Gets the component with the specified type.
        /// </summary>
        /// <typeparam name="TComponent">The component type.</typeparam>
        /// <returns>The component with the specified type.</returns>
        public TComponent GetComponent<TComponent>()
        {
            TComponent result;
            if (this is TComponent instanceResult)
            {
                result = instanceResult;
            }
            else if (_components.TryGetValue(typeof(TComponent), out var resultObj))
            {
                result = (TComponent)resultObj;
            }
            else if (typeof(TComponent) == typeof(PlacementStrategy))
            {
                result = (TComponent)(object)SystemTargetPlacementStrategy.Instance;
            }
            else
            {
                result = default;
            }

            return result;
        }

        /// <inheritdoc />
        public void SetComponent<TComponent>(TComponent instance) where TComponent : class
        {
            if (this is TComponent)
            {
                throw new ArgumentException("Cannot override a component which is implemented by this grain");
            }

            if (instance == null)
            {
                _components?.Remove(typeof(TComponent));
                return;
            }

            if (_components is null) _components = new Dictionary<Type, object>();
            _components[typeof(TComponent)] = instance;
        }

        internal void HandleNewRequest(Message request)
        {
            running = request;
            this.RuntimeClient.Invoke(this, request).Ignore();
        }

        internal void HandleResponse(Message response)
        {
            running = response;
            this.RuntimeClient.ReceiveResponse(response);
        }

        /// <summary>
        /// Registers a timer to send regular callbacks to this grain.
        /// This timer will keep the current grain from being deactivated.
        /// </summary>
        /// <param name="callback">The timer callback, which will fire whenever the timer becomes due.</param>
        /// <param name="state">The state object passed to the callback.</param>
        /// <param name="dueTime">
        /// The amount of time to delay before the <paramref name="callback"/> is invoked.
        /// Specify <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> to prevent the timer from starting.
        /// Specify <see cref="TimeSpan.Zero"/> to invoke the callback promptly.
        /// </param>
        /// <param name="period">
        /// The time interval between invocations of <paramref name="callback"/>.
        /// Specify <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> to disable periodic signaling.
        /// </param>
        /// <returns>
        /// An <see cref="IDisposable"/> object which will cancel the timer upon disposal.
        /// </returns>
        public IGrainTimer RegisterTimer(Func<object, Task> callback, object state, TimeSpan dueTime, TimeSpan period)
        {
            var ctxt = RuntimeContext.Current;
            ArgumentNullException.ThrowIfNull(callback);
            var timer = this.ActivationServices.GetRequiredService<ITimerRegistry>()
                .RegisterGrainTimer(this, static (state, _) => state.Callback(state.State), (Callback: callback, State: state), new() { DueTime = dueTime, Period = period, Interleave = true });
            return timer;
        }

        /// <inheritdoc/>
        public sealed override string ToString() => $"{this}";

        string IFormattable.ToString(string format, IFormatProvider formatProvider) => ToString();

        bool ISpanFormattable.TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider provider)
            => destination.TryWrite($"[SystemTarget: {Silo}/{id}{ActivationId}]", out charsWritten);

        /// <summary>Adds details about message currently being processed</summary>
        internal string ToDetailedString() => $"{this} CurrentlyExecuting={running}{(running != null ? null : "null")}";

        /// <inheritdoc/>
        bool IEquatable<IGrainContext>.Equals(IGrainContext other) => ReferenceEquals(this, other);

        /// <inheritdoc/>
        public (TExtension, TExtensionInterface) GetOrSetExtension<TExtension, TExtensionInterface>(Func<TExtension> newExtensionFunc)
            where TExtension : class, TExtensionInterface
            where TExtensionInterface : class, IGrainExtension
        {
            TExtension implementation;
            if (this.GetComponent<TExtensionInterface>() is object existing)
            {
                if (existing is TExtension typedResult)
                {
                    implementation = typedResult;
                }
                else
                {
                    throw new InvalidCastException($"Cannot cast existing extension of type {existing.GetType()} to target type {typeof(TExtension)}");
                }
            }
            else
            {
                implementation = newExtensionFunc();
                this.SetComponent<TExtensionInterface>(implementation);
            }

            var reference = this.GrainReference.Cast<TExtensionInterface>();
            return (implementation, reference);
        }

        /// <inheritdoc/>
        TComponent ITargetHolder.GetComponent<TComponent>()
        {
            var result = this.GetComponent<TComponent>();
            if (result is null && typeof(IGrainExtension).IsAssignableFrom(typeof(TComponent)))
            {
                var implementation = this.ActivationServices.GetKeyedService<IGrainExtension>(typeof(TComponent));
                if (implementation is not TComponent typedResult)
                {
                    throw new GrainExtensionNotInstalledException($"No extension of type {typeof(TComponent)} is installed on this instance and no implementations are registered for automated install");
                }

                this.SetComponent<TComponent>(typedResult);
                result = typedResult;
            }

            return result;
        }

        /// <inheritdoc/>
        public TExtensionInterface GetExtension<TExtensionInterface>()
            where TExtensionInterface : class, IGrainExtension
        {
            if (this.GetComponent<TExtensionInterface>() is TExtensionInterface result)
            {
                return result;
            }

            var implementation = this.ActivationServices.GetKeyedService<IGrainExtension>(typeof(TExtensionInterface));
            if (!(implementation is TExtensionInterface typedResult))
            {
                throw new GrainExtensionNotInstalledException($"No extension of type {typeof(TExtensionInterface)} is installed on this instance and no implementations are registered for automated install");
            }

            this.SetComponent<TExtensionInterface>(typedResult);
            return typedResult;
        }

        /// <inheritdoc/>
        public void ReceiveMessage(object message)
        {
            var msg = (Message)message;
            switch (msg.Direction)
            {
                case Message.Directions.Request:
                case Message.Directions.OneWay:
                    {
                        this.MessagingTrace.OnEnqueueMessageOnActivation(msg, this);
                        var workItem = new RequestWorkItem(this, msg);
                        this.WorkItemGroup.QueueWorkItem(workItem);
                        break;
                    }

                default:
                    this.logger.LogError((int)ErrorCode.Runtime_Error_100097, "Invalid message: {Message}", msg);
                    break;
            }
        }

        /// <inheritdoc/>
        public TTarget GetTarget<TTarget>() where TTarget : class => (TTarget)(object)this;

        /// <inheritdoc/>
        public void Activate(Dictionary<string, object> requestContext, CancellationToken cancellationToken) { }

        /// <inheritdoc/>
        public void Deactivate(DeactivationReason deactivationReason, CancellationToken cancellationToken) { }

        /// <inheritdoc/>
        public Task Deactivated => Task.CompletedTask;

        public void Dispose()
        {
            if (!Constants.IsSingletonSystemTarget(GrainId.Type))
            {
                GrainInstruments.DecrementSystemTargetCounts(Constants.SystemTargetName(GrainId.Type));
            }

            StopAllTimers();
        }

        public void Rehydrate(IRehydrationContext context)
        {
            // Migration is not supported, but we need to dispose of the context if it's provided
            (context as IDisposable)?.Dispose();
        }

        public void Migrate(Dictionary<string, object> requestContext, CancellationToken cancellationToken)
        {
            // Migration is not supported. Do nothing: the contract is that this method attempts migration, but does not guarantee it will occur.
        }

        void IGrainTimerRegistry.OnTimerCreated(IGrainTimer timer) { lock (_timers) { _timers.Add(timer); } }
        void IGrainTimerRegistry.OnTimerDisposed(IGrainTimer timer) { lock (_timers) { _timers.Remove(timer); } }
        private void StopAllTimers()
        {
            List<IGrainTimer> timers;
            lock (_timers)
            {
                timers = _timers.ToList();
                _timers.Clear();
            }

            foreach (var timer in timers)
            {
                timer.Dispose();
            }
        }
    }
}
