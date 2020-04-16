using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.CodeGeneration;
using Orleans.Runtime.Scheduler;

namespace Orleans.Runtime
{
    /// <summary>
    /// Base class for various system services, such as grain directory, reminder service, etc.
    /// Made public for GrainSerive to inherit from it.
    /// Can be turned to internal after a refactoring that would remove the inheritance relation.
    /// </summary>
    public abstract class SystemTarget : ISystemTarget, ISystemTargetBase, IInvokable, IGrainContext
    {
        private readonly SystemTargetGrainId id;
        private GrainReference selfReference;
        private IGrainMethodInvoker lastInvoker;
        private Message running;

        /// <summary>Silo address of the system target.</summary>
        public SiloAddress Silo { get; }
        internal ActivationAddress ActivationAddress { get; }

        GrainId ISystemTargetBase.GrainId => id.GrainId;
        internal ActivationId ActivationId { get; set; }
        private ISiloRuntimeClient runtimeClient;
        private readonly ILogger timerLogger;
        internal ISiloRuntimeClient RuntimeClient
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

        private ExtensionInvoker extensionInvoker;
        internal ExtensionInvoker ExtensionInvoker
        {
            get
            {
                this.lastInvoker = null;
                return this.extensionInvoker ?? (this.extensionInvoker = new ExtensionInvoker());
            }
        }

        IGrainReferenceRuntime ISystemTargetBase.GrainReferenceRuntime => this.RuntimeClient.GrainReferenceRuntime;

        GrainReference IGrainContext.GrainReference => selfReference ??= GrainReference.FromGrainId(this.id.GrainId, this.RuntimeClient.GrainReferenceRuntime);

        GrainId IGrainContext.GrainId => this.id.GrainId;

        IAddressable IGrainContext.GrainInstance => this;

        ActivationId IGrainContext.ActivationId => this.ActivationId;

        ActivationAddress IGrainContext.Address => this.ActivationAddress;
        
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
        }

        public bool IsLowPriority { get; }

        internal WorkItemGroup WorkItemGroup { get; set; }

        IGrainMethodInvoker IInvokable.GetInvoker(GrainTypeManager typeManager, int interfaceId, string genericGrainType)
        {
            // Return previous cached invoker, if applicable
            if (lastInvoker != null && interfaceId == lastInvoker.InterfaceId) // extension invoker returns InterfaceId==0, so this condition will never be true if an extension is installed
                return lastInvoker;

            if (extensionInvoker != null && extensionInvoker.IsExtensionInstalled(interfaceId))
            {
                // Shared invoker for all extensions installed on this target
                lastInvoker = extensionInvoker;
            }
            else
            {
                // Find the specific invoker for this interface / grain type
                lastInvoker = typeManager.GetInvoker(interfaceId, genericGrainType);
            }

            return lastInvoker;
        }

        internal void HandleNewRequest(Message request)
        {
            running = request;
            this.RuntimeClient.Invoke(this, this, request).Ignore();
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
            return String.Format("[{0}SystemTarget: {1}{2}{3}]",
                 IsLowPriority ? "LowPriority" : string.Empty,
                 Silo,
                 this.id,
                 this.ActivationId);
        }

        /// <summary>Adds details about message currently being processed</summary>
        internal string ToDetailedString()
        {
            return String.Format("{0} CurrentlyExecuting={1}", ToString(), running != null ? running.ToString() : "null");
        }

        bool IEquatable<IGrainContext>.Equals(IGrainContext other) => ReferenceEquals(this, other);
    }
}
