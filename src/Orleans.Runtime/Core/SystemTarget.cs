using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.GrainReferences;
using Orleans.Runtime.Scheduler;
using Orleans.Serialization.Invocation;

namespace Orleans.Runtime
{
    /// <summary>
    /// Base class for various system services, such as grain directory, reminder service, etc.
    /// Made public for GrainSerive to inherit from it.
    /// Can be turned to internal after a refactoring that would remove the inheritance relation.
    /// </summary>
    public abstract class SystemTarget : ISystemTarget, ISystemTargetBase, IGrainContext, IGrainExtensionBinder
    {
        private readonly SystemTargetGrainId id;
        private GrainReference selfReference;
        private Message running;
        private Dictionary<Type, object> _components = new Dictionary<Type, object>();

        /// <summary>Silo address of the system target.</summary>
        public SiloAddress Silo { get; }
        internal GrainAddress ActivationAddress { get; }

        internal ActivationId ActivationId { get; set; }
        private InsideRuntimeClient runtimeClient;
        private RuntimeMessagingTrace messagingTrace;
        private readonly ILogger timerLogger;
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
        GrainId IGrainContext.GrainId => this.id.GrainId;

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

        internal SystemTarget(GrainType grainType, SiloAddress silo, ILoggerFactory loggerFactory)
            : this(SystemTargetGrainId.Create(grainType, silo), silo, false, loggerFactory)
        {
        }

        internal SystemTarget(GrainType grainType, SiloAddress silo, bool lowPriority, ILoggerFactory loggerFactory)
            : this(SystemTargetGrainId.Create(grainType, silo), silo, lowPriority, loggerFactory)
        {
        }

        internal SystemTarget(SystemTargetGrainId grainId, SiloAddress silo, bool lowPriority, ILoggerFactory loggerFactory)
        {
            this.id = grainId;
            this.Silo = silo;
            this.ActivationAddress = GrainAddress.GetAddress(this.Silo, this.id.GrainId, this.ActivationId);
            this.IsLowPriority = lowPriority;
            this.ActivationId = ActivationId.GetDeterministic(grainId.GrainId);
            this.timerLogger = loggerFactory.CreateLogger<GrainTimer>();
            this.logger = loggerFactory.CreateLogger(this.GetType());
        }

        internal bool IsLowPriority { get; }

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
            else
            {
                result = default;
            }

            return result;
        }

        /// <inheritdoc />
        public void SetComponent<TComponent>(TComponent instance)
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
        /// <param name="asyncCallback">The timer callback, which will fire whenever the timer becomes due.</param>
        /// <param name="state">The state object passed to the callback.</param>
        /// <param name="dueTime">
        /// The amount of time to delay before the <paramref name="asyncCallback"/> is invoked.
        /// Specify <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> to prevent the timer from starting.
        /// Specify <see cref="TimeSpan.Zero"/> to invoke the callback promptly.
        /// </param>
        /// <param name="period">
        /// The time interval between invocations of <paramref name="asyncCallback"/>.
        /// Specify <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> to disable periodic signalling.
        /// </param>
        /// <param name="name">The timer name.</param>
        /// <returns>
        /// An <see cref="IDisposable"/> object which will cancel the timer upon disposal.
        /// </returns>
        public IDisposable RegisterTimer(Func<object, Task> asyncCallback, object state, TimeSpan dueTime, TimeSpan period, string name = null)
            => RegisterGrainTimer(asyncCallback, state, dueTime, period, name);

        /// <summary>
        /// Internal version of <see cref="RegisterTimer(Func{object, Task}, object, TimeSpan, TimeSpan, string)"/> that returns the inner IGrainTimer
        /// </summary>
        internal IGrainTimer RegisterGrainTimer(Func<object, Task> asyncCallback, object state, TimeSpan dueTime, TimeSpan period, string name = null)
        {
            var ctxt = RuntimeContext.Current;
            name = name ?? ctxt.GrainId + "Timer";

            var timer = GrainTimer.FromTaskCallback(this.timerLogger, asyncCallback, state, dueTime, period, name);
            timer.Start();
            return timer;
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return $"[{(IsLowPriority ? "LowPriority" : string.Empty)}SystemTarget: {Silo}/{this.id.ToString()}{this.ActivationId}]";
        }

        /// <summary>Adds details about message currently being processed</summary>
        internal string ToDetailedString()
        {
            return String.Format("{0} CurrentlyExecuting={1}", ToString(), running != null ? running.ToString() : "null");
        }

        /// <inheritdoc/>
        bool IEquatable<IGrainContext>.Equals(IGrainContext other) => ReferenceEquals(this, other);

        /// <inheritdoc/>
        public (TExtension, TExtensionInterface) GetOrSetExtension<TExtension, TExtensionInterface>(Func<TExtension> newExtensionFunc)
            where TExtension : TExtensionInterface
            where TExtensionInterface : IGrainExtension
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
                var implementation = this.ActivationServices.GetServiceByKey<Type, IGrainExtension>(typeof(TComponent));
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
            where TExtensionInterface : IGrainExtension
        {
            if (this.GetComponent<TExtensionInterface>() is TExtensionInterface result)
            {
                return result;
            }

            var implementation = this.ActivationServices.GetServiceByKey<Type, IGrainExtension>(typeof(TExtensionInterface));
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
                    {
                        this.MessagingTrace.OnEnqueueMessageOnActivation(msg, this);
                        var workItem = new RequestWorkItem(this, msg);
                        this.WorkItemGroup.TaskScheduler.QueueWorkItem(workItem);
                        break;
                    }

                case Message.Directions.Response:
                    {
                        this.MessagingTrace.OnEnqueueMessageOnActivation(msg, this);
                        var workItem = new ResponseWorkItem(this, msg);
                        this.WorkItemGroup.TaskScheduler.QueueWorkItem(workItem);
                        break;
                    }

                default:
                    this.logger.LogError((int)ErrorCode.Runtime_Error_100097, "Invalid message: {Message}", msg);
                    break;
            }
        }

        /// <inheritdoc/>
        public TTarget GetTarget<TTarget>() => (TTarget)(object)this;

        /// <inheritdoc/>
        public void Activate(Dictionary<string, object> requestContext, CancellationToken? cancellationToken = null) { }

        /// <inheritdoc/>
        public void Deactivate(DeactivationReason deactivationReason, CancellationToken? cancellationToken = null) { }

        /// <inheritdoc/>
        public Task Deactivated => Task.CompletedTask;
    }
}
