using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Orleans.Runtime;
using Xunit;

namespace NonSilo.Tests.Runtime;

public class AsyncTimerTests
{
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
