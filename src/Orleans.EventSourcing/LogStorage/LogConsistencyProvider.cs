using Orleans.Storage;


namespace Orleans.EventSourcing.LogStorage
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

        /// <summary>
        /// Make log view adaptor 
        /// </summary>
        /// <typeparam name="TView">The type of the view</typeparam>
        /// <typeparam name="TEntry">The type of the log entries</typeparam>
        /// <param name="hostGrain">The grain that is hosting this adaptor</param>
        /// <param name="initialState">The initial state for this view</param>
        /// <param name="grainTypeName">The type name of the grain</param>
        /// <param name="grainStorage">Storage provider</param>
        /// <param name="services">Runtime services for multi-cluster coherence protocols</param>
        public ILogViewAdaptor<TView, TEntry> MakeLogViewAdaptor<TView, TEntry>(ILogViewAdaptorHost<TView, TEntry> hostGrain, TView initialState, string grainTypeName, IGrainStorage grainStorage, ILogConsistencyProtocolServices services) 
            where TView : class, new()
            where TEntry : class
        {
            return new LogViewAdaptor<TView,TEntry>(hostGrain, initialState, grainStorage, grainTypeName, services);
        }
    }
}