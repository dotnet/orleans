using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans
{
    /// <summary>
    /// Utility functions for dealing with <see cref="Task"/> instances.
    /// </summary>
    public static class PublicOrleansTaskExtensions
    {
        private static readonly Action<Task> IgnoreTaskContinuation = t => { _ = t.Exception; };

        /// <summary>
        /// Waits for every supplied task, propagating a single failure directly or multiple failures as an <see cref="AggregateException"/>.
        /// </summary>
        /// <param name="tasks">The tasks to await.</param>
        /// <returns>
        /// A task which completes after all supplied tasks complete. It is faulted if any task fails,
        /// canceled if any task is canceled and none fail, and successful otherwise.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="tasks"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="tasks"/> contains a <see langword="null"/> task.</exception>
        /// <exception cref="AggregateException">The supplied tasks produce multiple failures.</exception>
        public static Task WhenAllWithAggregateException(IEnumerable<Task> tasks)
        {
            var combined = Task.WhenAll(tasks);
            return combined.IsCompletedSuccessfully ? combined : AwaitCompletion(combined);

            static async Task AwaitCompletion(Task task)
            {
                try
                {
                    await task.ConfigureAwait(false);
                }
                catch when (task.Exception is { InnerExceptions.Count: > 1 } exception)
                {
                    ExceptionDispatchInfo.Throw(exception);
                }
            }
        }

        /// <summary>
        /// Observes and ignores a potential exception on a given Task.
        /// If a Task fails and throws an exception which is never observed, it will be caught by the .NET finalizer thread.
        /// This function awaits the given task and if the exception is thrown, it observes this exception and simply ignores it.
        /// This will prevent the escalation of this exception to the .NET finalizer thread.
        /// </summary>
        /// <param name="task">The task to be ignored.</param>
        public static void Ignore(this Task task)
        {
            ArgumentNullException.ThrowIfNull(task);

            if (task.IsCompleted)
            {
                _ = task.Exception;
            }
            else
            {
                task.ContinueWith(
                    IgnoreTaskContinuation,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
    }
}
