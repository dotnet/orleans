using Orleans.Runtime.Placement;
using Orleans.Runtime.Placement.Repartitioning;

namespace UnitTests.ActivationRepartitioningTests;

/// <summary>
/// Ignores client messages to make testing easier
/// </summary>
internal sealed class TestMessageFilter(GrainMigratabilityChecker checker) : IRepartitionerMessageFilter
{
    private readonly RepartitionerMessageFilter _messageFilter = new(checker);

    public bool IsAcceptable(GrainId sendingGrain, GrainId targetGrain, out bool isSenderMigratable, out bool isTargetMigratable) =>
        _messageFilter.IsAcceptable(sendingGrain, targetGrain, out isSenderMigratable, out isTargetMigratable) &&
        !sendingGrain.IsClient() && !targetGrain.IsClient();
}
