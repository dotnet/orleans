using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
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
    private readonly List<LocalSiloHealthEvent> _pauseEvents = [];
    private readonly SortedDictionary<long, int> _activePauseIntervals = [];
    private readonly Dictionary<LocalSiloHealthCheckKind, List<PauseInterval>> _pauseIntervalsByKind = [];
    private readonly List<PauseInterval> _allPauseIntervals = [];
    private readonly List<LocalSiloPauseDuration> _pauseDurations = [];
    private int _pauseEventHead;
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

    public void BeginPauseCollection(long startTimestamp)
    {
        if (_activePauseIntervals.TryGetValue(startTimestamp, out var count))
        {
            _activePauseIntervals[startTimestamp] = count + 1;
        }
        else
        {
            _activePauseIntervals.Add(startTimestamp, 1);
        }
    }

    public void EndPauseCollection(long startTimestamp, long nowTimestamp)
    {
        if (!_activePauseIntervals.TryGetValue(startTimestamp, out var count))
        {
            throw new InvalidOperationException($"Pause collection interval {startTimestamp} is not active.");
        }

        if (count == 1)
        {
            _activePauseIntervals.Remove(startTimestamp);
        }
        else
        {
            _activePauseIntervals[startTimestamp] = count - 1;
        }

        RemoveExpiredPauseEvents(nowTimestamp, force: true);
    }

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

    public LocalSiloPauseStatus AggregatePauses(
        long startTimestamp,
        long endTimestamp,
        long nowTimestamp,
        LocalSiloHealthCheckCategory categories)
    {
        RemoveExpired(nowTimestamp);
        if (categories == LocalSiloHealthCheckCategory.None)
        {
            return new(TimeSpan.Zero, []);
        }

        try
        {
            for (var i = _pauseEventHead; i < _pauseEvents.Count; i++)
            {
                var healthEvent = _pauseEvents[i];
                if ((healthEvent.Category & categories) == 0
                    || !TryGetPauseInterval(healthEvent, startTimestamp, endTimestamp, out var interval))
                {
                    continue;
                }

                if (!_pauseIntervalsByKind.TryGetValue(healthEvent.Kind, out var intervals))
                {
                    intervals = [];
                    _pauseIntervalsByKind.Add(healthEvent.Kind, intervals);
                }

                intervals.Add(interval);
                _allPauseIntervals.Add(interval);
            }

            foreach (var (kind, intervals) in _pauseIntervalsByKind)
            {
                _pauseDurations.Add(new(kind, GetUnionDuration(intervals)));
            }

            _pauseDurations.Sort(static (left, right) => left.Kind.CompareTo(right.Kind));
            return new(
                GetUnionDuration(_allPauseIntervals),
                ImmutableArray.CreateRange(_pauseDurations));
        }
        finally
        {
            _pauseIntervalsByKind.Clear();
            _allPauseIntervals.Clear();
            _pauseDurations.Clear();
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
        if (IsPause(healthEvent))
        {
            _pauseEvents.Add(healthEvent);
        }
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

        RemoveExpiredPauseEvents(nowTimestamp, force: false);
    }

    private void RemoveExpiredPauseEvents(long nowTimestamp, bool force)
    {
        var oldestTimestamp = SubtractTimestamp(nowTimestamp, _retentionPeriod);
        if (_activePauseIntervals.Count > 0)
        {
            oldestTimestamp = Math.Min(oldestTimestamp, _activePauseIntervals.First().Key);
        }

        while (_pauseEventHead < _pauseEvents.Count
            && _pauseEvents[_pauseEventHead].Timestamp < oldestTimestamp)
        {
            _pauseEventHead++;
        }

        if (_pauseEventHead > 0
            && (force || _pauseEventHead >= 1024 && _pauseEventHead >= _pauseEvents.Count / 2))
        {
            _pauseEvents.RemoveRange(0, _pauseEventHead);
            _pauseEventHead = 0;
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

    private bool TryGetPauseInterval(
        LocalSiloHealthEvent healthEvent,
        long startTimestamp,
        long endTimestamp,
        out PauseInterval interval)
    {
        if (healthEvent.Duration is not { } duration || duration <= TimeSpan.Zero)
        {
            interval = default;
            return false;
        }

        var pauseStartTimestamp = SubtractTimestamp(healthEvent.Timestamp, duration);
        var overlapStartTimestamp = Math.Max(startTimestamp, pauseStartTimestamp);
        var overlapEndTimestamp = Math.Min(endTimestamp, healthEvent.Timestamp);
        if (overlapEndTimestamp <= overlapStartTimestamp)
        {
            interval = default;
            return false;
        }

        interval = new(overlapStartTimestamp, overlapEndTimestamp);
        return true;
    }

    private TimeSpan GetUnionDuration(List<PauseInterval> intervals)
    {
        if (intervals.Count == 0)
        {
            return TimeSpan.Zero;
        }

        intervals.Sort(static (left, right) =>
        {
            var startComparison = left.StartTimestamp.CompareTo(right.StartTimestamp);
            return startComparison != 0
                ? startComparison
                : left.EndTimestamp.CompareTo(right.EndTimestamp);
        });

        var totalTimestampLength = 0L;
        var currentStartTimestamp = intervals[0].StartTimestamp;
        var currentEndTimestamp = intervals[0].EndTimestamp;
        for (var i = 1; i < intervals.Count; i++)
        {
            var interval = intervals[i];
            if (interval.StartTimestamp <= currentEndTimestamp)
            {
                currentEndTimestamp = Math.Max(currentEndTimestamp, interval.EndTimestamp);
            }
            else
            {
                totalTimestampLength = checked(totalTimestampLength + currentEndTimestamp - currentStartTimestamp);
                currentStartTimestamp = interval.StartTimestamp;
                currentEndTimestamp = interval.EndTimestamp;
            }
        }

        totalTimestampLength = checked(totalTimestampLength + currentEndTimestamp - currentStartTimestamp);
        return _timeProvider.GetElapsedTime(0, totalTimestampLength);
    }

    private static bool IsStateful(LocalSiloHealthCheckKind kind)
        => kind is LocalSiloHealthCheckKind.MembershipStatus
            or LocalSiloHealthCheckKind.HealthCheckParticipant
            or LocalSiloHealthCheckKind.ThreadPoolQueueDelay
            or LocalSiloHealthCheckKind.ProbeRequests
            or LocalSiloHealthCheckKind.ProbeResponses;

    private static bool IsPause(LocalSiloHealthEvent healthEvent)
        => healthEvent.Duration > TimeSpan.Zero
            && healthEvent.Kind is LocalSiloHealthCheckKind.GarbageCollectionPause
                or LocalSiloHealthCheckKind.RuntimeStall
                or LocalSiloHealthCheckKind.ComponentHealthCheckStall;

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

    private readonly record struct PauseInterval(long StartTimestamp, long EndTimestamp);

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
