using System.Collections.Concurrent;
using Orleans.Runtime;

namespace Orleans.Messaging.Tests.Support;

public sealed class SnapshotProbe
{
    private readonly ConcurrentDictionary<GrainId, EndpointSnapshot> _latest = new();
    private readonly ConcurrentDictionary<GrainId, ConcurrentQueue<string>> _history = new();
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
        catch (TimeoutException exception)
        {
            var published = _history.TryGetValue(grainId, out var history) ? string.Join(" | ", history) : "none";
            throw new TimeoutException($"Snapshot wait for '{grainId}' timed out. Published (oldest first): {published}", exception);
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
        var history = _history.GetOrAdd(grainId, static _ => new());
        history.Enqueue(Describe(snapshot));
        while (history.Count > 8) history.TryDequeue(out _);
        if (!_waiters.TryGetValue(grainId, out var waiters))
        {
            return;
        }

        lock (waiters)
        {
            foreach (var waiter in waiters.ToArray())
            {
                bool matched;
                try
                {
                    matched = waiter.Predicate(snapshot);
                }
                catch (Exception exception)
                {
                    waiters.Remove(waiter);
                    waiter.Completion.TrySetException(exception);
                    continue;
                }

                if (matched)
                {
                    waiters.Remove(waiter);
                    waiter.Completion.TrySetResult(snapshot);
                }
            }

            RemoveWaiterListIfEmpty(grainId, waiters);
        }
    }

    internal static string Describe(EndpointSnapshot snapshot) =>
        $"activation={snapshot.ActivationId}, inbox={snapshot.InboxCount}, outbox={snapshot.OutboxCount}, " +
        $"effects={snapshot.Effects.Sum(static effect => effect.Count)}, processed={snapshot.ProcessedMessageCount}, " +
        $"inboxDeadLetters={snapshot.InboxDeadLetters.Count}, outboxDeadLetters={snapshot.OutboxDeadLetters.Count}";

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
