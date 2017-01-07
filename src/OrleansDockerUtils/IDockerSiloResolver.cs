using System;
using System.Threading.Tasks;

namespace Microsoft.Orleans.Docker
{
    internal interface IDockerSiloResolver
    {
        DateTime LastRefreshTime { get; }
        TimeSpan RefreshPeriod { get; }

        /// <summary>
        /// Subscribes the provided handler for update notifications.
        /// </summary>
        /// <param name="handler">The update notification handler.</param>
        void Subscribe(IDockerStatusListener handler);

        /// <summary>
        /// Unsubscribes the provided handler from update notifications.
        /// </summary>
        /// <param name="handler">The update notification handler.</param>
        void Unsubscribe(IDockerStatusListener handler);

        /// <summary>
        /// Forces a refresh of the partitions.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        Task Refresh();
    }
}
