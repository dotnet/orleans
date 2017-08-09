
namespace Orleans
{
    /// <summary>
    /// Shared state publisher
    /// NOTE: All implementations must be thread safe
    /// NOTE: All state must be immutable
    /// </summary>
    public interface ISharedStatePublisher<in T>
    {
        /// <summary>
        /// Publish state
        /// </summary>
        /// <param name="state">immutable state to publish</param>
        void Publish(T state);
    }
}
