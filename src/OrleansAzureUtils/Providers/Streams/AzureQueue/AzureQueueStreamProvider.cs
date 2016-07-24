using Orleans.Providers.Streams.Common;

namespace Orleans.Providers.Streams.AzureQueue
{
    /// <summary>
    /// Persistent stream provider that uses azure queue for persistence
    /// </summary>
    public class AzureQueueStreamProvider : PersistentStreamProvider<AzureQueueAdapterFactory>
    {
    }
}
