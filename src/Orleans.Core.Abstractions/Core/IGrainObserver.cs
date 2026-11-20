using Orleans.Runtime;

namespace Orleans
{
    /// <summary>
    /// A marker interface for grain observers.
    /// Observers are used to receive notifications from grains; that is, they represent the subscriber side of a 
    /// publisher/subscriber interface.
    /// </summary>
    /// <remarks>
    /// Calls to a client observer reference execute sequentially. Async-enumerable bodies and disposal can
    /// execute concurrently with ordinary calls and other enumerations. Operations on each enumerator are serialized.
    /// </remarks>
    public interface IGrainObserver : IAddressable
    {
    }
}
