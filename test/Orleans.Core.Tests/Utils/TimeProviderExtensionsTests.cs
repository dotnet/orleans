using Microsoft.Extensions.Time.Testing;
using Orleans.Internal;
using Xunit;

namespace NonSilo.Tests.Utils;

public class TimeProviderExtensionsTests
{
    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public async Task DelayAsync_CompletesImmediatelyForZeroDelay()
    {
        var timeProvider = new FakeTimeProvider();

        var task = timeProvider.DelayAsync(TimeSpan.Zero, TestContext.Current.CancellationToken);

        Assert.True(task.IsCompletedSuccessfully);
        await task;
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public async Task DelayAsync_SupportsInfiniteDelay()
    {
        var timeProvider = new FakeTimeProvider();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var task = timeProvider.DelayAsync(Timeout.InfiniteTimeSpan, cancellation.Token);

        timeProvider.Advance(TimeSpan.FromDays(60));
        Assert.False(task.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Theory, TestCategory("BVT")]
    [InlineData(-2)]
    [InlineData(-1000)]
    public async Task DelayAsync_RejectsInvalidNegativeDelays(int milliseconds)
    {
        var timeProvider = new FakeTimeProvider();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => timeProvider.DelayAsync(
                TimeSpan.FromMilliseconds(milliseconds),
                TestContext.Current.CancellationToken));
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public async Task DelayAsync_SupportsDelaysBeyondMaximumTimerDelay()
    {
        var timeProvider = new FakeTimeProvider();
        var delay = TimeSpan.FromDays(60);

        var task = timeProvider.DelayAsync(delay, TestContext.Current.CancellationToken);

        Assert.False(task.IsCompleted);
        timeProvider.Advance(TimeSpan.FromDays(50));
        Assert.False(task.IsCompleted);
        timeProvider.Advance(TimeSpan.FromDays(10));
        await task;
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Theory, TestCategory("BVT")]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-1000)]
    public async Task DelayUntilAsync_CompletesImmediatelyForCurrentOrPastDueTime(int milliseconds)
    {
        var timeProvider = new FakeTimeProvider();
        var dueTime = timeProvider.GetUtcNow() + TimeSpan.FromMilliseconds(milliseconds);

        var task = timeProvider.DelayUntilAsync(dueTime, TestContext.Current.CancellationToken);

        Assert.True(task.IsCompletedSuccessfully);
        await task;
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public async Task DelayUntilAsync_SupportsDelaysBeyondMaximumTimerDelay()
    {
        var timeProvider = new FakeTimeProvider();
        var delay = TimeSpan.FromDays(60);
        var task = timeProvider.DelayUntilAsync(
            timeProvider.GetUtcNow() + delay,
            TestContext.Current.CancellationToken);

        Assert.False(task.IsCompleted);
        timeProvider.Advance(TimeSpan.FromDays(50));
        Assert.False(task.IsCompleted);
        timeProvider.Advance(TimeSpan.FromDays(10));
        await task;
    }
}
