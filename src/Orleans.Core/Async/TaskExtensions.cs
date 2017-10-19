using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans
{
    internal static class OrleansTaskExtentions
    {
        internal static readonly Task<object> CanceledTask = TaskFromCanceled<object>();
        internal static readonly Task<object> CompletedTask = Task.FromResult(default(object));

        /// <summary>
        /// Returns a <see cref="Task{Object}"/> for the provided <see cref="Task"/>.
        /// </summary>
        /// <param name="task">The task.</param>
        public static Task<object> Box(this Task task)
        {
            switch (task.Status)
            {
                case TaskStatus.RanToCompletion:
                    return CompletedTask;

                case TaskStatus.Faulted:
                    return TaskFromFaulted(task);

                case TaskStatus.Canceled:
                    return CanceledTask;

                default:
                    return BoxAwait(task);
            }
        }

        /// <summary>
        /// Returns a <see cref="Task{Object}"/> for the provided <see cref="Task{T}"/>.
        /// </summary>
        /// <typeparam name="T">The underlying type of <paramref name="task"/>.</typeparam>
        /// <param name="task">The task.</param>
        public static Task<object> Box<T>(this Task<T> task)
        {
            if (typeof(T) == typeof(object))
                return task as Task<object>;

            switch (task.Status)
            {
                case TaskStatus.RanToCompletion:
                    return Task.FromResult((object)GetResult(task));

                case TaskStatus.Faulted:
                    return TaskFromFaulted(task);

                case TaskStatus.Canceled:
                    return CanceledTask;

                default:
                    return BoxAwait(task);
            }
        }

        /// <summary>
        /// Returns a <see cref="Task{Object}"/> for the provided <see cref="Task{T}"/>.
        /// </summary>
        /// <typeparam name="T">The underlying type of <paramref name="task"/>.</typeparam>
        /// <param name="task">The task.</param>
        internal static Task<T> Unbox<T>(this Task<object> task)
        {
            if (typeof(T) == typeof(object))
                return task as Task<T>;

            switch (task.Status)
            {
                case TaskStatus.RanToCompletion:
                    return Task.FromResult((T)GetResult(task));

                case TaskStatus.Faulted:
                    return TaskFromFaulted<T>(task);

                case TaskStatus.Canceled:
                    return TaskFromCanceled<T>();

                default:
                    return UnboxContinuation<T>(task);
            }
        }

        /// <summary>
        /// Returns a <see cref="Task{Object}"/> for the provided <see cref="Task{Object}"/>.
        /// </summary>
        /// <param name="task">The task.</param>
        public static Task<object> Box(this Task<object> task)
        {
            return task;
        }

        private static async Task<object> BoxAwait(Task task)
        {
            await task;
            return default(object);
        }

        private static async Task<object> BoxAwait<T>(Task<T> task)
        {
            return await task;
        }

        private static Task<T> UnboxContinuation<T>(Task<object> task)
        {
            return task.ContinueWith(t => t.Unbox<T>()).Unwrap();
        }

        private static Task<object> TaskFromFaulted(Task task)
        {
            var completion = new TaskCompletionSource<object>();
            completion.SetException(task.Exception.InnerExceptions);
            return completion.Task;
        }

        private static Task<T> TaskFromFaulted<T>(Task task)
        {
            var completion = new TaskCompletionSource<T>();
            completion.SetException(task.Exception.InnerExceptions);
            return completion.Task;
        }

        private static Task<T> TaskFromCanceled<T>()
        {
            var completion = new TaskCompletionSource<T>();
            completion.SetCanceled();
            return completion.Task;
        }

        public static async Task LogException(this Task task, Logger logger, ErrorCode errorCode, string message)
        {
            try
            {
                await task;
            }
            catch (Exception exc)
            {
                var ignored = task.Exception; // Observe exception
                logger.Error(errorCode, message, exc);
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

            var timeoutCancellationTokenSource = new CancellationTokenSource();
            var completedTask = await Task.WhenAny(taskToComplete, Task.Delay(timeout, timeoutCancellationTokenSource.Token));

            // We got done before the timeout, or were able to complete before this code ran, return the result
            if (taskToComplete == completedTask)
            {
                timeoutCancellationTokenSource.Cancel();
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
        /// <param name="timeSpan">Amount of time to wait before timing out</param>
        /// <exception cref="TimeoutException">If we time out we will get this exception</exception>
        /// <returns>The value of the completed task</returns>
        public static async Task<T> WithTimeout<T>(this Task<T> taskToComplete, TimeSpan timeSpan)
        {
            if (taskToComplete.IsCompleted)
            {
                return await taskToComplete;
            }

            var timeoutCancellationTokenSource = new CancellationTokenSource();
            var completedTask = await Task.WhenAny(taskToComplete, Task.Delay(timeSpan, timeoutCancellationTokenSource.Token));

            // We got done before the timeout, or were able to complete before this code ran, return the result
            if (taskToComplete == completedTask)
            {
                timeoutCancellationTokenSource.Cancel();
                // Await this so as to propagate the exception correctly
                return await taskToComplete;
            }

            // We did not complete before the timeout, we fire and forget to ensure we observe any exceptions that may occur
            taskToComplete.Ignore();
            throw new TimeoutException(String.Format("WithTimeout has timed out after {0}.", timeSpan));
        }

        internal static Task WrapInTask(Action action)
        {
            try
            {
                action();
                return Task.CompletedTask;
            }
            catch (Exception exc)
            {
                return Task.FromException<object>(exc);
            }
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
                resolver.TrySetException(task.Exception.InnerExceptions);
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
                        resolver.TrySetException(t.Exception.InnerExceptions);
                    }
                    else if (t.IsCanceled)
                    {
                        resolver.TrySetException(new TaskCanceledException(t));
                    }
                    else
                    {
                        resolver.TrySetResult(t.GetResult());
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
        internal static void GetResult(this Task task)
        {
            task.GetAwaiter().GetResult();
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
        /// <summary>
        /// A special 'Done' Task that is already in the RunToCompletion state
        /// </summary>
        [Obsolete("Use Task.CompletedTask")]
        public static Task Done => Task.CompletedTask;
    }
}
