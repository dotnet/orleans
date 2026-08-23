using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Orleans.Runtime.MembershipService;
using TestExtensions;
using Xunit;

namespace NonSilo.Tests.Membership;

[TestCategory("BVT"), TestCategory("Membership")]
public sealed class ThreadPoolStallDetectorTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan DetectionPeriod = TimeSpan.FromMilliseconds(100);

    [Fact]
    public async Task TimerCallback_DetectsDelayBeginningImmediatelyAfterPriorCycle()
    {
        var timeProvider = new ManualTimerTimeProvider(Start);
        using var detector = CreateDetector(timeProvider);
        var startTimestamp = timeProvider.GetTimestamp();
        timeProvider.Advance(DetectionPeriod);
        timeProvider.FireTimer();

        timeProvider.Advance(TimeSpan.FromMilliseconds(500));
        timeProvider.FireTimer();
        var endTimestamp = timeProvider.GetTimestamp();

        var duration = await detector.GetStallDurationAsync(
            startTimestamp,
            endTimestamp,
            CancellationToken.None);

        Assert.Equal(TimeSpan.FromMilliseconds(400), duration);
    }

    [Fact]
    public async Task LongStallExceedingHistoryDuration_IsDetected()
    {
        var timeProvider = new ManualTimerTimeProvider(Start);
        using var detector = CreateDetector(timeProvider, TimeSpan.FromMilliseconds(500));
        var startTimestamp = timeProvider.GetTimestamp();

        timeProvider.Advance(TimeSpan.FromSeconds(2));
        timeProvider.FireTimer();
        var endTimestamp = timeProvider.GetTimestamp();

        var duration = await detector.GetStallDurationAsync(
            startTimestamp,
            endTimestamp,
            CancellationToken.None);

        Assert.Equal(TimeSpan.FromMilliseconds(1900), duration);
    }

    [Fact]
    public async Task GetStallDurationAsync_WaitsForSampleAfterIntervalEnd()
    {
        var timeProvider = new ManualTimerTimeProvider(Start);
        using var detector = CreateDetector(timeProvider);
        timeProvider.Advance(DetectionPeriod);
        timeProvider.FireTimer();
        var startTimestamp = timeProvider.GetTimestamp();
        timeProvider.Advance(TimeSpan.FromMilliseconds(50));
        var endTimestamp = timeProvider.GetTimestamp();

        var durationTask = detector.GetStallDurationAsync(
            startTimestamp,
            endTimestamp,
            CancellationToken.None).AsTask();

        Assert.False(durationTask.IsCompleted);

        timeProvider.Advance(TimeSpan.FromMilliseconds(50));
        timeProvider.FireTimer();

        Assert.Equal(TimeSpan.Zero, await durationTask);
    }

    [Fact]
    public async Task GetStallDurationAsync_ExcludesDelayAfterIntervalEnd()
    {
        var timeProvider = new ManualTimerTimeProvider(Start);
        using var detector = CreateDetector(timeProvider);
        timeProvider.Advance(DetectionPeriod);
        timeProvider.FireTimer();
        var startTimestamp = timeProvider.GetTimestamp();
        timeProvider.Advance(TimeSpan.FromMilliseconds(50));
        var endTimestamp = timeProvider.GetTimestamp();
        var durationTask = detector.GetStallDurationAsync(
            startTimestamp,
            endTimestamp,
            CancellationToken.None).AsTask();

        timeProvider.Advance(TimeSpan.FromMilliseconds(100));
        timeProvider.FireTimer();

        Assert.Equal(TimeSpan.Zero, await durationTask);
    }

    [Fact]
    public async Task GetStallDurationAsync_AggregatesMultipleDelayedCycles()
    {
        var timeProvider = new ManualTimerTimeProvider(Start);
        using var detector = CreateDetector(timeProvider);
        var startTimestamp = timeProvider.GetTimestamp();
        timeProvider.Advance(TimeSpan.FromMilliseconds(300));
        timeProvider.FireTimer();
        timeProvider.Advance(TimeSpan.FromMilliseconds(250));
        timeProvider.FireTimer();
        var endTimestamp = timeProvider.GetTimestamp();

        var duration = await detector.GetStallDurationAsync(
            startTimestamp,
            endTimestamp,
            CancellationToken.None);

        Assert.Equal(TimeSpan.FromMilliseconds(350), duration);
    }

    [Fact]
    public async Task TimerCallback_AggregatesRepeatedLatenessAgainstFixedCadence()
    {
        var timeProvider = new ManualTimerTimeProvider(Start);
        using var detector = CreateDetector(timeProvider);
        var startTimestamp = timeProvider.GetTimestamp();
        timeProvider.Advance(TimeSpan.FromMilliseconds(150));
        timeProvider.FireTimer();
        timeProvider.Advance(DetectionPeriod);
        timeProvider.FireTimer();
        timeProvider.Advance(DetectionPeriod);
        timeProvider.FireTimer();
        var endTimestamp = timeProvider.GetTimestamp();

        var duration = await detector.GetStallDurationAsync(
            startTimestamp,
            endTimestamp,
            CancellationToken.None);

        Assert.Equal(TimeSpan.FromMilliseconds(150), duration);
    }

    [Fact]
    public async Task TimerCallback_EarlyExecutionAdvancesExpectedCadence()
    {
        var timeProvider = new ManualTimerTimeProvider(Start);
        using var detector = CreateDetector(timeProvider);
        var startTimestamp = timeProvider.GetTimestamp();
        timeProvider.Advance(TimeSpan.FromMilliseconds(99));
        timeProvider.FireTimer();
        timeProvider.Advance(DetectionPeriod);
        timeProvider.FireTimer();
        var endTimestamp = timeProvider.GetTimestamp();

        var duration = await detector.GetStallDurationAsync(
            startTimestamp,
            endTimestamp,
            CancellationToken.None);

        Assert.Equal(TimeSpan.Zero, duration);
    }

    [Fact]
    public void GetMaximumStallDuration_ReturnsFullOverlappingStall()
    {
        var timeProvider = new ManualTimerTimeProvider(Start);
        using var detector = CreateDetector(timeProvider);
        var initialTimestamp = timeProvider.GetTimestamp();
        timeProvider.Advance(TimeSpan.FromMilliseconds(600));
        timeProvider.FireTimer();

        var duration = detector.GetMaximumStallDuration(
            TimestampAt(timeProvider, initialTimestamp, TimeSpan.FromMilliseconds(500)),
            TimestampAt(timeProvider, initialTimestamp, TimeSpan.FromMilliseconds(600)));

        Assert.Equal(TimeSpan.FromMilliseconds(500), duration);
    }

    [Fact]
    public async Task PendingQuery_RetainsStallsPastConfiguredHistory()
    {
        var timeProvider = new ManualTimerTimeProvider(Start);
        using var detector = CreateDetector(timeProvider, TimeSpan.FromMilliseconds(500));
        var startTimestamp = timeProvider.GetTimestamp();
        for (var i = 0; i < 5; i++)
        {
            timeProvider.Advance(i == 0 ? TimeSpan.FromMilliseconds(150) : DetectionPeriod);
            timeProvider.FireTimer();
        }

        timeProvider.Advance(TimeSpan.FromMilliseconds(50));
        var endTimestamp = timeProvider.GetTimestamp();
        var durationTask = detector.GetStallDurationAsync(
            startTimestamp,
            endTimestamp,
            CancellationToken.None).AsTask();

        Assert.False(durationTask.IsCompleted);

        timeProvider.Advance(TimeSpan.FromMilliseconds(50));
        timeProvider.FireTimer();

        Assert.Equal(TimeSpan.FromMilliseconds(250), await durationTask);
        Assert.Equal(
            TimeSpan.Zero,
            detector.GetStallDuration(
                TimestampAt(timeProvider, startTimestamp, TimeSpan.FromMilliseconds(100)),
                TimestampAt(timeProvider, startTimestamp, TimeSpan.FromMilliseconds(150))));
    }

    [Fact]
    public void HistoryRetention_PreservesMinimumCycleCountAcrossLongStall()
    {
        var timeProvider = new ManualTimerTimeProvider(Start);
        using var detector = CreateDetector(timeProvider, TimeSpan.FromMilliseconds(500));
        var initialTimestamp = timeProvider.GetTimestamp();
        for (var i = 0; i < 6; i++)
        {
            timeProvider.Advance(i == 0 ? TimeSpan.FromMilliseconds(150) : DetectionPeriod);
            timeProvider.FireTimer();
        }

        timeProvider.Advance(TimeSpan.FromMilliseconds(1350));
        timeProvider.FireTimer();

        Assert.Equal(
            TimeSpan.Zero,
            detector.GetStallDuration(
                TimestampAt(timeProvider, initialTimestamp, TimeSpan.FromMilliseconds(200)),
                TimestampAt(timeProvider, initialTimestamp, TimeSpan.FromMilliseconds(250))));
        Assert.Equal(
            TimeSpan.FromMilliseconds(50),
            detector.GetStallDuration(
                TimestampAt(timeProvider, initialTimestamp, TimeSpan.FromMilliseconds(300)),
                TimestampAt(timeProvider, initialTimestamp, TimeSpan.FromMilliseconds(350))));
    }

    private static ThreadPoolStallDetector CreateDetector(
        TimeProvider timeProvider,
        TimeSpan? retentionPeriod = null)
        => new(
            NullLogger<ThreadPoolStallDetector>.Instance,
            timeProvider,
            DetectionPeriod,
            retentionPeriod ?? TimeSpan.FromMinutes(1));

    private static long TimestampAt(TimeProvider timeProvider, long initialTimestamp, TimeSpan elapsed)
        => initialTimestamp + (long)(elapsed.TotalSeconds * timeProvider.TimestampFrequency);

    private sealed class ManualTimerTimeProvider(DateTimeOffset start) : FakeTimeProvider(start)
    {
        private TimerCallback? _callback;
        private object? _state;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            Assert.Null(_callback);
            Assert.Equal(DetectionPeriod, dueTime);
            Assert.Equal(DetectionPeriod, period);
            _callback = callback;
            _state = state;
            return new ManualTimer();
        }

        public void FireTimer()
        {
            Assert.NotNull(_callback);
            _callback(_state);
        }

        private sealed class ManualTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => default;
        }
    }
}
