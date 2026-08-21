using Orleans.Runtime;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Support;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
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

        Assert.Equal(0, probe.WaiterListCount);

        probe.Publish(
            grainId,
            CreateSnapshot(inboxCount: 0));

        Assert.Equal(0, predicateCalls);
    }

    [Fact]
    public async Task WaitAsync_SuccessRemovesWaiterList()
    {
        var probe = new SnapshotProbe();
        var grainId = GrainId.Create("snapshot-probe", "success");
        var wait = probe.WaitAsync(grainId, static snapshot => snapshot.InboxCount == 1);
        var snapshot = CreateSnapshot(inboxCount: 1);

        probe.Publish(grainId, snapshot);

        Assert.Same(snapshot, await wait);
        Assert.Equal(0, probe.WaiterListCount);
    }

    [Fact]
    public async Task WaitAsync_TimeoutRetainsListWithActiveWaiter()
    {
        var probe = new SnapshotProbe();
        var grainId = GrainId.Create("snapshot-probe", "shared-list");
        var activeWait = probe.WaitAsync(grainId, static snapshot => snapshot.InboxCount == 1);

        await Assert.ThrowsAsync<TimeoutException>(
            () => probe.WaitAsync(grainId, static _ => false, TimeSpan.Zero));

        Assert.Equal(1, probe.WaiterListCount);

        probe.Publish(
            grainId,
            CreateSnapshot(inboxCount: 1));

        await activeWait;
        Assert.Equal(0, probe.WaiterListCount);
    }

    private static DurableEndpointSnapshot CreateSnapshot(int inboxCount) =>
        new(
            Guid.Empty,
            string.Empty,
            inboxCount,
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
            0);
}
