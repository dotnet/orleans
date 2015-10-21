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
using Orleans.Runtime;

namespace Orleans
{
    /// <summary>
    /// Utility functions for dealing with Task's.
    /// </summary>
    public static class PublicOrleansTaskExtentions
    {
        /// <summary>
        /// Observes and ignores a potential exception on a given Task.
        /// If a Task fails and throws an exception which is never observed, it will be caught by the .NET finalizer thread.
        /// This function awaits the given task and if the exception is thrown, it observes this exception and simply ignores it.
        /// This will prevent the escalation of this exception to the .NET finalizer thread.
        /// </summary>
        /// <param name="task">The task to be ignored.</param>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1804:RemoveUnusedLocals", MessageId = "ignored")]
        public static async void Ignore(this Task task)
        {
            try
            {
                await task;
            }
            catch (Exception)
            {
                var ignored = task.Exception; // Observe exception
            }
        }
    }

    internal static class OrleansTaskExtentions
    {
        public static async Task LogException(this Task task, Logger logger, ErrorCode errorCode, string message)
        {
            try
            {
                await task;
            }
            catch (Exception exc)
            {
                var ignored = task.Exception; // Observe exception
                logger.Error((int)errorCode, message, exc);
                throw;
            }
        }

        // Executes an async function such as Exception is never thrown but rather always returned as a broken task.
        public static async Task SafeExecute(Func<Task> action)
        {
            await action();
        }

        public static async Task ExecuteAndIgnoreException(Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (Exception)
            {
                // dont re-throw, just eat it.
            }
        }

        internal static String ToString(this Task t)
        {
            return t == null ? "null" : string.Format("[Id={0}, Status={1}]", t.Id, Enum.GetName(typeof(TaskStatus), t.Status));
        }

        internal static String ToString<T>(this Task<T> t)
        {
            return t == null ? "null" : string.Format("[Id={0}, Status={1}]", t.Id, Enum.GetName(typeof(TaskStatus), t.Status));
        }


        internal static void WaitWithThrow(this Task task, TimeSpan timeout)
        {
            if (!task.Wait(timeout))
            {
                throw new TimeoutException(String.Format("Task.WaitWithThrow has timed out after {0}.", timeout));
            }
        }

        internal static T WaitForResultWithThrow<T>(this Task<T> task, TimeSpan timeout)
        {
            if (!task.Wait(timeout))
            {
                throw new TimeoutException(String.Format("Task<T>.WaitForResultWithThrow has timed out after {0}.", timeout));
            }
            return task.Result;
        }

        /// <summary>
        /// This will apply a timeout delay to the task, allowing us to exit early
        /// </summary>
        /// <param name="taskToComplete">The task we will timeout after timeSpan</param>
        /// <param name="timeout">Amount of time to wait before timing out</param>
        /// <exception cref="TimeoutException">If we time out we will get this exception</exception>
        /// <returns>The completed task</returns>
        internal static async Task WithTimeout(this Task taskToComplete, TimeSpan timeout)
        {
            if (taskToComplete.IsCompleted)
            {
                await taskToComplete;
                return;
            }

            await Task.WhenAny(taskToComplete, Task.Delay(timeout));

            // We got done before the timeout, or were able to complete before this code ran, return the result
            if (taskToComplete.IsCompleted)
            {
                // Await this so as to propagate the exception correctly
                await taskToComplete;
                return;
            }

            // We did not complete before the timeout, we fire and forget to ensure we observe any exceptions that may occur
            taskToComplete.Ignore();
            throw new TimeoutException(String.Format("WithTimeout has timed out after {0}.", timeout));
        }

        /// <summary>
        /// This will apply a timeout delay to the task, allowing us to exit early
        /// </summary>
        /// <param name="taskToComplete">The task we will timeout after timeSpan</param>
        /// <param name="timeout">Amount of time to wait before timing out</param>
        /// <exception cref="TimeoutException">If we time out we will get this exception</exception>
        /// <returns>The value of the completed task</returns>
        public static async Task<T> WithTimeout<T>(this Task<T> taskToComplete, TimeSpan timeSpan)
        {
            if (taskToComplete.IsCompleted)
            {
                return await taskToComplete;
            }

            await Task.WhenAny(taskToComplete, Task.Delay(timeSpan));

            // We got done before the timeout, or were able to complete before this code ran, return the result
            if (taskToComplete.IsCompleted)
            {
                // Await this so as to propagate the exception correctly
                return await taskToComplete;
            }

            // We did not complete before the timeout, we fire and forget to ensure we observe any exceptions that may occur
            taskToComplete.Ignore();
            throw new TimeoutException(String.Format("WithTimeout has timed out after {0}.", timeSpan));
        }

        internal static Task<T> FromException<T>(Exception exception)
        {
            var tcs = new TaskCompletionSource<T>(exception);
            tcs.TrySetException(exception);
            return tcs.Task;
        }

        internal static Task<T> ConvertTaskViaTcs<T>(Task<T> task)
        {
            if (task == null) return Task.FromResult(default(T));

            var resolver = new TaskCompletionSource<T>();

            if (task.Status == TaskStatus.RanToCompletion)
            {
                resolver.TrySetResult(task.Result);
            }
            else if (task.IsFaulted)
            {
                resolver.TrySetException(task.Exception.Flatten());
            }
            else if (task.IsCanceled)
            {
                resolver.TrySetException(new TaskCanceledException(task));
            }
            else
            {
                if (task.Status == TaskStatus.Created) task.Start();

                task.ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        resolver.TrySetException(t.Exception.Flatten());
                    }
                    else if (t.IsCanceled)
                    {
                        resolver.TrySetException(new TaskCanceledException(t));
                    }
                    else
                    {
                        resolver.TrySetResult(t.Result);
                    }
                });
            }
            return resolver.Task;
        }

        //The rationale for GetAwaiter().GetResult() instead of .Result
        //is presented at https://github.com/aspnet/Security/issues/59.      
        internal static T GetResult<T>(this Task<T> task)
        {
            return task.GetAwaiter().GetResult();
        }
    }
}

namespace Orleans
{
    /// <summary>
    /// A special void 'Done' Task that is already in the RunToCompletion state.
    /// Equivalent to Task.FromResult(1).
    /// </summary>
    public static class TaskDone
    {
        private static readonly Task<int> doneConstant = Task.FromResult(1);

        /// <summary>
        /// A special 'Done' Task that is already in the RunToCompletion state
        /// </summary>
        public static Task Done
        {
            get
            {
                return doneConstant;
            }
        }
    }
}