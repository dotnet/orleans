using Microsoft.Extensions.Time.Testing;
using Orleans.Internal;
using Xunit;

namespace NonSilo.Tests.Utils;

public class TimeProviderExtensionsTests
{
    [Fact, TestCategory("BVT")]
    public async Task DelayAsync_CompletesImmediatelyForZeroDelay()
    {
        var timeProvider = new FakeTimeProvider();

        var task = timeProvider.DelayAsync(TimeSpan.Zero);

        Assert.True(task.IsCompletedSuccessfully);
        await task;
    }

    [Fact, TestCategory("BVT")]
    public async Task DelayAsync_SupportsInfiniteDelay()
    {
        var timeProvider = new FakeTimeProvider();
        using var cancellation = new CancellationTokenSource();

        var task = timeProvider.DelayAsync(Timeout.InfiniteTimeSpan, cancellation.Token);

        timeProvider.Advance(TimeSpan.FromDays(60));
        Assert.False(task.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Theory, TestCategory("BVT")]
    [InlineData(-2)]
    [InlineData(-1000)]
    public async Task DelayAsync_RejectsInvalidNegativeDelays(int milliseconds)
    {
        var timeProvider = new FakeTimeProvider();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => timeProvider.DelayAsync(TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Fact, TestCategory("BVT")]
    public async Task DelayAsync_SupportsDelaysBeyondMaximumTimerDelay()
    {
        var timeProvider = new FakeTimeProvider();
        var delay = TimeSpan.FromDays(60);

        var task = timeProvider.DelayAsync(delay);

        Assert.False(task.IsCompleted);
        timeProvider.Advance(TimeSpan.FromDays(50));
        Assert.False(task.IsCompleted);
        timeProvider.Advance(TimeSpan.FromDays(10));
        await task;
    }

    [Theory, TestCategory("BVT")]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-1000)]
    public async Task DelayUntilAsync_CompletesImmediatelyForCurrentOrPastDueTime(int milliseconds)
    {
        var timeProvider = new FakeTimeProvider();
        var dueTime = timeProvider.GetUtcNow() + TimeSpan.FromMilliseconds(milliseconds);

        var task = timeProvider.DelayUntilAsync(dueTime);

        Assert.True(task.IsCompletedSuccessfully);
        await task;
    }

    [Fact, TestCategory("BVT")]
    public async Task DelayUntilAsync_SupportsDelaysBeyondMaximumTimerDelay()
    {
        var timeProvider = new FakeTimeProvider();
        var delay = TimeSpan.FromDays(60);
        var task = timeProvider.DelayUntilAsync(timeProvider.GetUtcNow() + delay);

        Assert.False(task.IsCompleted);
        timeProvider.Advance(TimeSpan.FromDays(50));
        Assert.False(task.IsCompleted);
        timeProvider.Advance(TimeSpan.FromDays(10));
        await task;
    }
}
