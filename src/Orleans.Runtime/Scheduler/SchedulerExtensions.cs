using System;
using System.Threading.Tasks;

namespace Orleans.Runtime.Scheduler
{
    internal static class SchedulerExtensions
    {
        internal static Task QueueTask(this IGrainContext targetContext, Func<Task> taskFunc) => targetContext.Scheduler.QueueTask(taskFunc, targetContext);
        private static Task QueueTask(this IWorkItemScheduler scheduler, Func<Task> taskFunc, IGrainContext targetContext)
        {
            var workItem = new AsyncClosureWorkItem(taskFunc, targetContext);
            scheduler.QueueWorkItem(workItem);
            return workItem.Task;
        }

        internal static Task QueueNamedTask(this IGrainContext targetContext, Func<Task> taskFunc, string activityName = null) => targetContext.Scheduler.QueueNamedTask(taskFunc, targetContext, activityName);
        private static Task QueueNamedTask(this IWorkItemScheduler scheduler, Func<Task> taskFunc, IGrainContext targetContext, string activityName = null)
        {
            var workItem = new AsyncClosureWorkItem(taskFunc, activityName, targetContext);
            scheduler.QueueWorkItem(workItem);
            return workItem.Task;
        }

        internal static Task QueueActionAsync(this IGrainContext targetContext, Action action) => targetContext.Scheduler.QueueActionAsync(action);
        private static Task QueueActionAsync(this IWorkItemScheduler scheduler, Action action)
        {
            var resolver = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
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
            scheduler.QueueAction(syncFunc);
            return resolver.Task;
        }

        /// <summary>
        /// Execute a closure ensuring that it has a runtime context (e.g. to send messages from an arbitrary thread)
        /// </summary>
        /// <param name="targetContext"></param>
        /// <param name="action"></param>
        internal static Task RunOrQueueAction(this IGrainContext targetContext, Action action) => targetContext.Scheduler.RunOrQueueAction(action, targetContext);

        /// <summary>
        /// Execute a closure ensuring that it has a runtime context (e.g. to send messages from an arbitrary thread)
        /// </summary>
        /// <param name="scheduler"></param>
        /// <param name="action"></param>
        /// <param name="targetContext"></param>
        private static Task RunOrQueueAction(this IWorkItemScheduler scheduler, Action action, IGrainContext targetContext)
        {
            return scheduler.RunOrQueueTask(() =>
            {
                action();
                return Task.CompletedTask;
            }, targetContext);
        }

        internal static Task<T> RunOrQueueTask<T>(this IGrainContext targetContext, Func<Task<T>> taskFunc) => targetContext.Scheduler.RunOrQueueTask(taskFunc, targetContext);
        private static Task<T> RunOrQueueTask<T>(this IWorkItemScheduler scheduler, Func<Task<T>> taskFunc, IGrainContext targetContext)
        {
            var currentContext = RuntimeContext.Current;
            if (currentContext is object && currentContext.Equals(targetContext))
            {
                try
                {
                    return taskFunc();
                }
                catch (Exception exc)
                {
                    var resolver = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                    resolver.TrySetException(exc);
                    return resolver.Task; 
                }
            }
            
            var workItem = new AsyncClosureWorkItem<T>(taskFunc, targetContext);
            scheduler.QueueWorkItem(workItem);
            return workItem.Task;
        }

        internal static Task RunOrQueueTask(this IGrainContext targetContext, Func<Task> taskFunc) => targetContext.Scheduler.RunOrQueueTask(taskFunc, targetContext);
        private static Task RunOrQueueTask(this IWorkItemScheduler scheduler, Func<Task> taskFunc, IGrainContext targetContext)
        {
            var currentContext = RuntimeContext.Current;
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
