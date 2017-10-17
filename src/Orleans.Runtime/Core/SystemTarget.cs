using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
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
    public abstract class SystemTarget : ISystemTarget, ISystemTargetBase, IInvokable
    {
        private readonly GrainId grainId;
        private readonly SchedulingContext schedulingContext;
        private IGrainMethodInvoker lastInvoker;
        private Message running;

        /// <summary>Silo address of the system target.</summary>
        public SiloAddress Silo { get; }
        GrainId ISystemTargetBase.GrainId => grainId;
        internal SchedulingContext SchedulingContext => schedulingContext;
        internal ActivationId ActivationId { get; set; }
        private ISiloRuntimeClient runtimeClient;
        private readonly ILoggerFactory loggerFactory;
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

        IGrainReferenceRuntime ISystemTargetBase.GrainReferenceRuntime => this.RuntimeClient.GrainReferenceRuntime;

        /// <summary>Only needed to make Reflection happy.</summary>
        protected SystemTarget()
        {
        }

        internal SystemTarget(GrainId grainId, SiloAddress silo, ILoggerFactory loggerFactory) 
            : this(grainId, silo, false, loggerFactory)
        {
        }

        internal SystemTarget(GrainId grainId, SiloAddress silo, bool lowPriority, ILoggerFactory loggerFactory)
        {
            this.grainId = grainId;
            Silo = silo;
            this.loggerFactory = loggerFactory;
            ActivationId = ActivationId.GetSystemActivation(grainId, silo);
            schedulingContext = new SchedulingContext(this, lowPriority);
            this.timerLogger = loggerFactory.CreateLogger<GrainTimer>();
        }

        IGrainMethodInvoker IInvokable.GetInvoker(GrainTypeManager typeManager, int interfaceId, string genericGrainType)
        {
            if (lastInvoker != null && interfaceId == lastInvoker.InterfaceId)
                return lastInvoker;

            var invoker = typeManager.GetInvoker(interfaceId);
            lastInvoker = invoker;
            
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
            var ctxt = RuntimeContext.CurrentActivationContext;
            this.RuntimeClient.Scheduler.CheckSchedulingContextValidity(ctxt);
            name = name ?? ctxt.Name + "Timer";

            var timer = GrainTimer.FromTaskCallback(this.RuntimeClient.Scheduler,this.timerLogger, asyncCallback, state, dueTime, period, name);
            timer.Start();
            return timer;
        }

        /// <summary>Override of object.ToString()</summary>
        public override string ToString()
        {
            return String.Format("[{0}SystemTarget: {1}{2}{3}]",
                 SchedulingContext.IsSystemPriorityContext ? String.Empty : "LowPriority",
                 Silo,
                 this.grainId,
                 ActivationId);
        }

        /// <summary>Adds details about message currently being processed</summary>
        internal string ToDetailedString()
        {
            return String.Format("{0} CurrentlyExecuting={1}", ToString(), running != null ? running.ToString() : "null");
        }
    }
}
