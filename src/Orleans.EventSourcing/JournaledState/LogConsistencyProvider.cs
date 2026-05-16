using Orleans.Storage;

#nullable disable
namespace Orleans.EventSourcing.JournaledState
{
    /// <summary>
    /// A log-consistency provider that stores event-sourcing events in the host grain's journaled state.
    /// </summary>
    public sealed class LogConsistencyProvider : ILogViewAdaptorFactory
    {
        /// <inheritdoc/>
        public bool UsesStorageProvider => false;

        /// <inheritdoc/>
        public ILogViewAdaptor<TView, TEntry> MakeLogViewAdaptor<TView, TEntry>(
            ILogViewAdaptorHost<TView, TEntry> hostGrain,
            TView initialState,
            string grainTypeName,
            IGrainStorage grainStorage,
            ILogConsistencyProtocolServices services)
            where TView : class, new()
            where TEntry : class
        {
            ArgumentNullException.ThrowIfNull(hostGrain);
            ArgumentNullException.ThrowIfNull(initialState);
            ArgumentNullException.ThrowIfNull(services);

            return new LogViewAdaptor<TView, TEntry>(hostGrain, initialState, services);
        }
    }
}
