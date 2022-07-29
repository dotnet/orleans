using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Clustering.AdoNet.Storage;
using Orleans.Messaging;
using Orleans.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Runtime.Membership
{
    public class AdoNetGatewayListProvider : IGatewayListProvider
    {
        private readonly ILogger logger;
        private string clusterId;
        private readonly AdoNetClusteringClientOptions options;
        private RelationalOrleansQueries orleansQueries;
        private readonly IServiceProvider serviceProvider;
        private readonly TimeSpan maxStaleness;
        public AdoNetGatewayListProvider(
            ILogger<AdoNetGatewayListProvider> logger, 
            IServiceProvider serviceProvider,
            IOptions<AdoNetClusteringClientOptions> options,
            IOptions<GatewayOptions> gatewayOptions,
            IOptions<ClusterOptions> clusterOptions)
        {
            this.logger = logger;
            this.serviceProvider = serviceProvider;
            this.options = options.Value;
            this.clusterId = clusterOptions.Value.ClusterId;
            this.maxStaleness = gatewayOptions.Value.GatewayListRefreshPeriod;
        }

        public TimeSpan MaxStaleness
        {
            get { return this.maxStaleness; }
        }

        public bool IsUpdatable
        {
            get { return true; }
        }

        public async Task InitializeGatewayListProvider()
        {
            if (logger.IsEnabled(LogLevel.Trace)) logger.LogTrace("AdoNetClusteringTable.InitializeGatewayListProvider called.");
            orleansQueries = await RelationalOrleansQueries.CreateInstance(options.Invariant, options.ConnectionString);
        }

        public async Task<IList<Uri>> GetGateways()
        {
            if (logger.IsEnabled(LogLevel.Trace)) logger.LogTrace("AdoNetClusteringTable.GetGateways called.");
            try
            {
                return await orleansQueries.ActiveGatewaysAsync(this.clusterId);
            }
            catch (Exception ex)
            {
                if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug(ex, "AdoNetClusteringTable.Gateways failed");
                throw;
            }
        }
    }
}
