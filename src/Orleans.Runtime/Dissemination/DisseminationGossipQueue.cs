using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Orleans.Runtime.Dissemination;

internal sealed class DisseminationGossipQueue(
    TimeProvider timeProvider,
    Func<IReadOnlyList<DisseminationGossipQueue.Batch>, CancellationToken, Task> sendBatches,
    Action<Exception> logFlushFailed)
{
    private readonly object _lock = new();
    private readonly Dictionary<SiloAddress, PendingGossipBatch> _pendingGossip = [];
    private DateTimeOffset? _nextFlushAt;
    private CancellationTokenSource? _flushWakeup;
    private bool _flushScheduled;

    public void Enqueue(
        SiloAddress peer,
        DisseminationValue item,
        IDisseminationTopic topic,
        int maxBatchItems,
        int maxBatchBytes)
    {
        var now = timeProvider.GetUtcNow();
        var key = new DigestKey(topic.Name, item.Digest.Key);
        lock (_lock)
        {
            if (!_pendingGossip.TryGetValue(peer, out var pending))
            {
                pending = new PendingGossipBatch(now + topic.Options.MaxCoalescingDelay);
                _pendingGossip.Add(peer, pending);
            }
            else if (pending.TryGetValue(key, out var existing)
                && existing.Digest.Version >= item.Digest.Version)
            {
                return;
            }
            else if (now + topic.Options.MaxCoalescingDelay < pending.FlushAfter)
            {
                pending.FlushAfter = now + topic.Options.MaxCoalescingDelay;
            }

            pending.AddOrReplace(key, item);
            if (pending.Count >= maxBatchItems
                || pending.ByteCount >= maxBatchBytes
                || pending.GetTopicCount(topic.Name) >= topic.Options.MaxPendingItemCount)
            {
                pending.FlushAfter = now;
            }
        }

        ScheduleFlush();
    }

    public async Task FlushPendingGossip(CancellationToken cancellationToken)
    {
        var batches = DrainPendingGossip(force: true);
        CancelScheduledFlushDelay();
        await sendBatches(batches, cancellationToken);
    }

    public void Prune(DisseminationMembershipSnapshot membershipSnapshot, SiloAddress localSilo)
    {
        lock (_lock)
        {
            List<SiloAddress>? removedPeers = null;
            foreach (var peer in _pendingGossip.Keys)
            {
                if (localSilo.Equals(peer))
                {
                    continue;
                }

                if (!membershipSnapshot.ContainsParticipant(peer))
                {
                    (removedPeers ??= []).Add(peer);
                }
            }

            if (removedPeers is not null)
            {
                foreach (var peer in removedPeers)
                {
                    _pendingGossip.Remove(peer);
                }
            }

            if (_pendingGossip.Count == 0)
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
            if (_pendingGossip.Count == 0)
            {
                return;
            }

            var next = GetNextPendingGossipFlushUnsafe();
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

                var batches = DrainPendingGossip(force: false);
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
                reschedule = _pendingGossip.Count > 0;
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
            if (_pendingGossip.Count == 0)
            {
                _nextFlushAt = null;
                DisposeFlushWakeupUnsafe();
                wakeupToken = CancellationToken.None;
                return null;
            }

            var now = timeProvider.GetUtcNow();
            var next = GetNextPendingGossipFlushUnsafe();
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

    private DateTimeOffset GetNextPendingGossipFlushUnsafe() =>
        _pendingGossip.Values.Min(static pending => pending.FlushAfter);

    private List<Batch> DrainPendingGossip(bool force)
    {
        var now = timeProvider.GetUtcNow();
        var result = new List<Batch>();
        lock (_lock)
        {
            List<SiloAddress>? drainedPeers = null;
            foreach (var (peer, pending) in _pendingGossip)
            {
                if (!force && pending.FlushAfter > now)
                {
                    continue;
                }

                (drainedPeers ??= []).Add(peer);
                result.Add(new Batch(peer, pending.ToImmutableValuesByTopic()));
            }

            if (drainedPeers is not null)
            {
                foreach (var peer in drainedPeers)
                {
                    _pendingGossip.Remove(peer);
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

    public readonly record struct Batch(SiloAddress Peer, ImmutableArray<PendingTopicValues> ValuesByTopic);

    public readonly record struct PendingTopicValues(string Topic, ImmutableArray<DisseminationValue> Values);

    private readonly record struct DigestKey(string Topic, string Key);

    private sealed class PendingGossipBatch(DateTimeOffset flushAfter)
    {
        private readonly Dictionary<string, Dictionary<string, DisseminationValue>> _valuesByTopic = new(StringComparer.Ordinal);

        public DateTimeOffset FlushAfter { get; set; } = flushAfter;

        public int Count { get; private set; }

        public int ByteCount { get; private set; }

        public int GetTopicCount(string topic) => _valuesByTopic.TryGetValue(topic, out var values) ? values.Count : 0;

        public bool TryGetValue(DigestKey key, [NotNullWhen(true)] out DisseminationValue? value)
        {
            if (_valuesByTopic.TryGetValue(key.Topic, out var topicValues))
            {
                return topicValues.TryGetValue(key.Key, out value!);
            }

            value = null;
            return false;
        }

        public void AddOrReplace(DigestKey key, DisseminationValue value)
        {
            if (!_valuesByTopic.TryGetValue(key.Topic, out var topicValues))
            {
                topicValues = new Dictionary<string, DisseminationValue>(StringComparer.Ordinal);
                _valuesByTopic.Add(key.Topic, topicValues);
            }

            if (topicValues.TryGetValue(key.Key, out var previous))
            {
                ByteCount -= previous.Payload.Length;
            }
            else
            {
                Count++;
            }

            topicValues[key.Key] = value;
            ByteCount += value.Payload.Length;
        }

        public ImmutableArray<PendingTopicValues> ToImmutableValuesByTopic()
        {
            var result = ImmutableArray.CreateBuilder<PendingTopicValues>(_valuesByTopic.Count);
            foreach (var (topic, values) in _valuesByTopic)
            {
                result.Add(new PendingTopicValues(topic, [.. values.Values]));
            }

            return result.ToImmutable();
        }
    }
}
