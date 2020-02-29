using System;
using System.Threading.Tasks;

namespace Orleans.Runtime.Scheduler
{
    internal static class SchedulerExtensions
    {
        internal static Task<T> QueueTask<T>(this OrleansTaskScheduler scheduler, Func<Task<T>> taskFunc, IGrainContext targetContext)
        {
            var workItem = new AsyncClosureWorkItem<T>(taskFunc, targetContext);
            scheduler.QueueWorkItem(workItem);
            return workItem.Task;
        }

        internal static Task QueueTask(this OrleansTaskScheduler scheduler, Func<Task> taskFunc, IGrainContext targetContext)
        {
            var workItem = new AsyncClosureWorkItem(taskFunc, targetContext);
            scheduler.QueueWorkItem(workItem);
            return workItem.Task;
        }

        internal static Task QueueNamedTask(this OrleansTaskScheduler scheduler, Func<Task> taskFunc, IGrainContext targetContext, string activityName = null)
        {
            var workItem = new AsyncClosureWorkItem(taskFunc, activityName, targetContext);
            scheduler.QueueWorkItem(workItem);
            return workItem.Task;
        }

        internal static Task QueueActionAsync(this OrleansTaskScheduler scheduler, Action action, IGrainContext targetContext)
        {
            var resolver = new TaskCompletionSource<bool>();
            Action syncFunc =
                () =>
                {
                    try
                    {
                        action();
                        resolver.TrySetResult(true);
                    }
                    catch (Exception exc)
                    {
                        resolver.TrySetException(exc);
                    }
                };
            scheduler.QueueAction(syncFunc, targetContext);
            return resolver.Task;
        }

        /// <summary>
        /// Execute a closure ensuring that it has a runtime context (e.g. to send messages from an arbitrary thread)
        /// </summary>
        /// <param name="scheduler"></param>
        /// <param name="action"></param>
        /// <param name="targetContext"></param>
        internal static Task RunOrQueueAction(this OrleansTaskScheduler scheduler, Action action, IGrainContext targetContext)
        {
            return scheduler.RunOrQueueTask(() =>
            {
                action();
                return Task.CompletedTask;
            }, targetContext);
        }


        internal static Task<T> RunOrQueueTask<T>(this OrleansTaskScheduler scheduler, Func<Task<T>> taskFunc, IGrainContext targetContext)
        {
            var currentContext = RuntimeContext.CurrentGrainContext;
            if (currentContext is object && currentContext.Equals(targetContext))
            {
                try
                {
                    return taskFunc();
                }
                catch (Exception exc)
                {
                    var resolver = new TaskCompletionSource<T>();
                    resolver.TrySetException(exc);
                    return resolver.Task; 
                }
            }
            
            return scheduler.QueueTask(taskFunc, targetContext);
        }

        internal static Task RunOrQueueTask(this OrleansTaskScheduler scheduler, Func<Task> taskFunc, IGrainContext targetContext)
        {
            var currentContext = RuntimeContext.CurrentGrainContext;
            if (currentContext is object && currentContext.Equals(targetContext))
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

            return scheduler.QueueTask(taskFunc, targetContext);
        }
    }
}
