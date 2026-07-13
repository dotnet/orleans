using Microsoft.Extensions.Time.Testing;
using Orleans.Internal;
using Xunit;

namespace NonSilo.Tests.Utils;

public class TimeProviderExtensionsTests
{
    [Fact, TestCategory("BVT")]
    public async Task DelayUntilAsync_SupportsDelaysBeyondMaximumTimerDelay()
    {
        var timeProvider = new FakeTimeProvider();
        var delay = TimeSpan.FromDays(60);

        var task = timeProvider.DelayUntilAsync(timeProvider.GetUtcNow() + delay);

        Assert.False(task.IsCompleted);
        timeProvider.Advance(delay);
        await task;
    }
}
