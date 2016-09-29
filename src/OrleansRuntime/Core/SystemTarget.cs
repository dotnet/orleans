using System;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.Runtime.Scheduler;

namespace Orleans.Runtime
{
    internal abstract class SystemTarget : ISystemTarget, ISystemTargetBase, IInvokable
    {
        private IGrainMethodInvoker lastInvoker;
        private readonly SchedulingContext schedulingContext;
        private Message running;
        
        protected SystemTarget(GrainId grainId, SiloAddress silo) 
            : this(grainId, silo, false)
        {
        }

        protected SystemTarget(GrainId grainId, SiloAddress silo, bool lowPriority)
        {
            GrainId = grainId;
            Silo = silo;
            ActivationId = ActivationId.GetSystemActivation(grainId, silo);
            schedulingContext = new SchedulingContext(this, lowPriority);
        }

        public SiloAddress Silo { get; private set; }
        public GrainId GrainId { get; private set; }
        public ActivationId ActivationId { get; set; }

        internal SchedulingContext SchedulingContext { get { return schedulingContext; } }

        public IGrainMethodInvoker GetInvoker(int interfaceId, string genericGrainType = null)
        {
            if (lastInvoker != null && interfaceId == lastInvoker.InterfaceId)
                return lastInvoker;

            var invoker = GrainTypeManager.Instance.GetInvoker(interfaceId);
            lastInvoker = invoker;
            
            return lastInvoker;
        }

        public void HandleNewRequest(Message request)
        {
            running = request;
            InsideRuntimeClient.Current.Invoke(this, this, request).Ignore();
        }

        public void HandleResponse(Message response)
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
                 GrainId,
                 ActivationId);
        }

        public string ToDetailedString()
        {
            return String.Format("{0} CurrentlyExecuting={1}", ToString(), running != null ? running.ToString() : "null");
        }
    }
}
