/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

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
