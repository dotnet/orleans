using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.GrainReferences;
using Orleans.Runtime.Scheduler;

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
        internal ActivationAddress ActivationAddress { get; }

        GrainId ISystemTargetBase.GrainId => id.GrainId;
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

        public GrainReference GrainReference => selfReference ??= this.RuntimeClient.ServiceProvider.GetRequiredService<GrainReferenceActivator>().CreateReference(this.id.GrainId, default);

        GrainId IGrainContext.GrainId => this.id.GrainId;

        IAddressable IGrainContext.GrainInstance => this;

        ActivationId IGrainContext.ActivationId => this.ActivationId;

        ActivationAddress IGrainContext.Address => this.ActivationAddress;
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
            this.ActivationAddress = ActivationAddress.GetAddress(this.Silo, this.id.GrainId, this.ActivationId);
            this.IsLowPriority = lowPriority;
            this.ActivationId = ActivationId.GetDeterministic(grainId.GrainId);
            this.timerLogger = loggerFactory.CreateLogger<GrainTimer>();
            this.logger = loggerFactory.CreateLogger(this.GetType());
        }

        public bool IsLowPriority { get; }

        internal WorkItemGroup WorkItemGroup { get; set; }

        public IServiceProvider ActivationServices => this.RuntimeClient.ServiceProvider;

        IGrainLifecycle IGrainContext.ObservableLifecycle => throw new NotImplementedException("IGrainContext.ObservableLifecycle is not implemented by SystemTarget");

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
        /// Register a timer to send regular callbacks to this grain.
        /// This timer will keep the current grain from being deactivated.
        /// </summary>
        /// <param name="asyncCallback"></param>
        /// <param name="state"></param>
        /// <param name="dueTime"></param>
        /// <param name="period"></param>
        /// <param name="name"></param>
        /// <returns></returns>
        public IDisposable RegisterTimer(Func<object, Task> asyncCallback, object state, TimeSpan dueTime, TimeSpan period, string name = null)
        {
            var ctxt = RuntimeContext.CurrentGrainContext;
            this.RuntimeClient.Scheduler.CheckSchedulingContextValidity(ctxt);
            name = name ?? ctxt.GrainId + "Timer";

            var timer = GrainTimer.FromTaskCallback(this.RuntimeClient.Scheduler, this.timerLogger, asyncCallback, state, dueTime, period, name);
            timer.Start();
            return timer;
        }

        /// <summary>Override of object.ToString()</summary>
        public override string ToString()
        {
            return $"[{(IsLowPriority ? "LowPriority" : string.Empty)}SystemTarget: {Silo}/{this.id.ToString()}{this.ActivationId}]";
        }

        /// <summary>Adds details about message currently being processed</summary>
        internal string ToDetailedString()
        {
            return String.Format("{0} CurrentlyExecuting={1}", ToString(), running != null ? running.ToString() : "null");
        }

        bool IEquatable<IGrainContext>.Equals(IGrainContext other) => ReferenceEquals(this, other);

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
    }
}
