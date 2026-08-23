using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.MembershipService;

/// <summary>
/// Detects delays in .NET Thread Pool execution using a periodic timer callback.
/// </summary>
/// <remarks>
/// Timer callbacks execute on the Thread Pool. Each callback compares its execution time with the next expected
/// cadence boundary, producing non-overlapping stall intervals when callback dispatch is delayed.
/// </remarks>
internal sealed partial class ThreadPoolStallDetector : IDisposable
{
    internal static readonly TimeSpan DetectionPeriod = TimeSpan.FromMilliseconds(100);

    private static readonly TimerCallback TimerCallback = static state => ((ThreadPoolStallDetector)state!).OnTimer();
    private readonly ILogger<ThreadPoolStallDetector> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _retentionPeriod;
    private readonly long _detectionPeriodTimestampLength;
    private readonly int _minimumRetainedStallCount;
    private readonly ITimer _timer;
    private readonly List<StallInterval> _stalls = [];
    private readonly SortedDictionary<long, int> _pendingQueries = [];
#if NET9_0_OR_GREATER
    private readonly Lock _lock = new();
#else
    private readonly object _lock = new();
#endif
    private TaskCompletionSource? _nextSample;
    private long _lastSampleTimestamp;
    private long _nextExpectedTimestamp;
    private int _stallHead;
    private bool _disposed;

    public ThreadPoolStallDetector(
        ILogger<ThreadPoolStallDetector> logger,
        TimeProvider timeProvider,
        TimeSpan detectionPeriod,
        TimeSpan retentionPeriod)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(detectionPeriod, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retentionPeriod, TimeSpan.Zero);

        _logger = logger;
        _timeProvider = timeProvider;
        _retentionPeriod = retentionPeriod;
        _detectionPeriodTimestampLength = GetTimestampLength(detectionPeriod);
        _minimumRetainedStallCount = checked((int)Math.Ceiling(retentionPeriod / detectionPeriod));
        _lastSampleTimestamp = timeProvider.GetTimestamp();
        _nextExpectedTimestamp = _lastSampleTimestamp + _detectionPeriodTimestampLength;
        _timer = timeProvider.CreateTimer(TimerCallback, this, detectionPeriod, detectionPeriod);
    }

    public async ValueTask<TimeSpan> GetStallDurationAsync(
        long startTimestamp,
        long endTimestamp,
        CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ValidateInterval(startTimestamp, endTimestamp);
            AddPendingQuery(startTimestamp);
        }

        try
        {
            while (true)
            {
                Task nextSample;
                lock (_lock)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (_lastSampleTimestamp >= endTimestamp)
                    {
                        return GetStallDurationCore(startTimestamp, endTimestamp);
                    }

                    nextSample = (_nextSample ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
                }

                await nextSample.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_lock)
            {
                RemovePendingQuery(startTimestamp);
                RemoveExpired(_timeProvider.GetTimestamp());
            }
        }
    }

    public TimeSpan GetStallDuration(long startTimestamp, long endTimestamp)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ValidateInterval(startTimestamp, endTimestamp);
            return GetStallDurationCore(startTimestamp, endTimestamp);
        }
    }

    public TimeSpan GetMaximumStallDuration(long startTimestamp, long endTimestamp)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ValidateInterval(startTimestamp, endTimestamp);
            var maximumTimestampLength = 0L;
            for (var i = _stallHead; i < _stalls.Count; i++)
            {
                var stall = _stalls[i];
                if (stall.EndTimestamp <= startTimestamp)
                {
                    continue;
                }

                if (stall.StartTimestamp >= endTimestamp)
                {
                    break;
                }

                maximumTimestampLength = Math.Max(
                    maximumTimestampLength,
                    stall.EndTimestamp - stall.StartTimestamp);
            }

            return _timeProvider.GetElapsedTime(0, maximumTimestampLength);
        }
    }

    public void Dispose()
    {
        TaskCompletionSource? nextSample;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            nextSample = _nextSample;
        }

        _timer.Dispose();
        nextSample?.TrySetCanceled();
    }

    private void OnTimer()
    {
        try
        {
            Sample();
        }
        catch (Exception exception)
        {
            LogStallDetectorError(_logger, exception);
        }
    }

    private void Sample()
    {
        TaskCompletionSource? completedSample = null;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            var timestamp = _timeProvider.GetTimestamp();
            if (timestamp > _lastSampleTimestamp)
            {
                if (timestamp > _nextExpectedTimestamp)
                {
                    AddStall(new(_nextExpectedTimestamp, timestamp));
                }

                var elapsedPeriods = Math.Max(
                    1,
                    (timestamp - _nextExpectedTimestamp) / _detectionPeriodTimestampLength + 1);
                _nextExpectedTimestamp = checked(
                    _nextExpectedTimestamp + elapsedPeriods * _detectionPeriodTimestampLength);

                _lastSampleTimestamp = timestamp;
                RemoveExpired(timestamp);
                completedSample = _nextSample;
                _nextSample = null;
            }
        }

        completedSample?.TrySetResult();
    }

    private void AddStall(StallInterval stall)
    {
        if (_stalls.Count > _stallHead
            && _stalls[^1].EndTimestamp >= stall.StartTimestamp)
        {
            var prior = _stalls[^1];
            _stalls[^1] = prior with { EndTimestamp = Math.Max(prior.EndTimestamp, stall.EndTimestamp) };
        }
        else
        {
            _stalls.Add(stall);
        }
    }

    private TimeSpan GetStallDurationCore(long startTimestamp, long endTimestamp)
    {
        var durationTimestampLength = 0L;
        for (var i = _stallHead; i < _stalls.Count; i++)
        {
            var stall = _stalls[i];
            if (stall.EndTimestamp <= startTimestamp)
            {
                continue;
            }

            if (stall.StartTimestamp >= endTimestamp)
            {
                break;
            }

            var overlapStartTimestamp = Math.Max(startTimestamp, stall.StartTimestamp);
            var overlapEndTimestamp = Math.Min(endTimestamp, stall.EndTimestamp);
            durationTimestampLength = checked(
                durationTimestampLength + overlapEndTimestamp - overlapStartTimestamp);
        }

        return _timeProvider.GetElapsedTime(0, durationTimestampLength);
    }

    private void RemoveExpired(long nowTimestamp)
    {
        var oldestTimestamp = nowTimestamp - GetTimestampLength(_retentionPeriod);
        if (_pendingQueries.Count > 0)
        {
            using var enumerator = _pendingQueries.GetEnumerator();
            _ = enumerator.MoveNext();
            oldestTimestamp = Math.Min(oldestTimestamp, enumerator.Current.Key);
        }

        while (_stalls.Count - _stallHead > _minimumRetainedStallCount
            && _stalls[_stallHead].EndTimestamp <= oldestTimestamp)
        {
            _stallHead++;
        }

        if (_stallHead >= 1024 && _stallHead >= _stalls.Count / 2)
        {
            _stalls.RemoveRange(0, _stallHead);
            _stallHead = 0;
        }
    }

    private void AddPendingQuery(long startTimestamp)
    {
        if (_pendingQueries.TryGetValue(startTimestamp, out var count))
        {
            _pendingQueries[startTimestamp] = count + 1;
        }
        else
        {
            _pendingQueries.Add(startTimestamp, 1);
        }
    }

    private void RemovePendingQuery(long startTimestamp)
    {
        var count = _pendingQueries[startTimestamp];
        if (count == 1)
        {
            _pendingQueries.Remove(startTimestamp);
        }
        else
        {
            _pendingQueries[startTimestamp] = count - 1;
        }
    }

    private void ValidateInterval(long startTimestamp, long endTimestamp)
    {
        if (endTimestamp < startTimestamp)
        {
            throw new ArgumentOutOfRangeException(
                nameof(endTimestamp),
                endTimestamp,
                "The interval end must not precede its start.");
        }
    }

    private long GetTimestampLength(TimeSpan duration)
        => checked((long)Math.Ceiling(duration.TotalSeconds * _timeProvider.TimestampFrequency));

    private readonly record struct StallInterval(long StartTimestamp, long EndTimestamp);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Exception monitoring .NET Thread Pool stalls"
    )]
    private static partial void LogStallDetectorError(ILogger logger, Exception exception);
}
