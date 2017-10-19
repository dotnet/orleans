using System.Threading.Tasks;

namespace Microsoft.Orleans.ServiceFabric
{
    internal interface IFabricServiceSiloResolver
    {
        /// <summary>
        /// Subscribes the provided handler for update notifications.
        /// </summary>
        /// <param name="handler">The update notification handler.</param>
        void Subscribe(IFabricServiceStatusListener handler);

        /// <summary>
        /// Unsubscribes the provided handler from update notifications.
        /// </summary>
        /// <param name="handler">The update notification handler.</param>
        void Unsubscribe(IFabricServiceStatusListener handler);

        /// <summary>
        /// Forces a refresh of the partitions.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        Task Refresh();
    }
}