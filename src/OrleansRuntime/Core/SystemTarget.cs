using System;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.Runtime.Scheduler;

namespace Orleans.Runtime
{
    public abstract class SystemTarget : ISystemTarget, ISystemTargetBase, IInvokable
    {
        private readonly GrainId grainId;
        private readonly SchedulingContext schedulingContext;
        private IGrainMethodInvoker lastInvoker;
        private Message running;

        public SiloAddress Silo { get; }
        GrainId ISystemTargetBase.GrainId => grainId;
        internal SchedulingContext SchedulingContext => schedulingContext;
        internal ActivationId ActivationId { get; set; }

        public SystemTarget()
        {
            
        }

        internal SystemTarget(GrainId grainId, SiloAddress silo) 
            : this(grainId, silo, false)
        {
        }

        internal SystemTarget(GrainId grainId, SiloAddress silo, bool lowPriority)
        {
            this.grainId = grainId;
            Silo = silo;
            ActivationId = ActivationId.GetSystemActivation(grainId, silo);
            schedulingContext = new SchedulingContext(this, lowPriority);
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
            InsideRuntimeClient.Current.Invoke(this, this, request).Ignore();
        }

        internal void HandleResponse(Message response)
        {
            running = response;
            InsideRuntimeClient.Current.ReceiveResponse(response);
        }

        /// <summary>
        /// Register a timer to send regular callbacks to this grain.
        /// This timer will keep the current grain from being deactivated.
        /// </summary>
        /// <param name="asyncCallback"></param>
        /// <param name="state"></param>
        /// <param name="dueTime"></param>
        /// <param name="period"></param>
        /// <returns></returns>
        public IDisposable RegisterTimer(Func<object, Task> asyncCallback, object state, TimeSpan dueTime, TimeSpan period)
        {
            var ctxt = RuntimeContext.CurrentActivationContext;
            InsideRuntimeClient.Current.Scheduler.CheckSchedulingContextValidity(ctxt);
            String name = ctxt.Name + "Timer";
          
            var timer = GrainTimer.FromTaskCallback(asyncCallback, state, dueTime, period, name);
            timer.Start();
            return timer;
        }

        public override string ToString()
        {
            return String.Format("[{0}SystemTarget: {1}{2}{3}]",
                 SchedulingContext.IsSystemPriorityContext ? String.Empty : "LowPriority",
                 Silo,
                 this.grainId,
                 ActivationId);
        }

        public string ToDetailedString()
        {
            return String.Format("{0} CurrentlyExecuting={1}", ToString(), running != null ? running.ToString() : "null");
        }
    }
}
