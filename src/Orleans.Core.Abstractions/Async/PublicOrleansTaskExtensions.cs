using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans
{
    /// <summary>
    /// Utility functions for dealing with Tasks.
    /// </summary>
    public static class PublicOrleansTaskExtensions
    {
        internal static readonly Task<object> CanceledTask = TaskFromCanceled<object>();
        internal static readonly Task<object> CompletedTask = Task.FromResult(default(object));

        /// <summary>
        /// Observes and ignores a potential exception on a given Task.
        /// If a Task fails and throws an exception which is never observed, it will be caught by the .NET finalizer thread.
        /// This function awaits the given task and if the exception is thrown, it observes this exception and simply ignores it.
        /// This will prevent the escalation of this exception to the .NET finalizer thread.
        /// </summary>
        /// <param name="task">The task to be ignored.</param>
        [SuppressMessage("Microsoft.Performance", "CA1804:RemoveUnusedLocals", MessageId = "ignored")]
        public static void Ignore(this Task task)
        {
            if (task.IsCompleted)
            {
                var ignored = task.Exception;
            }
            else
            {
                task.ContinueWith(
                    t => { var ignored = t.Exception; },
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

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
        public static Task<T> Unbox<T>(this Task<object> task)
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
            var completion = new TaskCompletionSource<T> ();
            completion.SetException(task.Exception.InnerExceptions);
            return completion.Task;
        }

        private static Task<T> TaskFromCanceled<T>()
        {
            var completion = new TaskCompletionSource<T>();
            completion.SetCanceled();
            return completion.Task;
        }

        private static T GetResult<T>(Task<T> task)
        {
            return task.GetAwaiter().GetResult();
        }
        private static void GetResult(Task task)
        {
            task.GetAwaiter().GetResult();
        }
    }
}
