#if NEW_RUNTIME
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.Dissemination;

namespace Orleans.Dissemination.PerformanceHarness;

internal static class NewRuntime
{
    public static void Configure(IServiceCollection services, NodeConfiguration configuration)
    {
        services.Configure<DisseminationOptions>(options => options.Enabled = configuration.Enabled);
        services.Configure<ClusterMembershipOptions>(options => options.Dissemination.Enabled = configuration.Enabled);
        services.Configure<DeploymentLoadPublisherOptions>(options => options.Dissemination.Enabled = configuration.Enabled);
    }

    public static NodeSnapshot Decorate(IServiceProvider services, NodeSnapshot snapshot)
    {
        var options = services.GetRequiredService<IOptions<DisseminationOptions>>().Value;
        if (!options.Enabled)
        {
            return snapshot with
            {
                NamespaceEnabled = services.GetRequiredService<IOptions<DeploymentLoadPublisherOptions>>().Value.Dissemination.Enabled,
            };
        }

        var ns = services.GetRequiredService<DeploymentLoadStatisticsDisseminationNamespace>();
        var topology = services.GetRequiredService<DisseminationMembership>().CurrentSnapshots.ActiveMembers;
        var aggregationTree = ns.RoutingMode == DisseminationRoutingMode.AggregationTree;
        var fanout = AggregationFanout.Read(
            options.Overlay, aggregationTree, topology.Members.Length, () => options.Overlay.GetFanOutFactor(topology.Members.Length));
        return snapshot with
        {
            Enabled = options.Enabled,
            NamespaceEnabled = ns.Options.Enabled,
            UnconfirmedPeers = services.GetRequiredService<IDisseminationService>()
                .GetUnconfirmedPeers(ns).Select(address => address.ToParsableString()).Order(StringComparer.Ordinal).ToArray(),
            OriginatorTargets = topology.GetOriginatorTargets(ns.RoutingMode).Select(address => address.ToParsableString()).ToArray(),
            ForwardingTargets = topology.GetForwardingTargets(ns.RoutingMode).Select(address => address.ToParsableString()).ToArray(),
            TopologyMembers = topology.Members.Select(address => address.ToParsableString()).ToArray(),
            Fanout = fanout.Value,
            FanoutSource = fanout.Source,
            AggregationTree = aggregationTree,
            AntiEntropyPeerCount = options.Overlay.AntiEntropyPeerCount,
            AntiEntropyIntervalMilliseconds = options.Overlay.AntiEntropyInterval.TotalMilliseconds,
        };
    }
}
#endif
