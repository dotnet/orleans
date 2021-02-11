using Orleans.Storage;
using Orleans.Configuration;
using System;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.EventSourcing.CustomStorage
{
    /// <summary>
    /// A log-consistency provider that relies on grain-specific custom code for 
    /// reading states from storage, and appending deltas to storage.
    /// Grains that wish to use this provider must implement the <see cref="ICustomStorageInterface{TState, TDelta}"/>
    /// interface, to define how state is read and how deltas are written.
    /// If the provider attribute "PrimaryCluster" is supplied in the provider configuration, then only the specified cluster
    /// accesses storage, and other clusters may not issue updates. 
    /// </summary>
    public class LogConsistencyProvider : ILogViewAdaptorFactory
    {
        private readonly CustomStorageLogConsistencyOptions options;

        /// <summary>
        /// Specifies a clusterid of the primary cluster from which to access storage exclusively, null if
        /// storage should be accessed directly from all clusters.
        /// </summary>
        public string PrimaryCluster => options.PrimaryCluster;

        /// <inheritdoc/>
        public bool UsesStorageProvider => false;
        
        public LogConsistencyProvider(CustomStorageLogConsistencyOptions options)
        {
            this.options = options;
        }

        /// <inheritdoc/>
        public ILogViewAdaptor<TView, TEntry> MakeLogViewAdaptor<TView, TEntry>(ILogViewAdaptorHost<TView, TEntry> hostgrain, TView initialstate, string graintypename, IGrainStorage grainStorage, ILogConsistencyProtocolServices services)
            where TView : class, new()
            where TEntry : class
        {
            return new CustomStorageAdaptor<TView, TEntry>(hostgrain, initialstate, services, PrimaryCluster);
        }
    }

    public static class LogConsistencyProviderFactory
    {
        public static ILogViewAdaptorFactory Create(IServiceProvider services, string name)
        {
            var optionsMonitor = services.GetRequiredService<IOptionsMonitor<CustomStorageLogConsistencyOptions>>();
            return ActivatorUtilities.CreateInstance<LogConsistencyProvider>(services, optionsMonitor.Get(name));
        }
    }
}