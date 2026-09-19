using Orleans.Runtime.ClusterServices;

namespace Orleans.Runtime.GrainDirectory;

internal sealed class DirectoryAcquisition(
    RingRange range,
    ClusterServiceViewId previous,
    ClusterServiceViewId target) : OwnershipAcquisition<ClusterServiceViewId>(previous, target)
{
    public RingRange Range { get; } = range;
}

internal sealed class DirectoryRelease(
    RingRange range,
    ClusterServiceViewId previous,
    ClusterServiceViewId target) : OwnershipRelease<ClusterServiceViewId>(previous, target)
{
    public RingRange Range { get; } = range;
}

internal sealed class DirectoryBarrier(RingRange range, ClusterServiceViewId target) : ViewBarrier<ClusterServiceViewId>(target)
{
    public RingRange Range { get; } = range;
}

internal sealed class DirectoryTransitions
{
    private readonly RangeTransitionGateMap<ClusterServiceViewId> _gates = new();

    public void Add(RingRange range, TransitionGate<ClusterServiceViewId> gate) => _gates.Add(range, gate);

    public bool TryGetBlockingTransition(RingRange range, ClusterServiceViewId requestView, out Task completion) =>
        _gates.TryGetBlockingTransition(range, requestView, out completion);

    public void Prune() => _gates.Prune();

    public void AbortAll(CancellationToken cancellationToken) => _gates.AbortAll(cancellationToken);
}
