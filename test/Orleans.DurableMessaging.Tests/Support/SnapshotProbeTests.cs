using Orleans.Runtime;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Support;

public class SnapshotProbeTests
{
    [Fact]
    public async Task WaitAsync_TimeoutRemovesWaiter()
    {
        var probe = new SnapshotProbe();
        var grainId = GrainId.Create("snapshot-probe", "timeout");
        var predicateCalls = 0;

        await Assert.ThrowsAsync<TimeoutException>(
            () => probe.WaitAsync(
                grainId,
                _ =>
                {
                    predicateCalls++;
                    return false;
                },
                TimeSpan.Zero));

        probe.Publish(
            grainId,
            new DurableEndpointSnapshot(
                Guid.Empty,
                string.Empty,
                0,
                0,
                0,
                [],
                [],
                [],
                null,
                0,
                0,
                0,
                null,
                0,
                0));

        Assert.Equal(0, predicateCalls);
    }
}
