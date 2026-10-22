using Orleans.Runtime;
using Xunit;
using static Orleans.Clustering.TestKit.MembershipTableTestData;

namespace Orleans.Clustering.TestKit.Tests;

public sealed class MembershipTableTestDataTests
{
    [Fact]
    public void CreateInitialEntry_UsesIndependentEmptySuspicionHistories()
    {
        var first = CreateInitialEntry(1);
        var second = CreateInitialEntry(1);
        Assert.Empty(first.SuspectTimes!);
        Assert.Empty(second.SuspectTimes!);
        Assert.NotSame(first.SuspectTimes, second.SuspectTimes);
        first.AddSuspector(CreateEntry(2).SiloAddress, T1);
        Assert.Empty(second.SuspectTimes!);
    }

    [Fact]
    public void Forward_ChangesStatusAndVotesWhilePreservingActivationMetadata()
    {
        var initial = CreateInitialEntry(1);
        var updated = Forward(initial);
        Assert.Equal(SiloStatus.Joining, updated.Status);
        Assert.Single(updated.SuspectTimes!);
        Assert.Empty(initial.SuspectTimes!);
        Assert.Equal(initial.SiloAddress, updated.SiloAddress);
        Assert.Equal(initial.HostName, updated.HostName);
        Assert.Equal(initial.SiloName, updated.SiloName);
        Assert.Equal(initial.ProxyPort, updated.ProxyPort);
        Assert.Equal(initial.StartTime, updated.StartTime);
        Assert.Equal(initial.RoleName, updated.RoleName);
        Assert.Equal(initial.UpdateZone, updated.UpdateZone);
        Assert.Equal(initial.FaultZone, updated.FaultZone);
    }

    [Fact]
    public void CreateEntry_SameSeedProducesDetachedWholeSecondUtcData()
    {
        var first = CreateEntry(4, 19);
        var second = CreateEntry(4, 19);
        Assert.NotNull(first.SuspectTimes);
        Assert.NotNull(second.SuspectTimes);
        Assert.NotSame(first.SuspectTimes, second.SuspectTimes);
        Assert.Equal(119, first.SiloAddress.Generation);
        Assert.Equal(12004, first.SiloAddress.Endpoint.Port);
        Assert.Equal("host-4", first.HostName);
        Assert.Equal(string.Empty, first.RoleName);
        Assert.Equal(0, first.FaultZone);
        Assert.Equal(0, first.UpdateZone);
        foreach (var time in first.SuspectTimes.Select(v => v.Item2).Append(first.IAmAliveTime).Append(first.StartTime))
        {
            Assert.Equal(DateTimeKind.Utc, time.Kind);
            Assert.Equal(0, time.Ticks % TimeSpan.TicksPerSecond);
        }
        first.SuspectTimes.Clear();
        Assert.Equal(2, second.SuspectTimes.Count);
    }

    [Fact]
    public void CreateSuccessor_KeepsEndpointAndStrictlyIncreasesGeneration()
    {
        var dead = CreateEntry(4, 19, SiloStatus.Dead);
        var successor = CreateSuccessor(dead);
        Assert.Equal(dead.SiloAddress.Endpoint, successor.SiloAddress.Endpoint);
        Assert.Equal(120, successor.SiloAddress.Generation);
        Assert.Equal(119, dead.SiloAddress.Generation);
        Assert.Equal(SiloStatus.Dead, dead.Status);
        Assert.Equal(SiloStatus.Created, successor.Status);
        Assert.Empty(successor.SuspectTimes!);
        Assert.Equal(T2, successor.StartTime);
        Assert.Equal(T2, successor.IAmAliveTime);
    }

    [Fact]
    public void CreateCleanupEntries_ContainsEveryLifecycleStatusAndEffectiveTimeBoundary()
    {
        var entries = CreateCleanupEntries();
        Assert.Equal(10, entries.Count);
        Assert.Equal(Enum.GetValues<SiloStatus>().Where(status => status != SiloStatus.None), entries.Take(6).Select(e => e.Status));
        var removable = Assert.Single(entries, e => e.Status == SiloStatus.Dead && MembershipEntrySnapshot.Capture(e).GetEffectiveUpdateTime() < T1);
        Assert.Equal(12106, removable.SiloAddress.Endpoint.Port);
        Assert.Equal(new[] { T2, T2, T2, T1 }, entries.Skip(6).Select(e => MembershipEntrySnapshot.Capture(e).GetEffectiveUpdateTime()));
        Assert.Equal(5, entries.Count(e => e.Status != SiloStatus.Dead));
    }
}
