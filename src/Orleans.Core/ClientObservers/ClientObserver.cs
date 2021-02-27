using Orleans.Runtime;

namespace Orleans.ClientObservers
{
    public abstract class ClientObserver
    {
        internal abstract ObserverGrainId GetObserverGrainId(ClientGrainId clientId);
    }
}
