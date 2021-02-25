using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Orleans.Internal
{
    public static class OrleansTaskExtentions
    {
        internal static readonly Task<object> CanceledTask = TaskFromCanceled<object>();
        internal static readonly Task<object> CompletedTask = Task.FromResult(default(object));

        /// <summary>
        /// Returns a <see cref="Task{Object}"/> for the provided <see cref="Task"/>.
        /// </summary>
        /// <param name="task">The task.</param>
        public static Task<object> ToUntypedTask(this Task task)
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
                    return ConvertAsync(task);
            }

            async Task<object> ConvertAsync(Task asyncTask)
            {
                await asyncTask;
                return null;
            }
        }

        /// <summary>
        /// Returns a <see cref="Task{Object}"/> for the provided <see cref="Task{T}"/>.
        /// </summary>
        /// <typeparam name="T">The underlying type of <paramref name="task"/>.</typeparam>
        /// <param name="task">The task.</param>
        public static Task<object> ToUntypedTask<T>(this Task<T> task)
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
                    return ConvertAsync(task);
            }

            async Task<object> ConvertAsync(Task<T> asyncTask)
            {
                return await asyncTask.ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Returns a <see cref="Task{Object}"/> for the provided <see cref="Task{T}"/>.
        /// </summary>
        /// <typeparam name="T">The underlying type of <paramref name="task"/>.</typeparam>
        /// <param name="task">The task.</param>
        internal static Task<T> ToTypedTask<T>(this Task<object> task)
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
                    return ConvertAsync(task);
            }

            async Task<T> ConvertAsync(Task<object> asyncTask)
            {
                var result = await asyncTask.ConfigureAwait(false);

                if (result is null)
                {
                    if (!NullabilityHelper<T>.IsNullableType)
                    {
                        ThrowInvalidTaskResultType(typeof(T));
                    }

                    return default;
                }

                return (T)result;
            }
        }

        private static class NullabilityHelper<T>
        {
            /// <summary>
            /// True if <typeparamref name="T" /> is an instance of a nullable type (a reference type or <see cref="Nullable{T}"/>), otherwise false.
            /// </summary>
            public static readonly bool IsNullableType = !typeof(T).IsValueType || typeof(T).IsConstructedGenericType && typeof(T).GetGenericTypeDefinition() == typeof(Nullable<>);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowInvalidTaskResultType(Type type)
        {
            var message = $"Expected result of type {type} but encountered a null value. This may be caused by a grain call filter swallowing an exception.";
            throw new InvalidOperationException(message);
        }

        /// <summary>
        /// Returns a <see cref="Task{Object}"/> for the provided <see cref="Task{Object}"/>.
        /// </summary>
        /// <param name="task">The task.</param>
        public static Task<object> ToUntypedTask(this Task<object> task)
        {
            return task;
        }
        
        private static Task<object> TaskFromFaulted(Task task)
        {
            var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            completion.SetException(task.Exception.InnerExceptions);
            return completion.Task;
        }

        private static Task<T> TaskFromFaulted<T>(Task task)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            completion.SetException(task.Exception.InnerExceptions);
            return completion.Task;
        }

        private static Task<T> TaskFromCanceled<T>()
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            completion.SetCanceled();
            return completion.Task;
        }

        public static async Task LogException(this Task task, ILogger logger, ErrorCode errorCode, string message)
        {
            try
            {
                await task;
            }
            catch (Exception exc)
            {
                _ = task.Exception; // Observe exception
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

        public static void WaitWithThrow(this Task task, TimeSpan timeout)
        {
            if (!task.Wait(timeout))
            {
                throw new TimeoutException($"Task.WaitWithThrow has timed out after {timeout}.");
            }
        }

        internal static T WaitForResultWithThrow<T>(this Task<T> task, TimeSpan timeout)
        {
            if (!task.Wait(timeout))
            {
                throw new TimeoutException($"Task<T>.WaitForResultWithThrow has timed out after {timeout}.");
            }
            return task.Result;
        }

        /// <summary>
        /// This will apply a timeout delay to the task, allowing us to exit early
        /// </summary>
        /// <param name="taskToComplete">The task we will timeout after timeSpan</param>
        /// <param name="timeout">Amount of time to wait before timing out</param>
        /// <param name="exceptionMessage">Text to put into the timeout exception message</param>
        /// <exception cref="TimeoutException">If we time out we will get this exception</exception>
        /// <returns>The completed task</returns>
        public static async Task WithTimeout(this Task taskToComplete, TimeSpan timeout, string exceptionMessage = null)
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
            var errorMessage = exceptionMessage ?? $"WithTimeout has timed out after {timeout}";
            throw new TimeoutException(errorMessage);
        }

        /// <summary>
        /// This will apply a timeout delay to the task, allowing us to exit early
        /// </summary>
        /// <param name="taskToComplete">The task we will timeout after timeSpan</param>
        /// <param name="timeSpan">Amount of time to wait before timing out</param>
        /// <param name="exceptionMessage">Text to put into the timeout exception message</param>
        /// <exception cref="TimeoutException">If we time out we will get this exception</exception>
        /// <exception cref="TimeoutException">If we time out we will get this exception</exception>
        /// <returns>The value of the completed task</returns>
        public static async Task<T> WithTimeout<T>(this Task<T> taskToComplete, TimeSpan timeSpan, string exceptionMessage = null)
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
            var errorMessage = exceptionMessage ?? $"WithTimeout has timed out after {timeSpan}";
            throw new TimeoutException(errorMessage);
        }

        /// <summary>
        /// For making an uncancellable task cancellable, by ignoring its result.
        /// </summary>
        /// <param name="taskToComplete">The task to wait for unless cancelled</param>
        /// <param name="cancellationToken">A cancellation token for cancelling the wait</param>
        /// <param name="message">Message to set in the exception</param>
        /// <returns></returns>
        internal static async Task WithCancellation(
            this Task taskToComplete,
            CancellationToken cancellationToken,
            string message)
        {
            try
            {
                await taskToComplete.WithCancellation(cancellationToken);
            }
            catch (TaskCanceledException ex)
            {
                throw new TaskCanceledException(message, ex);
            }
        }

        /// <summary>
        /// For making an uncancellable task cancellable, by ignoring its result.
        /// </summary>
        /// <param name="taskToComplete">The task to wait for unless cancelled</param>
        /// <param name="cancellationToken">A cancellation token for cancelling the wait</param>
        /// <returns></returns>
        internal static Task WithCancellation(this Task taskToComplete, CancellationToken cancellationToken)
        {
            if (taskToComplete.IsCompleted || !cancellationToken.CanBeCanceled)
            {
                return taskToComplete;
            }
            else if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled<object>(cancellationToken);
            }
            else
            {
                return MakeCancellable(taskToComplete, cancellationToken);
            }
        }

        private static async Task MakeCancellable(Task task, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(() =>
                      tcs.TrySetCanceled(cancellationToken), useSynchronizationContext: false))
            {
                var firstToComplete = await Task.WhenAny(task, tcs.Task).ConfigureAwait(false);

                if (firstToComplete != task)
                {
                    task.Ignore();
                }

                await firstToComplete.ConfigureAwait(false);
            }
        }

        /// <summary>
        /// For making an uncancellable task cancellable, by ignoring its result.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="taskToComplete">The task to wait for unless cancelled</param>
        /// <param name="cancellationToken">A cancellation token for cancelling the wait</param>
        /// <returns></returns>
        internal static Task<T> WithCancellation<T>(this Task<T> taskToComplete, CancellationToken cancellationToken)
        {
            if (taskToComplete.IsCompleted || !cancellationToken.CanBeCanceled)
            {
                return taskToComplete;
            }
            else if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled<T>(cancellationToken);
            }
            else 
            {
                return MakeCancellable(taskToComplete, cancellationToken);
            }
        }

        private static async Task<T> MakeCancellable<T>(Task<T> task, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(() =>
                      tcs.TrySetCanceled(cancellationToken), useSynchronizationContext: false))
            {
                var firstToComplete = await Task.WhenAny(task, tcs.Task).ConfigureAwait(false);

                if (firstToComplete != task)
                {
                    task.Ignore();
                }

                return await firstToComplete.ConfigureAwait(false);
            }
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

        //The rationale for GetAwaiter().GetResult() instead of .Result
        //is presented at https://github.com/aspnet/Security/issues/59.      
        internal static T GetResult<T>(this Task<T> task)
        {
            return task.GetAwaiter().GetResult();
        }

        internal static Task WhenCancelled(this CancellationToken token)
        {
            if (token.IsCancellationRequested)
            {
                return Task.CompletedTask;
            }

            var waitForCancellation = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            token.Register(obj =>
            {
                var tcs = (TaskCompletionSource<object>)obj;
                tcs.TrySetResult(null);
            }, waitForCancellation);

            return waitForCancellation.Task;
        }
    }
}
