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
        private OrleansSiloInstanceManager siloInstanceManager = null!;
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

        public Task InitializeGatewayListProvider() => InitializeGatewayListProviderAsync(CancellationToken.None);

        public async Task InitializeGatewayListProviderAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.siloInstanceManager = await OrleansSiloInstanceManager.GetManager(
                this.clusterId,
                this.loggerFactory,
                this.options,
                cancellationToken);
        }
        // no caching
        public Task<IList<Uri>> GetGateways() => GetGatewaysAsync(CancellationToken.None);

        public Task<IList<Uri>> GetGatewaysAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // FindAllGatewayProxyEndpoints already returns a deep copied List<Uri>.
            return this.siloInstanceManager.FindAllGatewayProxyEndpoints(cancellationToken);
        }

        public TimeSpan MaxStaleness { get; }

        public bool IsUpdatable => true;
    }
}
