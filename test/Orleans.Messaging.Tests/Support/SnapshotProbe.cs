using System.Collections.Concurrent;
using Orleans.Runtime;

namespace Orleans.Messaging.Tests.Support;

public sealed class SnapshotProbe
{
    private readonly ConcurrentDictionary<GrainId, EndpointSnapshot> _latest = new();
    private readonly ConcurrentDictionary<GrainId, List<Waiter>> _waiters = new();

    internal int WaiterListCount => _waiters.Count;

    public async Task<EndpointSnapshot> WaitAsync(
        GrainId grainId,
        Func<EndpointSnapshot, bool> predicate,
        TimeSpan? timeout = null)
    {
        if (_latest.TryGetValue(grainId, out var current) && predicate(current))
        {
            return current;
        }

        var waiter = new Waiter(predicate);
        List<Waiter> waiters;
        while (true)
        {
            waiters = _waiters.GetOrAdd(grainId, static _ => []);
            lock (waiters)
            {
                if (!_waiters.TryGetValue(grainId, out var currentWaiters)
                    || !ReferenceEquals(waiters, currentWaiters))
                {
                    continue;
                }

                if (_latest.TryGetValue(grainId, out current) && predicate(current))
                {
                    RemoveWaiterListIfEmpty(grainId, waiters);
                    return current;
                }

                waiters.Add(waiter);
                break;
            }
        }

        try
        {
            return await waiter.Completion.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(30));
        }
        finally
        {
            lock (waiters)
            {
                waiters.Remove(waiter);
                RemoveWaiterListIfEmpty(grainId, waiters);
            }
        }
    }

    public void Publish(GrainId grainId, EndpointSnapshot snapshot)
    {
        _latest[grainId] = snapshot;
        if (!_waiters.TryGetValue(grainId, out var waiters))
        {
            return;
        }

        lock (waiters)
        {
            foreach (var waiter in waiters.ToArray())
            {
                if (waiter.Predicate(snapshot))
                {
                    waiters.Remove(waiter);
                    waiter.Completion.TrySetResult(snapshot);
                }
            }

            RemoveWaiterListIfEmpty(grainId, waiters);
        }
    }

    private void RemoveWaiterListIfEmpty(GrainId grainId, List<Waiter> waiters)
    {
        if (waiters.Count == 0)
        {
            _waiters.TryRemove(new KeyValuePair<GrainId, List<Waiter>>(grainId, waiters));
        }
    }

    private sealed class Waiter(Func<EndpointSnapshot, bool> predicate)
    {
        public Func<EndpointSnapshot, bool> Predicate { get; } = predicate;
        public TaskCompletionSource<EndpointSnapshot> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
