using System;
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
        /// Observes and ignores a potential exception on a given Task.
        /// If a Task fails and throws an exception which is never observed, it will be caught by the .NET finalizer thread.
        /// This function awaits the given task and if the exception is thrown, it observes this exception and simply ignores it.
        /// This will prevent the escalation of this exception to the .NET finalizer thread.
        /// </summary>
        /// <param name="task">The task to be ignored.</param>
        public static void Ignore(this Task task)
        {
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