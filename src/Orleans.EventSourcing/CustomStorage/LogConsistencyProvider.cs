using Orleans.Storage;
using Orleans.Configuration;
using System;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using OrleansEventSourcing.CustomStorage;

namespace Orleans.EventSourcing.CustomStorage
{
    /// <summary>
    /// A log-consistency provider that relies on grain-specific custom code for
    /// reading states from storage, and appending deltas to storage.
    /// Grains that wish to use this provider must implement the <see cref="ICustomStorageInterface{TState, TDelta}"/>
    /// interface, to define how state is read and how deltas are written.
    /// The configured primary cluster identifier is passed to each custom-storage adaptor.
    /// Custom-storage adaptors accept submissions from every cluster.
    /// </summary>
    public class LogConsistencyProvider : ILogViewAdaptorFactory
    {
        private readonly CustomStorageLogConsistencyOptions options;
        private readonly IServiceProvider serviceProvider;

        /// <summary>
        /// Gets the cluster identifier passed to each custom-storage adaptor.
        /// </summary>
        public string? PrimaryCluster => options.PrimaryCluster;

        /// <inheritdoc/>
        public bool UsesStorageProvider => false;

        /// <summary>
        /// Initializes a new instance of the <see cref="LogConsistencyProvider"/> class.
        /// </summary>
        /// <param name="options">The provider configuration.</param>
        /// <param name="serviceProvider">The service provider used to resolve custom storage implementations.</param>
        public LogConsistencyProvider(CustomStorageLogConsistencyOptions options, IServiceProvider serviceProvider)
        {
            this.options = options;
            this.serviceProvider = serviceProvider;
        }

        /// <inheritdoc/>
        public ILogViewAdaptor<TView, TEntry> MakeLogViewAdaptor<TView, TEntry>(ILogViewAdaptorHost<TView, TEntry> hostGrain, TView initialState, string grainTypeName, IGrainStorage? grainStorage, ILogConsistencyProtocolServices services)
            where TView : class, new()
            where TEntry : class
        {
            var customStorage = CustomStorageHelpers.GetCustomStorage<TView, TEntry>(hostGrain, services.GrainId, serviceProvider);
            return new CustomStorageAdaptor<TView, TEntry>(hostGrain, initialState, services, PrimaryCluster, customStorage);
        }
    }

    /// <summary>
    /// Creates custom-storage log consistency providers from named options.
    /// </summary>
    public static class LogConsistencyProviderFactory
    {
        /// <summary>
        /// Creates a custom-storage log consistency provider.
        /// </summary>
        /// <param name="services">The service provider.</param>
        /// <param name="name">The name of the provider configuration.</param>
        /// <returns>The configured log view adaptor factory.</returns>
        public static ILogViewAdaptorFactory Create(IServiceProvider services, string? name)
        {
            var optionsMonitor = services.GetRequiredService<IOptionsMonitor<CustomStorageLogConsistencyOptions>>();
            return ActivatorUtilities.CreateInstance<LogConsistencyProvider>(services, optionsMonitor.Get(name));
        }
    }
}
