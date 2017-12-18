using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Messaging;
using Orleans.Runtime.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.AzureUtils.Options;
using Orleans.Runtime;

namespace Orleans.AzureUtils
{
    internal class AzureGatewayListProvider : IGatewayListProvider
    {
        private OrleansSiloInstanceManager siloInstanceManager;
        private readonly string clusterId;
        private readonly AzureTableGatewayListProviderOptions options;
        private readonly ILoggerFactory loggerFactory;
        private readonly TimeSpan maxStaleness;

        public AzureGatewayListProvider(ILoggerFactory loggerFactory, IOptions<AzureTableGatewayListProviderOptions> options, IOptions<ClusterClientOptions> clusterClientOptions, ClientConfiguration clientConfiguration)
        {
            this.loggerFactory = loggerFactory;
            this.clusterId = clusterClientOptions.Value.ClusterId;
            this.maxStaleness = clientConfiguration.GatewayListRefreshPeriod;
            this.options = options.Value;
        }

        #region Implementation of IGatewayListProvider

        public async Task InitializeGatewayListProvider()
        {
            siloInstanceManager = await OrleansSiloInstanceManager.GetManager(this.clusterId, this.options.ConnectionString, this.loggerFactory);
        }
        // no caching
        public Task<IList<Uri>> GetGateways()
        {
            // FindAllGatewayProxyEndpoints already returns a deep copied List<Uri>.
            return siloInstanceManager.FindAllGatewayProxyEndpoints();
        }

        public TimeSpan MaxStaleness
        {
            get { return this.maxStaleness; }
        }

        public bool IsUpdatable
        {
            get { return true; }
        }

        #endregion
    }
}
