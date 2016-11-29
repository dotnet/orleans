
using Orleans.Providers.Streams.Common;

namespace Orleans.ServiceBus.Providers
{
    /// <summary>
    /// Persistent stream provider that uses EventHub for persistence
    ///  </summary>
    public class EventHubStreamProvider : PersistentStreamProvider<EventHubAdapterFactory>
    {
    }
}
