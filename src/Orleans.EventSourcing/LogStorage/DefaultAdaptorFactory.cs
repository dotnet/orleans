using Orleans.Storage;

namespace Orleans.EventSourcing.LogStorage
{
    internal class DefaultAdaptorFactory : ILogViewAdaptorFactory
    {
        public bool UsesStorageProvider => true;

        public ILogViewAdaptor<T, E> MakeLogViewAdaptor<T, E>(ILogViewAdaptorHost<T, E> hostgrain, T initialstate, string graintypename, IGrainStorage grainStorage, ILogConsistencyProtocolServices services)
           where T : class, new() where E : class => new LogViewAdaptor<T, E>(hostgrain, initialstate, grainStorage, graintypename, services);

    }
}
