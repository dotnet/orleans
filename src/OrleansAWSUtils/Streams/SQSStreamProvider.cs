using Orleans.Providers.Streams.Common;
using OrleansAWSUtils.Streams;

namespace Orleans.Providers.Streams
{
    /// <summary>
    /// Persistent stream provider that uses azure queue for persistence
    /// </summary>
    public class SQSStreamProvider : PersistentStreamProvider<SQSAdapterFactory>
    {
    }
}
