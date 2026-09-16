using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Runtime.Dissemination;
using Orleans.Serialization;
using Xunit;

namespace UnitTests.Dissemination;

public partial class DisseminationProtocolTests
{
    [Fact]
    public void MembershipRepairSendsFullStateWhenPeerVersionIsAhead()
    {
        var local = CreateSilo(41001);
        var snapshot = CreateMembershipSnapshot(2, CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch));
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer>();
        var ns = CreateMembershipNamespace(new FakeMembershipManager(snapshot), serializer);

        var repair = ns.CreateRepair(new(
            DisseminationKey.Default, 100, null, 1, 1024 * 1024, 1024 * 1024));

        Assert.Equal(DisseminationRepairStatus.Produced, repair.Status);
        Assert.Equal(2, repair.Version);
        var value = Assert.Single(repair.Values);
        Assert.Equal(0, value.FromVersion);
        Assert.Equal(2, value.ToVersion);
        var update = Assert.IsType<MembershipTableSnapshotUpdate>(
            serializer.Deserialize<MembershipTableSnapshotUpdate>(value.Payload));
        Assert.Null(update.Diff);
        var repaired = Assert.IsType<MembershipTableSnapshot>(update.Snapshot);
        Assert.Equal(snapshot.Version, repaired.Version);
        Assert.Equal(local, Assert.Single(repaired.Entries).Key);
    }

    [Fact]
    public async Task MembershipHistoryResetDiscardsPriorIncarnationAndDelayedPublication()
    {
        var members = CreateSilos(20);
        var old = CreateMembershipSnapshot(1, members.Select(
            member => CreateMembershipEntry(member, SiloStatus.Active, DateTime.UnixEpoch)).ToArray());
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer>();
        var manager = new FakeMembershipManager(old);
        var ns = CreateMembershipNamespace(manager, serializer);
        Assert.Equal(1, Assert.Single(ns.Digests).Version);
        manager.CurrentSnapshot = CreateMembershipSnapshot(100, old.Entries.Values.ToArray());
        Assert.Equal(100, Assert.Single(ns.Digests).Version);
        var reset = CreateMembershipSnapshot(2, old.Entries.Values.Select(entry =>
        {
            var result = entry.Copy();
            result.HostName = "new-table";
            return result;
        }).ToArray());
        manager.CurrentSnapshot = reset;
        Assert.Equal(2, Assert.Single(ns.Digests).Version);

        Assert.False(await ns.PublishAsync(new FakeDisseminationService(), old, TestContext.Current.CancellationToken));
        var obsoleteBaseline = ns.CreateRepair(new(
            DisseminationKey.Default, null, 1, 1, 1024 * 1024, 1024 * 1024));
        Assert.Equal(DisseminationRepairStatus.Unavailable, obsoleteBaseline.Status);
        var repair = ns.CreateRepair(new(
            DisseminationKey.Default, 1, null, 1, 1024 * 1024, 1024 * 1024));
        Assert.Equal(DisseminationRepairStatus.Produced, repair.Status);
        var value = Assert.Single(repair.Values);
        Assert.Equal(0, value.FromVersion);
        Assert.Equal(2, value.ToVersion);
        var payload = Assert.IsType<MembershipTableSnapshotUpdate>(
            serializer.Deserialize<MembershipTableSnapshotUpdate>(value.Payload));
        Assert.Null(payload.Diff);
        var repaired = Assert.IsType<MembershipTableSnapshot>(payload.Snapshot);
        Assert.Equal(members.Length, repaired.Entries.Count);
        Assert.All(repaired.Entries.Values, entry => Assert.Equal("new-table", entry.HostName));
    }
}
