using Orleans.Runtime;

namespace Orleans.ClientObservers
{
    /// <summary>
    /// Base type for special client-wide observers.
    /// </summary>
    internal abstract class ClientObserver
    {
        /// <summary>
        /// Gets the observer id.
        /// </summary>
        /// <param name="clientId">The client id.</param>
        /// <returns>The observer id.</returns>
        internal abstract ObserverGrainId GetObserverGrainId(ClientGrainId clientId);
    }
}
