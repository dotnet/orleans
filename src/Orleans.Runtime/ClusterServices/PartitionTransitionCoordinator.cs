using Orleans.Runtime.GrainDirectory;

namespace Orleans.Runtime.ClusterServices;

internal enum PartitionTransitionDirection
{
    Inbound,
    Outbound,
    Barrier
}

internal enum PartitionTransitionStage
{
    Blocking,
    Drained,
    StateRetained,
    StateInstalled,
    Fenced,
    Failed,
    Completed,
    Aborted
}

internal enum ClusterServiceFencingMode
{
    MembershipView,
    TimedSafetyLease,
    External
}

[GenerateSerializer, Immutable, Alias(nameof(ClusterServiceFence))]
internal readonly record struct ClusterServiceFence(
    [property: Id(0)] ClusterServiceFencingMode Mode,
    [property: Id(1)] long Token);

internal sealed class PartitionTransitionCoordinator
{
    private readonly object _lock = new();
    private readonly List<PartitionTransition> _transitions = [];

    public PartitionTransition BeginInbound(
        RingRange range,
        ClusterServiceViewId previousView,
        ClusterServiceViewId targetView) =>
        Begin(range, previousView, targetView, PartitionTransitionDirection.Inbound);

    public PartitionTransition BeginOutbound(
        RingRange range,
        ClusterServiceViewId previousView,
        ClusterServiceViewId targetView) =>
        Begin(range, previousView, targetView, PartitionTransitionDirection.Outbound);

    public PartitionTransition BeginBarrier(RingRange range, ClusterServiceViewId view) =>
        Begin(range, view, view, PartitionTransitionDirection.Barrier);

    public bool IsBlocked(RingRange range, MembershipVersion requestVersion)
    {
        lock (_lock)
        {
            return TryGetBlockingTransitionCore(range, requestVersion, out _);
        }
    }

    public bool TryGetBlockingTransition(
        RingRange range,
        MembershipVersion requestVersion,
        out Task completion)
    {
        lock (_lock)
        {
            if (TryGetBlockingTransitionCore(range, requestVersion, out var transition))
            {
                completion = transition.Completion;
                return true;
            }
        }

        completion = Task.CompletedTask;
        return false;
    }

    private PartitionTransition Begin(
        RingRange range,
        ClusterServiceViewId previousView,
        ClusterServiceViewId targetView,
        PartitionTransitionDirection direction)
    {
        if (range.IsEmpty)
        {
            throw new ArgumentException("A transition range must contain at least one point.", nameof(range));
        }

        if (direction is not PartitionTransitionDirection.Barrier
            && targetView.MembershipVersion <= previousView.MembershipVersion)
        {
            throw new ArgumentException("The target view must be newer than the previous view.", nameof(targetView));
        }

        var transition = new PartitionTransition(this, range, previousView, targetView, direction);
        lock (_lock)
        {
            if (_transitions.Any(existing =>
                existing.TargetView == targetView
                && existing.Range.Intersects(range)))
            {
                throw new InvalidOperationException(
                    $"An overlapping transition already exists for view '{targetView}'.");
            }

            _transitions.Add(transition);
        }

        return transition;
    }

    private bool TryGetBlockingTransitionCore(
        RingRange range,
        MembershipVersion requestVersion,
        out PartitionTransition transition)
    {
        foreach (var candidate in _transitions)
        {
            if (candidate.TargetView.MembershipVersion <= requestVersion
                && candidate.Range.Intersects(range)
                && candidate.Stage is not (PartitionTransitionStage.Completed or PartitionTransitionStage.Aborted))
            {
                transition = candidate;
                return true;
            }
        }

        transition = null!;
        return false;
    }

    internal void Complete(PartitionTransition transition, bool aborted, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (!_transitions.Remove(transition))
            {
                throw new InvalidOperationException("The transition is no longer active.");
            }

            transition.CompleteCore(aborted, cancellationToken);
        }
    }

    internal void Fail(PartitionTransition transition, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        lock (_lock)
        {
            if (!_transitions.Remove(transition))
            {
                throw new InvalidOperationException("The transition is no longer active.");
            }

            transition.FailCore(exception);
        }
    }
}

internal sealed class PartitionTransition
{
    private readonly PartitionTransitionCoordinator _owner;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _stage = (int)PartitionTransitionStage.Blocking;

    internal PartitionTransition(
        PartitionTransitionCoordinator owner,
        RingRange range,
        ClusterServiceViewId previousView,
        ClusterServiceViewId targetView,
        PartitionTransitionDirection direction)
    {
        _owner = owner;
        Range = range;
        PreviousView = previousView;
        TargetView = targetView;
        Direction = direction;
    }

    public RingRange Range { get; }

    public ClusterServiceViewId PreviousView { get; }

    public ClusterServiceViewId TargetView { get; }

    public PartitionTransitionDirection Direction { get; }

    public PartitionTransitionStage Stage => (PartitionTransitionStage)Volatile.Read(ref _stage);

    public ClusterServiceFence? Fence { get; private set; }

    public Exception? Failure { get; private set; }

    public Task Completion => _completion.Task;

    public void MarkDrained()
    {
        EnsureDirection(PartitionTransitionDirection.Outbound);
        Transition(PartitionTransitionStage.Blocking, PartitionTransitionStage.Drained);
    }

    public void MarkStateRetained()
    {
        EnsureDirection(PartitionTransitionDirection.Outbound);
        Transition(PartitionTransitionStage.Drained, PartitionTransitionStage.StateRetained);
    }

    public void MarkStateInstalled()
    {
        EnsureDirection(PartitionTransitionDirection.Inbound);
        Transition(PartitionTransitionStage.Blocking, PartitionTransitionStage.StateInstalled);
    }

    public void MarkFenced(ClusterServiceFence fence)
    {
        EnsureDirection(PartitionTransitionDirection.Inbound);
        Transition(PartitionTransitionStage.StateInstalled, PartitionTransitionStage.Fenced);
        Fence = fence;
    }

    public void Complete()
    {
        var stage = Stage;
        if (Direction == PartitionTransitionDirection.Inbound && stage != PartitionTransitionStage.Fenced)
        {
            throw new InvalidOperationException("An inbound transition must install state and establish fencing before activation.");
        }

        if (Direction == PartitionTransitionDirection.Outbound
            && stage is not (PartitionTransitionStage.Drained or PartitionTransitionStage.StateRetained))
        {
            throw new InvalidOperationException("An outbound transition must drain operations before completion.");
        }

        if (Direction == PartitionTransitionDirection.Barrier && stage != PartitionTransitionStage.Blocking)
        {
            throw new InvalidOperationException("A barrier transition must remain blocked until completion.");
        }

        _owner.Complete(this, aborted: false, CancellationToken.None);
    }

    public void Abort(CancellationToken cancellationToken = default) =>
        _owner.Complete(this, aborted: true, cancellationToken);

    public void Fail(Exception exception) => _owner.Fail(this, exception);

    internal void CompleteCore(bool aborted, CancellationToken cancellationToken)
    {
        Volatile.Write(
            ref _stage,
            (int)(aborted ? PartitionTransitionStage.Aborted : PartitionTransitionStage.Completed));
        if (aborted)
        {
            _completion.TrySetCanceled(cancellationToken.IsCancellationRequested
                ? cancellationToken
                : new CancellationToken(canceled: true));
        }
        else
        {
            _completion.TrySetResult();
        }
    }

    internal void FailCore(Exception exception)
    {
        var stage = Stage;
        if (stage is PartitionTransitionStage.Failed or PartitionTransitionStage.Completed or PartitionTransitionStage.Aborted)
        {
            throw new InvalidOperationException($"Transition stage '{stage}' cannot fail.");
        }

        Failure = exception;
        Volatile.Write(ref _stage, (int)PartitionTransitionStage.Failed);
        _completion.TrySetException(exception);
    }

    private void EnsureDirection(PartitionTransitionDirection expected)
    {
        if (Direction != expected)
        {
            throw new InvalidOperationException(
                $"Transition direction '{Direction}' does not support this operation.");
        }
    }

    private void Transition(PartitionTransitionStage expected, PartitionTransitionStage next)
    {
        var observed = Interlocked.CompareExchange(ref _stage, (int)next, (int)expected);
        if (observed != (int)expected)
        {
            throw new InvalidOperationException(
                $"Transition stage '{(PartitionTransitionStage)observed}' cannot advance to '{next}'.");
        }
    }
}
