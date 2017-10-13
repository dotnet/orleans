namespace Orleans.Async
{
    using System;
    using System.Threading.Tasks;

    /// <summary>
    /// Extension methods for <see cref="Task"/>.
    /// </summary>
    public static class TaskUtility
    {
        /// <summary>
        /// Returns a faulted task.
        /// </summary>
        /// <param name="exception">The exception which the return task faulted with.</param>
        /// <returns>A faulted task.</returns>
        public static Task<object> Faulted(Exception exception)
        {
            var completion = new TaskCompletionSource<object>();
            completion.TrySetException(exception);
            return completion.Task;
        }

        /// <summary>
        /// Returns a completed task.
        /// </summary>
        /// <returns>A completed task.</returns>
        public static Task<object> Completed()
        {
            return Task.FromResult((object)null);
        }
    }
}
