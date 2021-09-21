using System;
using System.Threading.Tasks;

namespace Orleans.Runtime.Scheduler
{
    internal static class SchedulerExtensions
    {
        internal static Task QueueTask(this IGrainContext targetContext, Func<Task> taskFunc)
        {
            var workItem = new AsyncClosureWorkItem(taskFunc, targetContext);
            targetContext.Scheduler.QueueWorkItem(workItem);
            return workItem.Task;
        }

        internal static Task QueueTask(this WorkItemGroup scheduler, Func<Task> taskFunc, IGrainContext targetContext)
        {
            var workItem = new AsyncClosureWorkItem(taskFunc, targetContext);
            scheduler.QueueWorkItem(workItem);
            return workItem.Task;
        }

        internal static Task RunOrQueueTask(this IGrainContext targetContext, Func<Task> taskFunc)
        {
            var currentContext = RuntimeContext.Current;
            if (currentContext != null && currentContext.Equals(targetContext))
            {
                try
                {
                    return taskFunc();
                }
                catch (Exception exc)
                {
                    return Task.FromResult(exc);
                }
            }

            var workItem = new AsyncClosureWorkItem(taskFunc, targetContext);
            targetContext.Scheduler.QueueWorkItem(workItem);
            return workItem.Task;
        }
    }
}
