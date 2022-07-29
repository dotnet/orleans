using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Orleans.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Runtime.Membership
{
    public class ZooKeeperGatewayListProvider : IGatewayListProvider
    {
        private readonly ZooKeeperWatcher _watcher;

        /// <summary>
        /// the node name for this deployment. for eg. /ClusterId
        /// </summary>
        private readonly string _deploymentPath;

        /// <summary>
        /// The deployment connection string. for eg. "192.168.1.1,192.168.1.2/ClusterId"
        /// </summary>
        private readonly string _deploymentConnectionString;
        private readonly TimeSpan _maxStaleness;

        public ZooKeeperGatewayListProvider(
            ILogger<ZooKeeperGatewayListProvider> logger,
            IOptions<ZooKeeperGatewayListProviderOptions> options,
            IOptions<GatewayOptions> gatewayOptions,
            IOptions<ClusterOptions> clusterOptions)
        {
            _watcher = new ZooKeeperWatcher(logger);
            _deploymentPath = "/" + clusterOptions.Value.ClusterId;
            _deploymentConnectionString = options.Value.ConnectionString + _deploymentPath;
            _maxStaleness = gatewayOptions.Value.GatewayListRefreshPeriod;
        }

        /// <summary>
        /// Initializes the ZooKeeper based gateway provider
        /// </summary>
        public Task InitializeGatewayListProvider() => Task.CompletedTask;

        /// <summary>
        /// Returns the list of gateways (silos) that can be used by a client to connect to Orleans cluster.
        /// The Uri is in the form of: "gwy.tcp://IP:port/Generation". See Utils.ToGatewayUri and Utils.ToSiloAddress for more details about Uri format.
        /// </summary>
        public async Task<IList<Uri>> GetGateways()
        {
            var membershipTableData = await ZooKeeperBasedMembershipTable.ReadAll(this._deploymentConnectionString, this._watcher);
            return membershipTableData.Members.Select(e => e.Item1).
                Where(m => m.Status == SiloStatus.Active && m.ProxyPort != 0).
                Select(m =>
                {
                    var gatewayAddress = SiloAddress.New(m.SiloAddress.Endpoint.Address, m.ProxyPort, m.SiloAddress.Generation);
                    return gatewayAddress.ToGatewayUri();
                }).ToList();
        }

        /// <summary>
        /// Specifies how often this IGatewayListProvider is refreshed, to have a bound on max staleness of its returned information.
        /// </summary>
        public TimeSpan MaxStaleness => _maxStaleness;

        /// <summary>
        /// Specifies whether this IGatewayListProvider ever refreshes its returned information, or always returns the same gw list.
        /// (currently only the static config based StaticGatewayListProvider is not updatable. All others are.)
        /// </summary>
        public bool IsUpdatable => true;
    }
}
