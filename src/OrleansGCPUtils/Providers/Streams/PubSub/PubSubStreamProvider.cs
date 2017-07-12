using Orleans.Providers.Streams.Common;

namespace Orleans.Providers.GCP.Streams.PubSub
{
    /// <summary>
    /// Persistent stream provider that uses Google PubSub as backend
    /// </summary>
    public class PubSubStreamProvider : PersistentStreamProvider<PubSubAdapterFactory<PubSubDataAdapter>>
    {
    }
}
