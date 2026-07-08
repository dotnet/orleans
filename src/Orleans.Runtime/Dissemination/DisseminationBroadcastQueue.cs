using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Orleans.Runtime.Dissemination;

internal sealed class DisseminationBroadcastQueue(
    TimeProvider timeProvider,
    Func<IReadOnlyList<DisseminationBroadcastQueue.Batch>, CancellationToken, Task> sendBatches,
    Action<Exception> logFlushFailed)
{
    private readonly object _lock = new();
    private readonly Dictionary<SiloAddress, PendingBroadcastBatch> _pendingBroadcast = [];
    private DateTimeOffset? _nextFlushAt;
    private CancellationTokenSource? _flushWakeup;
    private bool _flushScheduled;

    public void Enqueue(
        SiloAddress peer,
        DisseminationBroadcastValue item,
        IDisseminationNamespace disseminationNamespace,
        int maxBatchItems,
        int maxBatchBytes)
    {
        var now = timeProvider.GetUtcNow();
        var key = new DigestKey(disseminationNamespace.Name, item.Value.Key);
        lock (_lock)
        {
            if (!_pendingBroadcast.TryGetValue(peer, out var pending))
            {
                pending = new PendingBroadcastBatch(now + disseminationNamespace.Options.MaxCoalescingDelay);
                _pendingBroadcast.Add(peer, pending);
            }
            else if (pending.TryGetValue(key, out var existing)
                && existing.Value.ToVersion >= item.Value.ToVersion)
            {
                return;
            }
            else if (now + disseminationNamespace.Options.MaxCoalescingDelay < pending.FlushAfter)
            {
                pending.FlushAfter = now + disseminationNamespace.Options.MaxCoalescingDelay;
            }

            pending.AddOrReplace(key, item);
            if (pending.Count >= maxBatchItems
                || pending.ByteCount >= maxBatchBytes
                || pending.GetNamespaceCount(disseminationNamespace.Name) >= disseminationNamespace.Options.MaxPendingItemCount)
            {
                pending.FlushAfter = now;
            }
        }

        ScheduleFlush();
    }

    public async Task FlushPendingBroadcast(CancellationToken cancellationToken)
    {
        var batches = DrainPendingBroadcast(force: true);
        CancelScheduledFlushDelay();
        await sendBatches(batches, cancellationToken);
    }

    public void Prune(DisseminationMembershipSnapshot membershipSnapshot, SiloAddress localSilo)
    {
        lock (_lock)
        {
            List<SiloAddress>? removedPeers = null;
            foreach (var peer in _pendingBroadcast.Keys)
            {
                if (localSilo.Equals(peer))
                {
                    continue;
                }

                if (!membershipSnapshot.ContainsMember(peer))
                {
                    (removedPeers ??= []).Add(peer);
                }
            }

            if (removedPeers is not null)
            {
                foreach (var peer in removedPeers)
                {
                    _pendingBroadcast.Remove(peer);
                }
            }

            if (_pendingBroadcast.Count == 0)
            {
                _nextFlushAt = null;
                _flushWakeup?.Cancel();
            }
        }
    }

    private void ScheduleFlush()
    {
        var startFlushLoop = false;
        lock (_lock)
        {
            if (_pendingBroadcast.Count == 0)
            {
                return;
            }

            var next = GetNextPendingBroadcastFlushUnsafe();
            if (!_flushScheduled)
            {
                _flushScheduled = true;
                _nextFlushAt = next;
                startFlushLoop = true;
            }
            else if (_nextFlushAt is null || next < _nextFlushAt.Value)
            {
                _nextFlushAt = next;
                _flushWakeup?.Cancel();
            }
        }

        if (startFlushLoop)
        {
            _ = Task.Run(RunScheduledFlush);
        }
    }

    private async Task RunScheduledFlush()
    {
        try
        {
            while (true)
            {
                var delay = GetDelayUntilNextFlush(out var wakeupToken);
                if (delay is null)
                {
                    return;
                }

                if (delay > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(delay.Value, timeProvider, wakeupToken);
                    }
                    catch (OperationCanceledException) when (wakeupToken.IsCancellationRequested)
                    {
                        continue;
                    }
                }

                var batches = DrainPendingBroadcast(force: false);
                await sendBatches(batches, CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            logFlushFailed(exception);
        }
        finally
        {
            bool reschedule;
            lock (_lock)
            {
                _flushScheduled = false;
                _nextFlushAt = null;
                DisposeFlushWakeupUnsafe();
                reschedule = _pendingBroadcast.Count > 0;
            }

            if (reschedule)
            {
                ScheduleFlush();
            }
        }
    }

    private TimeSpan? GetDelayUntilNextFlush(out CancellationToken wakeupToken)
    {
        lock (_lock)
        {
            if (_pendingBroadcast.Count == 0)
            {
                _nextFlushAt = null;
                DisposeFlushWakeupUnsafe();
                wakeupToken = CancellationToken.None;
                return null;
            }

            var now = timeProvider.GetUtcNow();
            var next = GetNextPendingBroadcastFlushUnsafe();
            _nextFlushAt = next;
            if (next <= now)
            {
                DisposeFlushWakeupUnsafe();
                wakeupToken = CancellationToken.None;
                return TimeSpan.Zero;
            }

            DisposeFlushWakeupUnsafe();
            _flushWakeup = new CancellationTokenSource();
            wakeupToken = _flushWakeup.Token;
            return next - now;
        }
    }

    private void CancelScheduledFlushDelay()
    {
        lock (_lock)
        {
            _flushWakeup?.Cancel();
        }
    }

    private DateTimeOffset GetNextPendingBroadcastFlushUnsafe() =>
        _pendingBroadcast.Values.Min(static pending => pending.FlushAfter);

    private List<Batch> DrainPendingBroadcast(bool force)
    {
        var now = timeProvider.GetUtcNow();
        var result = new List<Batch>();
        lock (_lock)
        {
            List<SiloAddress>? drainedPeers = null;
            foreach (var (peer, pending) in _pendingBroadcast)
            {
                if (!force && pending.FlushAfter > now)
                {
                    continue;
                }

                (drainedPeers ??= []).Add(peer);
                result.Add(new Batch(peer, pending.ToImmutableValuesByNamespace()));
            }

            if (drainedPeers is not null)
            {
                foreach (var peer in drainedPeers)
                {
                    _pendingBroadcast.Remove(peer);
                }
            }
        }

        result.Sort(static (left, right) => left.Peer.CompareTo(right.Peer));
        return result;
    }

    private void DisposeFlushWakeupUnsafe()
    {
        _flushWakeup?.Dispose();
        _flushWakeup = null;
    }

    public readonly record struct Batch(SiloAddress Peer, ImmutableArray<PendingNamespaceValues> ValuesByNamespace);

    public readonly record struct PendingNamespaceValues(string Namespace, ImmutableArray<DisseminationBroadcastValue> Values);

    private readonly record struct DigestKey(string Namespace, string Key);

    private sealed class PendingBroadcastBatch(DateTimeOffset flushAfter)
    {
        private readonly Dictionary<string, Dictionary<string, DisseminationBroadcastValue>> _valuesByNamespace = new(StringComparer.Ordinal);

        public DateTimeOffset FlushAfter { get; set; } = flushAfter;

        public int Count { get; private set; }

        public int ByteCount { get; private set; }

        public int GetNamespaceCount(string namespaceName) => _valuesByNamespace.TryGetValue(namespaceName, out var values) ? values.Count : 0;

        public bool TryGetValue(DigestKey key, [NotNullWhen(true)] out DisseminationBroadcastValue? value)
        {
            if (_valuesByNamespace.TryGetValue(key.Namespace, out var namespaceValues))
            {
                return namespaceValues.TryGetValue(key.Key, out value!);
            }

            value = null;
            return false;
        }

        public void AddOrReplace(DigestKey key, DisseminationBroadcastValue value)
        {
            if (!_valuesByNamespace.TryGetValue(key.Namespace, out var namespaceValues))
            {
                namespaceValues = new Dictionary<string, DisseminationBroadcastValue>(StringComparer.Ordinal);
                _valuesByNamespace.Add(key.Namespace, namespaceValues);
            }

            if (namespaceValues.TryGetValue(key.Key, out var previous))
            {
                ByteCount -= previous.Value.Payload.Length;
            }
            else
            {
                Count++;
            }

            namespaceValues[key.Key] = value;
            ByteCount += value.Value.Payload.Length;
        }

        public ImmutableArray<PendingNamespaceValues> ToImmutableValuesByNamespace()
        {
            var result = ImmutableArray.CreateBuilder<PendingNamespaceValues>(_valuesByNamespace.Count);
            foreach (var (namespaceName, values) in _valuesByNamespace)
            {
                result.Add(new PendingNamespaceValues(namespaceName, [.. values.Values]));
            }

            return result.ToImmutable();
        }
    }
}
