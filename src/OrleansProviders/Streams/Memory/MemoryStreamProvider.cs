using Orleans.Providers.Streams.Common;

namespace Orleans.Providers
{
    /// <summary>
    /// This is a persistent stream provider that uses in-memory grain to queue the events.
    /// This is primarily for test purposes.
    /// </summary> 
    public class MemoryStreamProvider : PersistentStreamProvider<MemoryAdapterFactory<DefaultMemoryMessageBodySerializer>>
    {
    }

    /// <summary>
    /// This is a persistent stream provider that uses in-memory grain to queue the events.
    /// This is primarily for test purposes.
    /// </summary> 
    public class MemoryStreamProvider<TSerializer> : PersistentStreamProvider<MemoryAdapterFactory<TSerializer>>
        where TSerializer : IMemoryMessageBodySerializer, new()
    {
    }
}
