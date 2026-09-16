using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Runtime.Dissemination;
using Orleans.Serialization;
using Xunit;

namespace UnitTests.Dissemination;

public partial class DisseminationProtocolTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OlderFullMembershipValueReachesAuthoritativeOwner(bool antiEntropy)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var local = CreateSilo(41011);
        var peer = CreateSilo(41012);
        var current = CreateMembershipSnapshot(100, CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch));
        var notification = CreateMembershipSnapshot(2, current.Entries.Values.ToArray());
        var authority = CreateMembershipSnapshot(101, current.Entries.Values.Select(entry =>
        {
            var result = entry.Copy();
            result.HostName = "authoritative";
            return result;
        }).ToArray());
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer>();
        var manager = new FakeMembershipManager(current);
        var validations = 0;
        manager.ProcessGossipSnapshotHandler = (snapshot, token) =>
        {
            token.ThrowIfCancellationRequested();
            Assert.Equal(notification.Version, snapshot.Version);
            validations++;
            manager.CurrentSnapshot = authority;
            return Task.CompletedTask;
        };
        var ns = CreateMembershipNamespace(manager, serializer);
        var transport = new FakeTransport(local, peer);
        var protocol = CreateProtocol(transport, [ns]);
        var values = CreateValueGroups(ns.Name, CreateDisseminationValue(peer, new(
            DisseminationKey.Default, 0, 2,
            serializer.SerializeToArray(new MembershipTableSnapshotUpdate { Snapshot = notification }))));
        try
        {
            if (antiEntropy)
            {
                transport.ExchangeAntiEntropyHandler = (_, _, _) => ValueTask.FromResult(
                    new DisseminationAntiEntropyResponse { Sender = peer, Values = values });
                await protocol.RunAntiEntropyRound(cancellationToken);
            }
            else
            {
                var response = await protocol.ReceiveBroadcast(new() { Sender = peer, Values = values }, cancellationToken);
                Assert.Equal(101, Assert.Single(response.Acknowledgments[ns.Name]).Version);
            }

            Assert.Equal(1, validations);
            Assert.Same(authority, manager.CurrentSnapshot);
            Assert.Equal("authoritative", manager.CurrentSnapshot.Entries[local].HostName);
        }
        finally
        {
            ns.Options.Enabled = false;
            await protocol.StopAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task AntiEntropyConsultsMembershipNamespaceWhenPeerVersionIsAhead()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var local = CreateSilo(41013);
        var peer = CreateSilo(41014);
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer>();
        var ns = CreateMembershipNamespace(new FakeMembershipManager(CreateMembershipSnapshot(
            2, CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch))), serializer);
        var protocol = CreateProtocol(new FakeTransport(local, peer), [ns]);
        try
        {
            var response = await protocol.ReceiveAntiEntropy(new()
            {
                Sender = peer,
                SupportedNamespaces = [ns.Name],
                Digests = new() { [ns.Name] = [new(DisseminationKey.Default, 100)] },
            }, cancellationToken);

            var value = Assert.Single(GetAntiEntropyResponseValues(response)).Value;
            Assert.Equal(0, value.FromVersion);
            Assert.Equal(2, value.ToVersion);
            Assert.NotNull(Assert.IsType<MembershipTableSnapshotUpdate>(
                serializer.Deserialize<MembershipTableSnapshotUpdate>(value.Payload)).Snapshot);
        }
        finally
        {
            await protocol.StopAsync(cancellationToken);
        }
    }

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
