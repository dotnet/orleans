using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Messaging;
using Orleans.Runtime.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Runtime.Membership
{
    public class ZooKeeperClusteringClientOptions : IGatewayListProvider
    {
        private ZooKeeperWatcher watcher;
        /// <summary>
        /// the node name for this deployment. for eg. /ClusterId
        /// </summary>
        private string deploymentPath;

        /// <summary>
        /// The deployment connection string. for eg. "192.168.1.1,192.168.1.2/ClusterId"
        /// </summary>
        private string deploymentConnectionString;
        private TimeSpan maxStaleness;
        public ZooKeeperClusteringClientOptions(
            ILogger<ZooKeeperClusteringClientOptions> logger,
            IOptions<ZooKeeperGatewayListProviderOptions> options,
            IOptions<GatewayOptions> gatewayOptions,
            IOptions<ClusterOptions> clusterOptions)
        {
            watcher = new ZooKeeperWatcher(logger);

            deploymentPath = "/" + clusterOptions.Value.ClusterId;
            deploymentConnectionString = options.Value.ConnectionString + deploymentPath;
            maxStaleness = gatewayOptions.Value.GatewayListRefreshPeriod;
        }

        /// <summary>
        /// Initializes the ZooKeeper based gateway provider
        /// </summary>
        public Task InitializeGatewayListProvider()
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// Returns the list of gateways (silos) that can be used by a client to connect to Orleans cluster.
        /// The Uri is in the form of: "gwy.tcp://IP:port/Generation". See Utils.ToGatewayUri and Utils.ToSiloAddress for more details about Uri format.
        /// </summary>
        public async Task<IList<Uri>> GetGateways()
        {
            var membershipTableData = await ZooKeeperBasedMembershipTable.ReadAll(this.deploymentConnectionString, this.watcher);
            return membershipTableData.Members.Select(e => e.Item1).
                Where(m => m.Status == SiloStatus.Active && m.ProxyPort != 0).
                Select(m =>
                {
                    m.SiloAddress.Endpoint.Port = m.ProxyPort;
                    return m.SiloAddress.ToGatewayUri();
                }).ToList();
        }

        /// <summary>
        /// Specifies how often this IGatewayListProvider is refreshed, to have a bound on max staleness of its returned infomation.
        /// </summary>
        public TimeSpan MaxStaleness
        {
            get { return maxStaleness; }
        }

        /// <summary>
        /// Specifies whether this IGatewayListProvider ever refreshes its returned infomation, or always returns the same gw list.
        /// (currently only the static config based StaticGatewayListProvider is not updatable. All others are.)
        /// </summary>
        public bool IsUpdatable
        {
            get { return true; }
        }
    }
}
