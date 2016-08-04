using Orleans.Providers.Streams.Common;

namespace Orleans.Providers.Streams.Memory
{
    /// <summary>
    /// This is a persistent stream provider that uses in-memory grain to queue the events.
    /// This is primarily for test purposes.
    /// </summary> 
    public class MemoryStreamProvider : PersistentStreamProvider<MemoryAdapterFactory>
    {
    }
}
