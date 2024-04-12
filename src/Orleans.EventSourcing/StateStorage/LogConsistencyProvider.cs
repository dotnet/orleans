using Orleans.Storage;

namespace Orleans.EventSourcing.StateStorage
{
    /// <summary>
    /// A log-consistency provider that stores the latest view in primary storage, using any standard storage provider.
    /// Supports multiple clusters connecting to the same primary storage (doing optimistic concurrency control via e-tags)
    ///<para>
    /// The log itself is transient, i.e. not actually saved to storage - only the latest view (snapshot) and some 
    /// metadata (the log position, and write flags) are stored in the primary. 
    /// </para>
    /// </summary>
    public class LogConsistencyProvider : ILogViewAdaptorFactory
    {
        /// <inheritdoc/>
        public bool UsesStorageProvider
        {
            get
            {
                return true;
            }
        }

        /// <inheritdoc/>
        public ILogViewAdaptor<TView, TEntry> MakeLogViewAdaptor<TView, TEntry>(ILogViewAdaptorHost<TView, TEntry> hostGrain, TView initialState, string grainTypeName, IGrainStorage grainStorage, ILogConsistencyProtocolServices services) 
            where TView : class, new()
            where TEntry : class
        {
            return new LogViewAdaptor<TView,TEntry>(hostGrain, initialState, grainStorage, grainTypeName, services);
        }
    }
}