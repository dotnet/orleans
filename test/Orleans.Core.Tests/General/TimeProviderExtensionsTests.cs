using Microsoft.Extensions.Time.Testing;
using Orleans.Internal;
using Xunit;

namespace UnitTests.UtilsTests;

public class TimeProviderExtensionsTests
{
    private static readonly TimeSpan MaximumTimerDelay = TimeSpan.FromMilliseconds(0xfffffffe);

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Theory, TestCategory("BVT")]
    [MemberData(nameof(TimerDelayBoundaries))]
    public async Task DelayUntilAsync_CompletesAtRequestedTime(TimeSpan delay)
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(start);
        var delayTask = timeProvider.DelayUntilAsync(start.Add(delay));

        Assert.False(delayTask.IsCompleted);

        timeProvider.Advance(delay - TimeSpan.FromMilliseconds(1));
        await Task.Yield();
        Assert.False(delayTask.IsCompleted);

        timeProvider.Advance(TimeSpan.FromMilliseconds(1));

        await delayTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(start.Add(delay), timeProvider.GetUtcNow());
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public async Task DelayUntilAsync_CanBeCancelledBeyondMaximumTimerDelay()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(start);
        using var cancellation = new CancellationTokenSource();
        var delayTask = timeProvider.DelayUntilAsync(
            start.Add(MaximumTimerDelay).AddDays(1),
            cancellation.Token);

        Assert.False(delayTask.IsCompleted);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delayTask);
    }

    public static TheoryData<TimeSpan> TimerDelayBoundaries =>
    [
        MaximumTimerDelay - TimeSpan.FromMilliseconds(1),
        MaximumTimerDelay,
        MaximumTimerDelay + TimeSpan.FromMilliseconds(1),
    ];
}
