using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.LogConsistency;
using Orleans.Runtime;
using Orleans.Providers;
using Orleans.Storage;

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
    public class LogConsistencyProvider : ILogConsistencyProvider
    {
        private ILogger logger;
        /// <inheritdoc/>
        public string Name { get; private set; }

        /// <summary>
        /// Specifies a clusterid of the primary cluster from which to access storage exclusively, null if
        /// storage should be accessed direcly from all clusters.
        /// </summary>
        public string PrimaryCluster { get; private set; }

        /// <inheritdoc/>
        public bool UsesStorageProvider { get  { return false; } }

        /// <summary>
        /// Init function
        /// </summary>
        /// <param name="name">provider name</param>
        /// <param name="providerRuntime">provider runtime, see <see cref="IProviderRuntime"/></param>
        /// <param name="config">provider configuration</param>
        public Task Init(string name, IProviderRuntime providerRuntime, IProviderConfiguration config)
        {
            Name = name;
            PrimaryCluster = config.GetProperty("PrimaryCluster", null);

            var loggerName = $"{this.GetType().FullName}.{Name}";
            var loggerFactory = providerRuntime.ServiceProvider.GetRequiredService<ILoggerFactory>();
            this.logger = loggerFactory.CreateLogger(loggerName);
            logger.Info("Init PrimaryCluster={1}", string.IsNullOrEmpty(PrimaryCluster) ? "(none specified)" : PrimaryCluster);

            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task Close()
        {
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public ILogViewAdaptor<TView, TEntry> MakeLogViewAdaptor<TView, TEntry>(ILogViewAdaptorHost<TView, TEntry> hostgrain, TView initialstate, string graintypename, IStorageProvider storageProvider, ILogConsistencyProtocolServices services)
            where TView : class, new()
            where TEntry : class
        {
            return new CustomStorageAdaptor<TView, TEntry>(hostgrain, initialstate, services, PrimaryCluster);
        }

    }

}