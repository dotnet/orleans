using Orleans.Providers.Streams.Common;

namespace Orleans.Serialization.Providers.Streams
{
    /// <summary>
    /// Persistent stream provider that uses Google PubSub as backend
    /// </summary>
    public class GooglePubSubStreamProvider : PersistentStreamProvider<GooglePubSubAdapterFactory<IGooglePubSubDataAdapter>>
    {
    }
}
