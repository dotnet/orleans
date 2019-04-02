using System;
using System.Threading.Tasks;

namespace Grains
{
    public static class TaskExtensions
    {
        /// <summary>
        /// Returns a task that completes when this task completes or when the timeout is reached.
        /// If the timeout is reached, then the task returns the given default value.
        /// </summary>
        public static async Task<T> WithDefaultOnTimeout<T>(this Task<T> task, TimeSpan timeout, T value)
        {
            if (task == null) throw new ArgumentNullException(nameof(task));

            return await await Task.WhenAny(task, Task.Delay(timeout).ContinueWith(_ => value));
        }
    }
}
