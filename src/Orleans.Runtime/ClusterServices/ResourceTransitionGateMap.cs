namespace Orleans.Runtime.ClusterServices;

/// <summary>
/// Associates exact resource identities with gates without owning their role-specific lifecycle.
/// </summary>
internal sealed class ResourceTransitionGateMap<TResourceId, TViewId>
    where TResourceId : notnull
    where TViewId : IComparable<TViewId>, IEquatable<TViewId>
{
    private readonly object _lock = new();
    private readonly Dictionary<TResourceId, List<TransitionGate<TViewId>>> _transitions;

    public ResourceTransitionGateMap(IEqualityComparer<TResourceId>? comparer = null) =>
        _transitions = new(comparer);

    public void Add(TResourceId resource, TransitionGate<TViewId> gate)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(gate);
        lock (_lock)
        {
            if (_transitions.TryGetValue(resource, out var transitions))
            {
                transitions.RemoveAll(static existing => !existing.IsBlocking);
            }
            else
            {
                _transitions.Add(resource, transitions = []);
            }

            foreach (var existing in transitions)
            {
                if (existing.TargetView.Equals(gate.TargetView))
                {
                    throw new InvalidOperationException($"A transition for this resource already exists for view '{gate.TargetView}'.");
                }
            }

            transitions.Add(gate);
        }
    }

    public bool IsBlocked(TResourceId resource, TViewId requestView) =>
        TryGetBlockingTransition(resource, requestView, out _);

    public bool TryGetBlockingTransition(TResourceId resource, TViewId requestView, out Task completion)
    {
        lock (_lock)
        {
            if (_transitions.TryGetValue(resource, out var transitions))
            {
                foreach (var candidate in transitions)
                {
                    if (candidate.TargetView.CompareTo(requestView) <= 0 && candidate.IsBlocking)
                    {
                        completion = candidate.Completion;
                        return true;
                    }
                }
            }
        }

        completion = Task.CompletedTask;
        return false;
    }

    /// <summary>
    /// Removes completed or aborted associations for one resource without scanning other resources.
    /// Failed gates remain blocking and are retained.
    /// </summary>
    public void Prune(TResourceId resource)
    {
        lock (_lock)
        {
            if (_transitions.TryGetValue(resource, out var transitions))
            {
                transitions.RemoveAll(static gate => !gate.IsBlocking);
                if (transitions.Count == 0)
                {
                    _transitions.Remove(resource);
                }
            }
        }
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
            foreach (var transitions in _transitions.Values)
            {
                foreach (var gate in transitions)
                {
                    gate.Abort(cancellationToken);
                }
            }

            PruneCore();
        }
    }

    private void PruneCore()
    {
        foreach (var entry in _transitions)
        {
            entry.Value.RemoveAll(static gate => !gate.IsBlocking);
            if (entry.Value.Count == 0)
            {
                _transitions.Remove(entry.Key);
            }
        }
    }
}
