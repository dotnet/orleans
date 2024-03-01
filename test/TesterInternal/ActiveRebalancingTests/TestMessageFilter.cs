using Orleans.Runtime;
using Orleans.Runtime.Placement;
using Orleans.Runtime.Placement.Rebalancing;

namespace UnitTests.ActiveRebalancingTests;

/// <summary>
/// Ignores client messages to make testing easier
/// </summary>
internal sealed class TestMessageFilter : IRebalancingMessageFilter
{
    private readonly RebalancingMessageFilter _messageFilter;

    public TestMessageFilter(
        PlacementStrategyResolver strategyResolver,
        IClusterManifestProvider clusterManifestProvider)
        => _messageFilter = new(strategyResolver, clusterManifestProvider);

    public bool IsAcceptable(Message message, out bool isSenderMigratable, out bool isTargetMigratable) =>
        _messageFilter.IsAcceptable(message, out isSenderMigratable, out isTargetMigratable) &&
        !message.SendingGrain.IsClient() && !message.TargetGrain.IsClient();
}