using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Consul;
using Orleans.Messaging;
using Orleans.Runtime.Configuration;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.Membership
{
    public class ConsulBasedGatewayListProvider : IGatewayListProvider
    {
        private ConsulClient consulClient;
        private TimeSpan _maxStaleness;
        private string deploymentId;
        private ILogger logger;
        public ConsulBasedGatewayListProvider(ILogger<ConsulBasedGatewayListProvider> logger)
        {
            this.logger = logger;
        }

        public TimeSpan MaxStaleness
        {
            get { return _maxStaleness; }
        }

        public Boolean IsUpdatable
        {
            get { return true; }
        }
        public Task InitializeGatewayListProvider(ClientConfiguration configuration)
        {
            _maxStaleness = configuration.GatewayListRefreshPeriod;

            this.deploymentId = configuration.DeploymentId;

            consulClient =
                new ConsulClient(config => config.Address = new Uri(configuration.DataConnectionString));

            return Task.CompletedTask;
        }

        public async Task<IList<Uri>> GetGateways()
        {
            var membershipTableData = await ConsulBasedMembershipTable.ReadAll(this.consulClient, this.deploymentId, this.logger);
            if (membershipTableData == null) return new List<Uri>();

            return membershipTableData.Members.Select(e => e.Item1).
                Where(m => m.Status == SiloStatus.Active && m.ProxyPort != 0).
                Select(m =>
                {
                    m.SiloAddress.Endpoint.Port = m.ProxyPort;
                    return m.SiloAddress.ToGatewayUri();
                }).ToList();
        }
    }


}
