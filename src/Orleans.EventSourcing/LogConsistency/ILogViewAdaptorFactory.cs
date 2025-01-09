
using Orleans.Storage;

namespace Orleans.EventSourcing
{
    /// <summary>
    /// Interface to be implemented for a log-view adaptor factory
    /// </summary>
    public interface ILogViewAdaptorFactory  
    {
        /// <summary> Returns true if a storage provider is required for constructing adaptors. </summary>
        bool UsesStorageProvider { get; }

        /// <summary>
        /// Constructs a <see cref="ILogViewAdaptor{TLogView,TLogEntry}"/> to be installed in the given host grain.
        /// </summary>
        /// <typeparam name="TLogView">The type of the view</typeparam>
        /// <typeparam name="TLogEntry">The type of the log entries</typeparam>
        /// <param name="hostGrain">The grain that is hosting this adaptor</param>
        /// <param name="initialState">The initial state for this view</param>
        /// <param name="grainTypeName">The type name of the grain</param>
        /// <param name="grainStorage">Storage provider</param>
        /// <param name="services">Runtime services for multi-cluster coherence protocols</param>
        ILogViewAdaptor<TLogView, TLogEntry> MakeLogViewAdaptor<TLogView, TLogEntry>(
            ILogViewAdaptorHost<TLogView, TLogEntry> hostGrain,
            TLogView initialState,
            string grainTypeName,
            IGrainStorage grainStorage,
            ILogConsistencyProtocolServices services)

            where TLogView : class, new()
            where TLogEntry : class;

    }
}
