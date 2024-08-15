using System;
using System.Runtime.CompilerServices;
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
        public static ConfiguredTaskAwaitable SuppressThrowing(this ValueTask task) => task.AsTask().ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        public static ConfiguredTaskAwaitable SuppressThrowing(this Task task) => task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);

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
