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
    /// <summary>
    /// Provides Orleans gateway addresses from Consul cluster membership.
    /// </summary>
    public class ConsulGatewayListProvider : IGatewayListProvider
    {
        private IConsulClient consulClient = null!;
        private readonly string clusterId;
        private readonly ILogger logger;
        private readonly ConsulClusteringOptions options;
        private readonly TimeSpan maxStaleness;
        private readonly string? kvRootFolder;

        /// <summary>
        /// Initializes a new instance of the <see cref="ConsulGatewayListProvider"/> class.
        /// </summary>
        /// <param name="logger">The logger.</param>
        /// <param name="options">The Consul clustering options.</param>
        /// <param name="gatewayOptions">The gateway discovery options.</param>
        /// <param name="clusterOptions">The cluster identity options.</param>
        public ConsulGatewayListProvider(
            ILogger<ConsulGatewayListProvider> logger,
            IOptions<ConsulClusteringOptions> options,
            IOptions<GatewayOptions> gatewayOptions,
            IOptions<ClusterOptions> clusterOptions)
        {
            this.logger = logger;
            this.clusterId = clusterOptions.Value.ClusterId;
            this.maxStaleness = gatewayOptions.Value.GatewayListRefreshPeriod;
            this.options = options.Value;
            this.kvRootFolder = options.Value.KvRootFolder;
        }

        /// <inheritdoc />
        public TimeSpan MaxStaleness
        {
            get { return this.maxStaleness; }
        }

        /// <inheritdoc />
        public bool IsUpdatable
        {
            get { return true; }
        }
        /// <inheritdoc />
        public Task InitializeGatewayListProvider()
        {
            consulClient = options.CreateClient();
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task<IList<Uri>> GetGateways()
        {
            var membershipTableData = await ConsulBasedMembershipTable.ReadAll(this.consulClient, this.clusterId, this.kvRootFolder, this.logger, null);
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
