using Orleans.Runtime.GrainDirectory;

namespace Orleans.Runtime.ClusterServices;

/// <summary>
/// Associates ring ranges with gates without owning their role-specific lifecycle.
/// </summary>
internal sealed class RangeTransitionGateMap<TViewId>
    where TViewId : IComparable<TViewId>, IEquatable<TViewId>
{
    private readonly object _lock = new();
    private readonly List<(RingRange Range, TransitionGate<TViewId> Gate)> _transitions = [];

    public void Add(RingRange range, TransitionGate<TViewId> gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        if (range.IsEmpty)
        {
            throw new ArgumentException("A transition range must contain at least one point.", nameof(range));
        }

        lock (_lock)
        {
            PruneCore();
            foreach (var existing in _transitions)
            {
                if (existing.Gate.TargetView.Equals(gate.TargetView) && existing.Range.Intersects(range))
                {
                    throw new InvalidOperationException($"An overlapping transition already exists for view '{gate.TargetView}'.");
                }
            }

            _transitions.Add((range, gate));
        }
    }

    public bool IsBlocked(RingRange range, TViewId requestView) =>
        TryGetBlockingTransition(range, requestView, out _);

    public bool TryGetBlockingTransition(RingRange range, TViewId requestView, out Task completion)
    {
        lock (_lock)
        {
            foreach (var candidate in _transitions)
            {
                if (candidate.Gate.TargetView.CompareTo(requestView) <= 0
                    && candidate.Range.Intersects(range)
                    && candidate.Gate.IsBlocking)
                {
                    completion = candidate.Gate.Completion;
                    return true;
                }
            }
        }

        completion = Task.CompletedTask;
        return false;
    }

    /// <summary>
    /// Removes completed or aborted associations. Failed gates remain blocking and are retained.
    /// </summary>
    public void Prune()
    {
        lock (_lock)
        {
            PruneCore();
        }
    }

    /// <summary>
    /// Releases all associations for terminal shutdown without replacing existing completion faults.
    /// </summary>
    public void AbortAll(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            foreach (var candidate in _transitions)
            {
                candidate.Gate.Abort(cancellationToken);
            }

            PruneCore();
        }
    }

    private void PruneCore() => _transitions.RemoveAll(static entry => !entry.Gate.IsBlocking);
}
