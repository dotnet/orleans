using System;
using System.Threading.Tasks;

namespace Orleans.Runtime
{
    internal class RuntimeContext
    {
        public TaskScheduler Scheduler { get; private set; }
        public ISchedulingContext ActivationContext { get; private set; }

        [ThreadStatic]
        private static RuntimeContext context;
        public static RuntimeContext Current 
        { 
            get { return context; } 
        }

        internal static ISchedulingContext CurrentActivationContext
        {
            get { return RuntimeContext.Current != null ? RuntimeContext.Current.ActivationContext : null; }
        }

        internal static void InitializeThread(TaskScheduler scheduler)
        {
            // There seems to be an implicit coupling of threads and contexts here that may be fragile. 
            // E.g. if InitializeThread() is mistakenly called on a wrong thread, would that thread be considered a worker pool thread from that point on? 
            // Is there a better/safer way to identify worker threads? 
            if (context != null && scheduler != null)
            {
                throw new InvalidOperationException("RuntimeContext.Current has already been initialized for this thread.");
            }
            context = new RuntimeContext {Scheduler = scheduler};
        }

        internal static void InitializeMainThread()
        {
            context = new RuntimeContext {Scheduler = null};
        }

        internal static void SetExecutionContext(ISchedulingContext shedContext, TaskScheduler scheduler)
        {
            if (context == null) throw new InvalidOperationException("SetExecutionContext called on unexpected non-WorkerPool thread");
            context.ActivationContext = shedContext;
            context.Scheduler = scheduler;
        }

        internal static void ResetExecutionContext()
        {
            context.ActivationContext = null;
            context.Scheduler = null;
        }

        public override string ToString()
        {
            return String.Format("RuntimeContext: ActivationContext={0}, Scheduler={1}", 
                ActivationContext != null ? ActivationContext.ToString() : "null",
                Scheduler != null ? Scheduler.ToString() : "null");
        }
    }
}
