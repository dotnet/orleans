using Orleans.Runtime;

namespace Orleans
{
    /// <summary>
    /// The IGrainObserver interface is a marker interface for observers.
    /// Observers are used to receive notifications from grains; that is, they represent the subscriber side of a 
    /// publisher/subscriber interface.
    /// Note that all observer methods should be void, since they do not return a value to the observed grain.
    /// </summary>
    public interface IGrainObserver : IAddressable
    {
    }
}
