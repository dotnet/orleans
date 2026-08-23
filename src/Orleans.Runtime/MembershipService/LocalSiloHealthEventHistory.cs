using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace Orleans.Runtime.MembershipService;

internal sealed class LocalSiloHealthEventHistory
{
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _retentionPeriod;
    private readonly long _bucketTimestampLength;
    private readonly Bucket[] _buckets;
    private readonly Dictionary<HealthEventIdentity, AggregateEntry> _candidates = [];
    private readonly List<LocalSiloHealthEvent> _selected = [];
    private int _count;
    private int _occupiedBucketCount;

    public LocalSiloHealthEventHistory(
        TimeProvider timeProvider,
        TimeSpan retentionPeriod,
        TimeSpan bucketDuration)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retentionPeriod, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(bucketDuration, TimeSpan.Zero);

        _timeProvider = timeProvider;
        _retentionPeriod = retentionPeriod;
        _bucketTimestampLength = GetTimestampLength(bucketDuration);
        _buckets = new Bucket[checked((int)Math.Ceiling(retentionPeriod / bucketDuration) + 1)];
    }

    internal int Count => _count;

    internal int OccupiedBucketCount => _occupiedBucketCount;

    public void Add(LocalSiloHealthEvent healthEvent)
    {
        RemoveExpired(healthEvent.Timestamp);
        AddCore(healthEvent);
    }

    public void AddRange(List<LocalSiloHealthEvent> healthEvents, long nowTimestamp)
    {
        RemoveExpired(nowTimestamp);
        foreach (var healthEvent in healthEvents)
        {
            AddCore(healthEvent);
        }
    }

    public LocalSiloHealthStatus Aggregate(
        long startTimestamp,
        long endTimestamp,
        long nowTimestamp,
        LocalSiloHealthCheckCategory categories,
        int maxScore)
    {
        RemoveExpired(nowTimestamp);
        if (categories == LocalSiloHealthCheckCategory.None)
        {
            return new(0, []);
        }

        try
        {
            foreach (var bucket in _buckets)
            {
                if (bucket.Events is not { Count: > 0 } events)
                {
                    continue;
                }

                foreach (var healthEvent in events)
                {
                    if ((healthEvent.Category & categories) == 0)
                    {
                        continue;
                    }

                    var isPriorState = healthEvent.Timestamp < startTimestamp
                        && IsStateful(healthEvent.Kind);
                    if (!isPriorState && !Overlaps(healthEvent, startTimestamp, endTimestamp))
                    {
                        continue;
                    }

                    var identity = new HealthEventIdentity(healthEvent.Kind, healthEvent.Source);
                    ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(_candidates, identity, out _);
                    if (isPriorState)
                    {
                        if (!entry.HasPriorState || healthEvent.Timestamp > entry.PriorState.Timestamp)
                        {
                            entry.PriorState = healthEvent;
                            entry.HasPriorState = true;
                        }

                        continue;
                    }

                    entry.HasBoundaryState |= healthEvent.Timestamp == startTimestamp && IsStateful(healthEvent.Kind);
                    if (!entry.HasIntervalEvent || IsBetter(healthEvent, entry.IntervalEvent))
                    {
                        entry.IntervalEvent = healthEvent;
                        entry.HasIntervalEvent = true;
                    }
                }
            }

            _selected.EnsureCapacity(_candidates.Count);
            var score = 0;
            foreach (var entry in _candidates.Values)
            {
                LocalSiloHealthEvent healthEvent;
                if (!entry.HasIntervalEvent)
                {
                    healthEvent = entry.PriorState;
                }
                else if (entry.HasPriorState
                    && !entry.HasBoundaryState
                    && IsBetter(entry.PriorState, entry.IntervalEvent))
                {
                    healthEvent = entry.PriorState;
                }
                else
                {
                    healthEvent = entry.IntervalEvent;
                }

                _selected.Add(healthEvent);
                score = (int)Math.Min(maxScore, (long)score + healthEvent.Score);
            }

            _selected.Sort(static (left, right) =>
            {
                var kindComparison = left.Kind.CompareTo(right.Kind);
                return kindComparison != 0
                    ? kindComparison
                    : string.CompareOrdinal(left.Source, right.Source);
            });
            return new(score, ImmutableArray.CreateRange(_selected));
        }
        finally
        {
            _candidates.Clear();
            _selected.Clear();
        }
    }

    private void AddCore(LocalSiloHealthEvent healthEvent)
    {
        var bucketIndex = GetBucketIndex(healthEvent.Timestamp);
        ref var bucket = ref _buckets[GetSlot(bucketIndex)];
        if (!bucket.IsOccupied)
        {
            bucket.IsOccupied = true;
            bucket.Index = bucketIndex;
            bucket.Events ??= [];
            _occupiedBucketCount++;
        }
        else if (bucket.Index != bucketIndex)
        {
            if (bucket.Index > bucketIndex)
            {
                return;
            }

            _count -= bucket.Events!.Count;
            bucket.Events.Clear();
            bucket.Index = bucketIndex;
        }

        bucket.Events!.Add(healthEvent);
        _count++;
    }

    private void RemoveExpired(long nowTimestamp)
    {
        var oldestTimestamp = SubtractTimestamp(nowTimestamp, _retentionPeriod);
        var oldestBucketIndex = GetBucketIndex(oldestTimestamp);
        foreach (ref var bucket in _buckets.AsSpan())
        {
            if (!bucket.IsOccupied)
            {
                continue;
            }

            if (bucket.Index < oldestBucketIndex)
            {
                Clear(ref bucket);
            }
            else if (bucket.Index == oldestBucketIndex)
            {
                var events = bucket.Events!;
                var writeIndex = 0;
                for (var readIndex = 0; readIndex < events.Count; readIndex++)
                {
                    var healthEvent = events[readIndex];
                    if (healthEvent.Timestamp >= oldestTimestamp)
                    {
                        events[writeIndex++] = healthEvent;
                    }
                }

                var removedCount = events.Count - writeIndex;
                if (removedCount > 0)
                {
                    events.RemoveRange(writeIndex, removedCount);
                    _count -= removedCount;
                }

                if (events.Count == 0)
                {
                    Clear(ref bucket);
                }
            }
        }
    }

    private void Clear(ref Bucket bucket)
    {
        _count -= bucket.Events!.Count;
        bucket.Events.Clear();
        bucket.IsOccupied = false;
        _occupiedBucketCount--;
    }

    private bool Overlaps(LocalSiloHealthEvent healthEvent, long startTimestamp, long endTimestamp)
    {
        if (healthEvent.Timestamp < startTimestamp)
        {
            return false;
        }

        return healthEvent.Timestamp <= endTimestamp
            || healthEvent.Duration is { } duration
                && duration > TimeSpan.Zero
                && _timeProvider.GetElapsedTime(endTimestamp, healthEvent.Timestamp) <= duration;
    }

    private static bool IsStateful(LocalSiloHealthCheckKind kind)
        => kind is LocalSiloHealthCheckKind.MembershipStatus
            or LocalSiloHealthCheckKind.HealthCheckParticipant
            or LocalSiloHealthCheckKind.ThreadPoolStall
            or LocalSiloHealthCheckKind.ProbeRequests
            or LocalSiloHealthCheckKind.ProbeResponses;

    private static bool IsBetter(
        LocalSiloHealthEvent candidate,
        LocalSiloHealthEvent current)
        => candidate.Score > current.Score
            || candidate.Score == current.Score && candidate.Timestamp > current.Timestamp;

    private long GetBucketIndex(long timestamp) => timestamp / _bucketTimestampLength;

    private int GetSlot(long bucketIndex)
    {
        var slot = bucketIndex % _buckets.Length;
        return (int)(slot >= 0 ? slot : slot + _buckets.Length);
    }

    private long GetTimestampLength(TimeSpan duration)
        => checked((long)Math.Ceiling(duration.TotalSeconds * _timeProvider.TimestampFrequency));

    private long SubtractTimestamp(long timestamp, TimeSpan duration)
        => timestamp - GetTimestampLength(duration);

    private readonly record struct HealthEventIdentity(
        LocalSiloHealthCheckKind Kind,
        string? Source);

    private struct AggregateEntry
    {
        public LocalSiloHealthEvent PriorState;
        public LocalSiloHealthEvent IntervalEvent;
        public bool HasPriorState;
        public bool HasIntervalEvent;
        public bool HasBoundaryState;
    }

    private struct Bucket
    {
        public long Index;
        public List<LocalSiloHealthEvent>? Events;
        public bool IsOccupied;
    }
}
