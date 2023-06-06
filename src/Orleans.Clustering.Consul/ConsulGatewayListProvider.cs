using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Consul;
using Orleans.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Runtime.Membership
{
    public class ConsulGatewayListProvider : IGatewayListProvider
    {
        private IConsulClient consulClient;
        private readonly string clusterId;
        private readonly ILogger logger;
        private readonly ConsulClusteringOptions options;
        private readonly string kvRootFolder;

        public ConsulGatewayListProvider(
            ILogger<ConsulGatewayListProvider> logger, 
            IOptions<ConsulClusteringOptions> options, 
            IOptions<GatewayOptions> gatewayOptions, 
            IOptions<ClusterOptions> clusterOptions)
        {
            this.logger = logger;
            clusterId = clusterOptions.Value.ClusterId;
            MaxStaleness = gatewayOptions.Value.GatewayListRefreshPeriod;
            this.options = options.Value;
            kvRootFolder = options.Value.KvRootFolder;
        }

        public TimeSpan MaxStaleness { get; }

        public bool IsUpdatable => true;
        public Task InitializeGatewayListProvider()
        {
            consulClient = options.CreateClient();
            return Task.CompletedTask;
        }

        public async Task<IList<Uri>> GetGateways()
        {
            var membershipTableData = await ConsulBasedMembershipTable.ReadAll(consulClient, clusterId, kvRootFolder, logger, null);
            if (membershipTableData == null) return new List<Uri>();

            return membershipTableData.Members.Select(e => e.Item1).
                Where(m => m.Status == SiloStatus.Active && m.ProxyPort != 0).
                Select(m =>
                {
                    var gatewayAddress = SiloAddress.New(m.SiloAddress.Endpoint.Address, m.ProxyPort, m.SiloAddress.Generation);
                    return gatewayAddress.ToGatewayUri();
                }).ToList();
        }
    }


}
