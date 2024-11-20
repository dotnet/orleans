using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Orleans.Messaging;
using Orleans.Runtime.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Runtime.Membership;

public class ZooKeeperClusteringClientOptions : IGatewayListProvider
{
    private TimeSpan maxStaleness;
    private readonly ZooKeeperBasedMembershipTable zooKeeperBasedMembershipTable;

    public ZooKeeperClusteringClientOptions(
        ZooKeeperBasedMembershipTable zooKeeperBasedMembershipTable,
        ILogger<ZooKeeperClusteringClientOptions> logger,
        IOptions<ZooKeeperGatewayListProviderOptions> options,
        IOptions<GatewayOptions> gatewayOptions,
        IOptions<ClusterOptions> clusterOptions)
    {
        maxStaleness = gatewayOptions.Value.GatewayListRefreshPeriod;
        this.zooKeeperBasedMembershipTable = zooKeeperBasedMembershipTable;
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
        var membershipTableData = await zooKeeperBasedMembershipTable.ReadAll();

        return membershipTableData
            .Members
            .Select(e => e.Item1)
            .Where(m => m.Status == SiloStatus.Active && m.ProxyPort != 0)
            .Select(m =>
            {
                var endpoint = new IPEndPoint(m.SiloAddress.Endpoint.Address, m.ProxyPort);
                var gatewayAddress = SiloAddress.New(endpoint, m.SiloAddress.Generation);

                return gatewayAddress.ToGatewayUri();
            }).ToList();
    }

    /// <summary>
    /// Specifies how often this IGatewayListProvider is refreshed, to have a bound on max staleness of its returned information.
    /// </summary>
    public TimeSpan MaxStaleness
    {
        get { return maxStaleness; }
    }

    /// <summary>
    /// Specifies whether this IGatewayListProvider ever refreshes its returned information, or always returns the same gw list.
    /// (currently only the static config based StaticGatewayListProvider is not updatable. All others are.)
    /// </summary>
    public bool IsUpdatable
    {
        get { return true; }
    }
}