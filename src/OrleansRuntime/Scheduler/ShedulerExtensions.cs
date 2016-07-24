using System;
using System.Threading.Tasks;

namespace Orleans.Runtime.Scheduler
{
    internal static class SchedulerExtensions
    {
        internal static Task<T> QueueTask<T>(this OrleansTaskScheduler scheduler, Func<Task<T>> taskFunc, ISchedulingContext targetContext)
        {
            var resolver = new TaskCompletionSource<T>();
            Func<Task> asyncFunc =
                async () =>
                {
                    try
                    {
                        T result = await taskFunc();
                        resolver.TrySetResult(result);
                    }
                    catch (Exception exc)
                    {
                        resolver.TrySetException(exc);
                    }
                };

            // it appears that it's not important that we fire-and-forget asyncFunc() because we wait on the
            scheduler.QueueWorkItem(new ClosureWorkItem(() => asyncFunc().Ignore()), targetContext);
            return resolver.Task;
        }

        internal static Task QueueTask(this OrleansTaskScheduler scheduler, Func<Task> taskFunc, ISchedulingContext targetContext)
        {
            var resolver = new TaskCompletionSource<bool>();
            Func<Task> asyncFunc =
                async () =>
                {
                    try
                    {
                        await taskFunc();
                        resolver.TrySetResult(true);
                    }
                    catch (Exception exc)
                    {
                        resolver.TrySetException(exc);
                    }
                };
            scheduler.QueueWorkItem(new ClosureWorkItem(() => asyncFunc().Ignore()), targetContext);
            return resolver.Task;
        }

        internal static Task QueueNamedTask(this OrleansTaskScheduler scheduler, Func<Task> taskFunc, ISchedulingContext targetContext, string activityName = null)
        {
            var resolver = new TaskCompletionSource<bool>();
            Func<Task> asyncFunc =
                async () =>
                {
                    try
                    {
                        await taskFunc();
                        resolver.TrySetResult(true);
                    }
                    catch (Exception exc)
                    {
                        resolver.TrySetException(exc);
                    }
                };
            scheduler.QueueWorkItem(new ClosureWorkItem(() => asyncFunc().Ignore(), () => activityName), targetContext);
            return resolver.Task;
        }

        internal static Task QueueAction(this OrleansTaskScheduler scheduler, Action action, ISchedulingContext targetContext)
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
            scheduler.QueueWorkItem(new ClosureWorkItem(() => syncFunc()), targetContext);
            return resolver.Task;
        }

        /// <summary>
        /// Execute a closure ensuring that it has a runtime context (e.g. to send messages from an arbitrary thread)
        /// </summary>
        /// <param name="scheduler"></param>
        /// <param name="action"></param>
        /// <param name="targetContext"></param>
        internal static Task RunOrQueueAction(this OrleansTaskScheduler scheduler, Action action, ISchedulingContext targetContext)
        {
            return scheduler.RunOrQueueTask(() =>
            {
                action();
                return TaskDone.Done;
            }, targetContext);
        }


        internal static Task<T> RunOrQueueTask<T>(this OrleansTaskScheduler scheduler, Func<Task<T>> taskFunc, ISchedulingContext targetContext)
        {
            ISchedulingContext currentContext = RuntimeContext.CurrentActivationContext;
            if (SchedulingUtils.IsAddressableContext(currentContext)
                && currentContext.Equals(targetContext))
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

        internal static Task RunOrQueueTask(this OrleansTaskScheduler scheduler, Func<Task> taskFunc, ISchedulingContext targetContext)
        {
            var currentContext = RuntimeContext.CurrentActivationContext;
            if (SchedulingUtils.IsAddressableContext(currentContext)
                && currentContext.Equals(targetContext))
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
