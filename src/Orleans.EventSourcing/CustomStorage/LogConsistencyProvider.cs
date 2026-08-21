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
    /// The storage implementation is supplied by the grain through <see cref="ICustomStorageInterface{TState, TDelta}"/>
    /// or created by a registered <see cref="ICustomStorageFactory"/>.
    /// If the provider attribute "PrimaryCluster" is supplied in the provider configuration, then only the specified cluster
    /// accesses storage, and other clusters may not issue updates.
    /// </summary>
    public class LogConsistencyProvider : ILogViewAdaptorFactory
    {
        private readonly CustomStorageLogConsistencyOptions options;
        private readonly IServiceProvider? serviceProvider;
        private readonly string? providerName;

        /// <summary>
        /// Specifies a cluster id of the primary cluster from which to access storage exclusively, null if
        /// storage should be accessed directly from all clusters.
        /// </summary>
        public string? PrimaryCluster => options.PrimaryCluster;

        /// <inheritdoc/>
        public bool UsesStorageProvider => false;

        public LogConsistencyProvider(CustomStorageLogConsistencyOptions options)
        {
            this.options = options;
        }

        internal LogConsistencyProvider(CustomStorageLogConsistencyOptions options, IServiceProvider serviceProvider, string providerName)
        {
            this.options = options;
            this.serviceProvider = serviceProvider;
            this.providerName = providerName;
        }

        /// <inheritdoc/>
        public ILogViewAdaptor<TView, TEntry> MakeLogViewAdaptor<TView, TEntry>(ILogViewAdaptorHost<TView, TEntry> hostGrain, TView initialState, string grainTypeName, IGrainStorage? grainStorage, ILogConsistencyProtocolServices services)
            where TView : class, new()
            where TEntry : class
        {
            var customStorage = CustomStorageHelpers.GetCustomStorage<TView, TEntry>(hostGrain, services.GrainId, serviceProvider, providerName);
            return new CustomStorageAdaptor<TView, TEntry>(hostGrain, initialState, services, PrimaryCluster, customStorage);
        }
    }

    public static class LogConsistencyProviderFactory
    {
        public static ILogViewAdaptorFactory Create(IServiceProvider services, string? name)
        {
            var optionsMonitor = services.GetRequiredService<IOptionsMonitor<CustomStorageLogConsistencyOptions>>();
            return name is null
                ? new LogConsistencyProvider(optionsMonitor.Get(name))
                : new LogConsistencyProvider(optionsMonitor.Get(name), services, name);
        }
    }
}