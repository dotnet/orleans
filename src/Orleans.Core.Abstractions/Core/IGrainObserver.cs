using Orleans.Runtime;

namespace Orleans
{
    /// <summary>
    /// A marker interface for grain observers.
    /// Observers are used to receive notifications from grains; that is, they represent the subscriber side of a 
    /// publisher/subscriber interface.
    /// Note that all observer methods should have a <see langword="void"/> return type, since they do not return a value to the observed grain.
    /// </summary>
    public interface IGrainObserver : IAddressable
    {
    }
}
