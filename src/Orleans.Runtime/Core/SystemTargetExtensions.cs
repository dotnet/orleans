using System;
using System.Threading.Tasks;
using Orleans.Runtime.Scheduler;

namespace Orleans.Runtime
{
    /// <summary>
    /// Extensions for <see cref="SystemTarget"/>.
    /// </summary>
    public static class SystemTargetExtensions
    {
        /// <summary>
        /// Schedules the provided <paramref name="action"/> on the <see cref="SystemTarget"/>.
        /// </summary>
        /// <param name="self">The <see cref="SystemTarget"/>.</param>
        /// <param name="action">The action.</param>
        /// <returns>A <see cref="Task"/> which completes when the <paramref name="action"/> has completed.</returns>
        public static Task ScheduleTask(this SystemTarget self, Func<Task> action)
        {
            return self.RuntimeClient.Scheduler.RunOrQueueTask(action, self);
        }

        /// <summary>
        /// Schedules the provided <paramref name="action"/> on the <see cref="SystemTarget"/>.
        /// </summary>
        /// <param name="self">The <see cref="SystemTarget"/>.</param>
        /// <param name="action">The action.</param>
        /// <returns>A <see cref="Task"/> which completes when the <paramref name="action"/> has completed.</returns>
        public static Task ScheduleTask(this SystemTarget self, Action action)
        {
            return self.RuntimeClient.Scheduler.RunOrQueueAction(action, self);
        }
    }
}