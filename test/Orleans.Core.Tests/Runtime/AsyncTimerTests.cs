using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Orleans.Runtime;
using Xunit;

namespace NonSilo.Tests.Runtime;

public class AsyncTimerTests
{
    [Theory, TestCategory("BVT")]
    [InlineData(0)]
    [InlineData(-2)]
    [InlineData(-1000)]
    public async Task NextTick_TreatsNonInfiniteNonPositivePeriodAsImmediate(int milliseconds)
    {
        var timeProvider = new FakeTimeProvider();
        using var timer = new AsyncTimer(TimeSpan.FromMilliseconds(milliseconds), "test", NullLogger.Instance, timeProvider);

        var task = timer.NextTick();

        Assert.True(task.IsCompletedSuccessfully);
        Assert.True(await task);
    }

    [Theory, TestCategory("BVT")]
    [InlineData(0)]
    [InlineData(-2)]
    [InlineData(-1000)]
    public async Task NextTick_TreatsNonInfiniteNonPositiveOverrideAsImmediate(int milliseconds)
    {
        var timeProvider = new FakeTimeProvider();
        using var timer = new AsyncTimer(TimeSpan.FromDays(1), "test", NullLogger.Instance, timeProvider);

        var task = timer.NextTick(TimeSpan.FromMilliseconds(milliseconds));

        Assert.True(task.IsCompletedSuccessfully);
        Assert.True(await task);
    }

    [Fact, TestCategory("BVT")]
    public async Task NextTick_SupportsInfinitePeriod()
    {
        var timeProvider = new FakeTimeProvider();
        using var timer = new AsyncTimer(Timeout.InfiniteTimeSpan, "test", NullLogger.Instance, timeProvider);

        Assert.True(await timer.NextTick(TimeSpan.Zero));
        var task = timer.NextTick();

        timeProvider.Advance(TimeSpan.FromDays(60));
        Assert.False(task.IsCompleted);
        timer.Dispose();
        Assert.False(await task);
    }

    [Fact, TestCategory("BVT")]
    public async Task NextTick_SupportsInfiniteOverride()
    {
        var timeProvider = new FakeTimeProvider();
        using var timer = new AsyncTimer(TimeSpan.FromDays(1), "test", NullLogger.Instance, timeProvider);

        var task = timer.NextTick(Timeout.InfiniteTimeSpan);

        timeProvider.Advance(TimeSpan.FromDays(60));
        Assert.False(task.IsCompleted);
        timer.Dispose();
        Assert.False(await task);
    }

    [Fact, TestCategory("BVT")]
    public async Task NextTick_SupportsPeriodsBeyondMaximumTimerDelay()
    {
        var timeProvider = new FakeTimeProvider();
        using var timer = new AsyncTimer(TimeSpan.FromDays(60), "test", NullLogger.Instance, timeProvider);

        var task = timer.NextTick();

        timeProvider.Advance(TimeSpan.FromDays(50));
        Assert.False(task.IsCompleted);
        timeProvider.Advance(TimeSpan.FromDays(10));
        Assert.True(await task);
    }

    [Fact, TestCategory("BVT")]
    public async Task NextTick_SupportsOverridesBeyondMaximumTimerDelay()
    {
        var timeProvider = new FakeTimeProvider();
        using var timer = new AsyncTimer(TimeSpan.FromDays(1), "test", NullLogger.Instance, timeProvider);

        var task = timer.NextTick(TimeSpan.FromDays(60));

        timeProvider.Advance(TimeSpan.FromDays(50));
        Assert.False(task.IsCompleted);
        timeProvider.Advance(TimeSpan.FromDays(10));
        Assert.True(await task);
    }

    [Fact, TestCategory("BVT")]
    public async Task NextTick_SupportsMaximumPeriod()
    {
        var timeProvider = new FakeTimeProvider();
        using var timer = new AsyncTimer(TimeSpan.MaxValue, "test", NullLogger.Instance, timeProvider);

        var task = timer.NextTick();

        Assert.False(task.IsCompleted);
        timer.Dispose();
        Assert.False(await task);
    }
}
