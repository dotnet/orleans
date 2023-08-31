using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Clustering.AzureStorage;
using Orleans.Configuration;
using Orleans.Messaging;

namespace Orleans.AzureUtils
{
    internal class AzureGatewayListProvider : IGatewayListProvider
    {
        private OrleansSiloInstanceManager siloInstanceManager;
        private readonly string clusterId;
        private readonly AzureStorageGatewayOptions options;
        private readonly ILoggerFactory loggerFactory;

        public AzureGatewayListProvider(ILoggerFactory loggerFactory, IOptions<AzureStorageGatewayOptions> options, IOptions<ClusterOptions> clusterOptions, IOptions<GatewayOptions> gatewayOptions)
        {
            this.loggerFactory = loggerFactory;
            this.clusterId = clusterOptions.Value.ClusterId;
            this.MaxStaleness = gatewayOptions.Value.GatewayListRefreshPeriod;
            this.options = options.Value;
        }

        public async Task InitializeGatewayListProvider(CancellationToken cancellationToken)
        {
            this.siloInstanceManager = await OrleansSiloInstanceManager.GetManager(
                this.clusterId,
                this.loggerFactory,
                this.options,
                cancellationToken);
        }

        // no caching
        public Task<IList<Uri>> GetGateways(CancellationToken cancellationToken)
        {
            // FindAllGatewayProxyEndpoints already returns a deep copied List<Uri>.
            return this.siloInstanceManager.FindAllGatewayProxyEndpoints(cancellationToken);
        }

        public Task InitializeGatewayListProvider() => InitializeGatewayListProvider(CancellationToken.None);
        public Task<IList<Uri>> GetGateways() => GetGateways(CancellationToken.None);

        public TimeSpan MaxStaleness { get; }

        public bool IsUpdatable => true;
    }
}
