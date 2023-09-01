using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Clustering.AdoNet.Storage;
using Orleans.Messaging;
using Orleans.Configuration;

namespace Orleans.Runtime.Membership
{
    public class AdoNetGatewayListProvider : IGatewayListProvider
    {
        private readonly ILogger _logger;
        private readonly string _clusterId;
        private readonly AdoNetClusteringClientOptions _options;
        private RelationalOrleansQueries _orleansQueries;
        private readonly IServiceProvider _serviceProvider;
        private readonly TimeSpan _maxStaleness;

        public AdoNetGatewayListProvider(
            ILogger<AdoNetGatewayListProvider> logger, 
            IServiceProvider serviceProvider,
            IOptions<AdoNetClusteringClientOptions> options,
            IOptions<GatewayOptions> gatewayOptions,
            IOptions<ClusterOptions> clusterOptions)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
            _options = options.Value;
            _clusterId = clusterOptions.Value.ClusterId;
            _maxStaleness = gatewayOptions.Value.GatewayListRefreshPeriod;
        }

        public TimeSpan MaxStaleness
        {
            get { return _maxStaleness; }
        }

        public bool IsUpdatable
        {
            get { return true; }
        }

        public async Task InitializeGatewayListProvider()
        {
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("AdoNetClusteringTable.InitializeGatewayListProvider called.");
            _orleansQueries = await RelationalOrleansQueries.CreateInstance(_options.Invariant, _options.ConnectionString);
        }

        public async Task<IList<Uri>> GetGateways()
        {
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("AdoNetClusteringTable.GetGateways called.");
            try
            {
                return await _orleansQueries.ActiveGatewaysAsync(_clusterId);
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug(ex, "AdoNetClusteringTable.Gateways failed");
                throw;
            }
        }
    }
}
