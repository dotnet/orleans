using Orleans.Providers.Streams.Common;

namespace Orleans.Providers.Streams
{
    /// <summary>
    /// Persistent stream provider that uses Google PubSub as backend
    /// </summary>
    public class GooglePubSubStreamProvider : PersistentStreamProvider<GooglePubSubAdapterFactory<GooglePubSubDataAdapter>>
    {
    }
}
