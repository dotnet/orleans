using System.Collections.Concurrent;
using Orleans.Runtime;

namespace Orleans.DurableMessaging.Tests.Support;

public sealed class SnapshotProbe
{
    private readonly ConcurrentDictionary<GrainId, DurableEndpointSnapshot> _latest = new();
    private readonly ConcurrentDictionary<GrainId, List<Waiter>> _waiters = new();

    public Task<DurableEndpointSnapshot> WaitAsync(
        GrainId grainId,
        Func<DurableEndpointSnapshot, bool> predicate)
    {
        if (_latest.TryGetValue(grainId, out var current) && predicate(current))
        {
            return Task.FromResult(current);
        }

        var waiter = new Waiter(predicate);
        var waiters = _waiters.GetOrAdd(grainId, static _ => []);
        lock (waiters)
        {
            if (_latest.TryGetValue(grainId, out current) && predicate(current))
            {
                return Task.FromResult(current);
            }

            waiters.Add(waiter);
        }

        return waiter.Completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    public void Publish(GrainId grainId, DurableEndpointSnapshot snapshot)
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
        }
    }

    private sealed class Waiter(Func<DurableEndpointSnapshot, bool> predicate)
    {
        public Func<DurableEndpointSnapshot, bool> Predicate { get; } = predicate;
        public TaskCompletionSource<DurableEndpointSnapshot> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
