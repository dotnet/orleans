using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Orleans.Internal
{
    /// <summary>
    /// Extensions for working with <see cref="Task"/> and <see cref="Task{TResult}"/>.
    /// </summary>
    internal static class OrleansTaskExtentions
    {
        public static async Task LogException(this Task task, ILogger logger, ErrorCode errorCode, string message)
        {
            try
            {
                await task;
            }
            catch (Exception exc)
            {
                logger.LogError((int)errorCode, exc, "{Message}", message);
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

        internal static string ToString(this Task t) => t == null ? "null" : $"[Id={t.Id}, Status={t.Status}]";

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
        /// <param name="message">Message to set in the exception</param>
        /// <param name="cancellationToken">A cancellation token for cancelling the wait</param>
        /// <returns></returns>
        internal static async Task WithCancellation(
            this Task taskToComplete,
            string message,
            CancellationToken cancellationToken)
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
