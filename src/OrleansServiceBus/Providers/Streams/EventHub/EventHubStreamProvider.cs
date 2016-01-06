
using Orleans.Providers.Streams.Common;

namespace Orleans.ServiceBus.Providers
{
    /// <summary>
    /// Persistent stream provider that uses EventHub for persistence
    /// TODO: This stream provider is still under development.  DO NOT USE IN PRODUCTION - jbragg
    ///  </summary>
    public class EventHubStreamProvider : PersistentStreamProvider<EventHubAdapterFactory>
    {
    }
}
