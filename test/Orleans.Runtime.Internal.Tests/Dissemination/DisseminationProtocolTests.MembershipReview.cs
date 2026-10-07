using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans;
using Orleans.Configuration;
using Orleans.Core.Diagnostics;
using Orleans.Runtime;
using Orleans.Runtime.Dissemination;
using Orleans.Runtime.MembershipService;
using Orleans.Serialization;
using Xunit;

namespace UnitTests.Dissemination;

public partial class DisseminationProtocolTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task MembershipNamespaceFullSnapshotPreservesMixedLivenessInRealManager(
        long incomingVersion, bool complementaryPeers)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var local = CreateSilo(39500);
        var newer = CreateSilo(39501);
        var stale = CreateSilo(39502);
        var previous = CreateMembershipSnapshot(
            1,
            CreateMembershipEntry(newer, SiloStatus.Active, DateTime.UnixEpoch, DateTime.UnixEpoch.AddSeconds(10)),
            CreateMembershipEntry(stale, SiloStatus.Active, DateTime.UnixEpoch, DateTime.UnixEpoch.AddSeconds(20)));
        var incoming = CreateMembershipSnapshot(
            incomingVersion,
            previous.Entries[newer].WithIAmAliveTime(DateTime.UnixEpoch.AddSeconds(30)),
            previous.Entries[stale].WithIAmAliveTime(DateTime.UnixEpoch.AddSeconds(5)));
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer>();
        using var manager = CreateMembershipReviewManager(local, out _);
        await ((IMembershipManager)manager).ProcessGossipSnapshot(previous, cancellationToken);
        var ns = CreateMembershipNamespace((IMembershipManager)manager, serializer);
        var value = new DisseminationValue(
            DisseminationKey.Default,
            0,
            incomingVersion,
            serializer.SerializeToArray(new MembershipTableSnapshotUpdate { Snapshot = incoming }));

        Assert.True(incoming.IsSuccessorTo(previous));
        if (complementaryPeers)
        {
            var complementary = CreateMembershipSnapshot(incomingVersion,
                previous.Entries[newer].WithIAmAliveTime(DateTime.UnixEpoch.AddSeconds(15)),
                previous.Entries[stale].WithIAmAliveTime(DateTime.UnixEpoch.AddSeconds(40)));
            var otherValue = new DisseminationValue(DisseminationKey.Default, 0, incomingVersion,
                serializer.SerializeToArray(new MembershipTableSnapshotUpdate { Snapshot = complementary }));
            var transport = new FakeTransport(local, newer, stale);
            transport.ExchangeAntiEntropyHandler = (peer, _, _) => ValueTask.FromResult(new DisseminationAntiEntropyResponse
            {
                Sender = peer,
                Values = CreateValueGroups(ns.Name, CreateDisseminationValue(peer, peer.Equals(newer) ? value : otherValue)),
            });
            var protocol = CreateProtocol(transport, [ns], options => options.Overlay.AntiEntropyPeerCount = 2);
            try
            {
                await protocol.RunAntiEntropyRound(cancellationToken);
                Assert.Equal(2, transport.AntiEntropyRequests.Count);
            }
            finally
            {
                await protocol.StopAsync(cancellationToken);
            }
        }
        else
        {
            Assert.Equal(DisseminationApplyResult.Applied, await ns.ApplyValueAsync(value, cancellationToken));
        }

        var expected = CreateMembershipSnapshot(
            incomingVersion,
            incoming.Entries[newer],
            complementaryPeers
                ? previous.Entries[stale].WithIAmAliveTime(DateTime.UnixEpoch.AddSeconds(40))
                : previous.Entries[stale]);
        AssertMembershipState(expected, manager.MembershipTableSnapshot);
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(5), incoming.Entries[stale].IAmAliveTime);
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(10), previous.Entries[newer].IAmAliveTime);

        // Repairs must contain the manager's merged state, not the unmerged incoming snapshot.
        var repair = GetMembershipRepair(ns, incomingVersion);
        Assert.Equal(0, repair.FromVersion);
        var repaired = Assert.IsType<MembershipTableSnapshotUpdate>(
            serializer.Deserialize<MembershipTableSnapshotUpdate>(repair.Payload));
        AssertMembershipState(expected, Assert.IsType<MembershipTableSnapshot>(repaired.Snapshot));
        Assert.Equal(DisseminationApplyResult.Duplicate, await ns.ApplyValueAsync(value, cancellationToken));
        AssertMembershipState(expected, manager.MembershipTableSnapshot);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MembershipNamespaceFullSnapshotPrunesDeadRowsAndRepairsDownstream(
        bool advanceVersion, bool receiverAlreadyAtTargetVersion)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var retired = CreateSilo(39511);
        var successor = SiloAddress.New(retired.Endpoint, retired.Generation + 1);
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer>();
        var staleGenerationSnapshot = CreateMembershipSnapshot(1,
            CreateMembershipEntry(retired, SiloStatus.Active, DateTime.UnixEpoch));
        var beforeCleanup = CreateMembershipSnapshot(2,
            staleGenerationSnapshot.Entries[retired].WithStatus(SiloStatus.Dead),
            CreateMembershipEntry(successor, SiloStatus.Active, DateTime.UnixEpoch, DateTime.UnixEpoch.AddSeconds(20)));
        using var sourceManager = CreateMembershipReviewManager(CreateSilo(39510), out _);
        using var receiverManager = CreateMembershipReviewManager(CreateSilo(39512), out _);
        await ((IMembershipManager)sourceManager).ProcessGossipSnapshot(beforeCleanup, cancellationToken);
        await ((IMembershipManager)receiverManager).ProcessGossipSnapshot(beforeCleanup, cancellationToken);

        var source = CreateMembershipNamespace((IMembershipManager)sourceManager, serializer);
        var receiver = CreateMembershipNamespace((IMembershipManager)receiverManager, serializer);
        var sourceBaseline = Assert.Single(source.Digests);
        var receiverBaseline = Assert.Single(receiver.Digests);
        Assert.Equal(sourceBaseline, receiverBaseline);

        var afterCleanup = MembershipTableSnapshot.Update(beforeCleanup, CreateMembershipSnapshot(
            beforeCleanup.Version.Value + (advanceVersion ? 1 : 0),
            beforeCleanup.Entries[successor].WithIAmAliveTime(DateTime.UnixEpoch.AddSeconds(10))));
        Assert.Equal(beforeCleanup.Version.Value + (advanceVersion ? 1 : 0), afterCleanup.Version.Value);
        Assert.Equal(successor, Assert.Single(afterCleanup.Entries).Key);
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(20), afterCleanup.Entries[successor].IAmAliveTime);
        Assert.True(afterCleanup.IsSuccessorTo(beforeCleanup));
        await ((IMembershipManager)sourceManager).ProcessGossipSnapshot(afterCleanup, cancellationToken);
        var target = CreateMembershipSnapshot(afterCleanup.Version.Value,
            afterCleanup.Entries[successor].WithIAmAliveTime(DateTime.UnixEpoch.AddSeconds(30)));
        await ((IMembershipManager)sourceManager).ProcessGossipSnapshot(target, cancellationToken);
        if (receiverAlreadyAtTargetVersion)
        {
            await ((IMembershipManager)receiverManager).ProcessGossipSnapshot(afterCleanup, cancellationToken);
            Assert.Equal(target.Entries.Keys, receiverManager.MembershipTableSnapshot.Entries.Keys);
            Assert.Equal(DateTime.UnixEpoch.AddSeconds(20),
                receiverManager.MembershipTableSnapshot.Entries[successor].IAmAliveTime);
        }

        var value = GetMembershipRepair(source, sourceBaseline.Version);
        Assert.Equal((0L, target.Version.Value), (value.FromVersion, value.ToVersion));
        var update = Assert.IsType<MembershipTableSnapshotUpdate>(
            serializer.Deserialize<MembershipTableSnapshotUpdate>(value.Payload));
        var snapshot = Assert.IsType<MembershipTableSnapshot>(update.Snapshot);
        AssertMembershipState(target, snapshot);
        Assert.Equal(successor, Assert.Single(snapshot.Entries).Key);

        Assert.Equal(DisseminationApplyResult.Applied, await receiver.ApplyValueAsync(value, cancellationToken));
        AssertMembershipState(target, receiverManager.MembershipTableSnapshot);
        Assert.False(receiverManager.MembershipTableSnapshot.Entries.ContainsKey(retired));
        Assert.Equal(SiloStatus.Dead, receiverManager.MembershipTableSnapshot.GetSiloStatus(retired));
        Assert.Equal(SiloStatus.Active, receiverManager.MembershipTableSnapshot.GetSiloStatus(successor));
        Assert.Equal(Assert.Single(source.Digests), Assert.Single(receiver.Digests));
        Assert.Equal(DisseminationApplyResult.Duplicate, await receiver.ApplyValueAsync(value, cancellationToken));

        var obsolete = new DisseminationValue(
            DisseminationKey.Default,
            0,
            staleGenerationSnapshot.Version.Value,
            serializer.SerializeToArray(new MembershipTableSnapshotUpdate { Snapshot = staleGenerationSnapshot }));
        Assert.Equal(DisseminationApplyResult.Obsolete, await receiver.ApplyValueAsync(obsolete, cancellationToken));
        AssertMembershipState(target, receiverManager.MembershipTableSnapshot);

        using var downstreamManager = CreateMembershipReviewManager(CreateSilo(39513), out _);
        await ((IMembershipManager)downstreamManager).ProcessGossipSnapshot(beforeCleanup, cancellationToken);
        var downstream = CreateMembershipNamespace((IMembershipManager)downstreamManager, serializer);
        var forwarded = GetMembershipRepair(receiver, sourceBaseline.Version);
        Assert.Equal(0, forwarded.FromVersion);
        Assert.Equal(target.Version.Value, forwarded.ToVersion);
        Assert.Equal(DisseminationApplyResult.Applied, await downstream.ApplyValueAsync(forwarded, cancellationToken));
        AssertMembershipState(target, downstreamManager.MembershipTableSnapshot);
    }

    [Fact]
    public async Task MembershipNamespaceFullSnapshotPreservesRealManagerLocalDeathEntry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var local = CreateSilo(39520);
        var peer = CreateSilo(39521);
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer>();
        using var manager = CreateMembershipReviewManager(local, out var fatalErrorHandler);
        var localEntry = manager.MembershipTableSnapshot.Entries[local].WithStatus(SiloStatus.Active);
        var previous = CreateMembershipSnapshot(
            1,
            localEntry,
            CreateMembershipEntry(peer, SiloStatus.Active, DateTime.UnixEpoch));
        await ((IMembershipManager)manager).ProcessGossipSnapshot(previous, cancellationToken);
        var ns = CreateMembershipNamespace((IMembershipManager)manager, serializer);
        var update = new MembershipTableSnapshotUpdate
        {
            Snapshot = CreateMembershipSnapshot(2, previous.Entries[peer]),
        };
        var value = new DisseminationValue(DisseminationKey.Default, 0, 2, serializer.SerializeToArray(update));

        Assert.Equal(DisseminationApplyResult.Applied, await ns.ApplyValueAsync(value, cancellationToken));

        var expected = CreateMembershipSnapshot(2, localEntry.WithStatus(SiloStatus.Dead), previous.Entries[peer]);
        AssertMembershipState(expected, manager.MembershipTableSnapshot);
        Assert.Equal(SiloStatus.Dead, manager.CurrentStatus);
        fatalErrorHandler.Received(1).OnFatalException(manager, Arg.Any<string>(), null);
        var repair = GetMembershipRepair(ns, 2);
        Assert.Equal(0, repair.FromVersion);
        var repaired = Assert.IsType<MembershipTableSnapshotUpdate>(
            serializer.Deserialize<MembershipTableSnapshotUpdate>(repair.Payload));
        AssertMembershipState(expected, Assert.IsType<MembershipTableSnapshot>(repaired.Snapshot));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MembershipNamespaceDeadRowCleanupConvergesWithoutHeartbeatWithFullRepair(bool advanceVersion)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var retired = CreateSilo(39541);
        var successor = SiloAddress.New(retired.Endpoint, retired.Generation + 1);
        var receiverBaseline = CreateMembershipSnapshot(
            1,
            CreateMembershipEntry(retired, SiloStatus.Dead, DateTime.UnixEpoch),
            CreateMembershipEntry(successor, SiloStatus.Active, DateTime.UnixEpoch, DateTime.UnixEpoch.AddSeconds(10)));
        var target = CreateMembershipSnapshot(
            receiverBaseline.Version.Value + (advanceVersion ? 1 : 0),
            receiverBaseline.Entries[successor]);
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer>();
        using var sourceManager = CreateMembershipReviewManager(CreateSilo(39540), out _);
        using var receiverManager = CreateMembershipReviewManager(CreateSilo(39542), out _);
        await ((IMembershipManager)sourceManager).ProcessGossipSnapshot(receiverBaseline, cancellationToken);
        await ((IMembershipManager)receiverManager).ProcessGossipSnapshot(receiverBaseline, cancellationToken);
        var source = CreateMembershipNamespace((IMembershipManager)sourceManager, serializer);
        var receiver = CreateMembershipNamespace((IMembershipManager)receiverManager, serializer);
        _ = Assert.Single(source.Digests);
        await ((IMembershipManager)sourceManager).ProcessGossipSnapshot(target, cancellationToken);
        Assert.Equal(SiloStatus.Dead, receiverManager.MembershipTableSnapshot.GetSiloStatus(retired));
        Assert.True(receiverManager.MembershipTableSnapshot.Entries.ContainsKey(retired));
        Assert.Equal(receiverBaseline.Version, receiverManager.MembershipTableSnapshot.Version);
        Assert.Equal(target.Version, sourceManager.MembershipTableSnapshot.Version);
        Assert.Equal(receiverBaseline.Entries[successor].IAmAliveTime, target.Entries[successor].IAmAliveTime);

        var transport = new FakeTransport(CreateSilo(39540), CreateSilo(39542));
        var protocol = CreateProtocol(transport, [source], timeProvider: new FakeTimeProvider());
        try
        {
            var response = await protocol.ReceiveAntiEntropy(new DisseminationAntiEntropyRequest
            {
                Sender = transport.Peers[0],
                Digests = new() { [source.Name] = [Assert.Single(receiver.Digests)] },
            }, cancellationToken);
            var full = Assert.Single(GetAntiEntropyResponseValues(response)).Value;
            Assert.Equal((0L, target.Version.Value), (full.FromVersion, full.ToVersion));
            Assert.Equal(DisseminationApplyResult.Applied, await receiver.ApplyValueAsync(full, cancellationToken));
            AssertMembershipState(target, receiverManager.MembershipTableSnapshot);
            Assert.Equal(successor, Assert.Single(receiverManager.MembershipTableSnapshot.Entries).Key);
            Assert.Equal(Assert.Single(source.Digests), Assert.Single(receiver.Digests));
            Assert.Equal(DisseminationApplyResult.Duplicate, await receiver.ApplyValueAsync(full, cancellationToken));
            var converged = await protocol.ReceiveAntiEntropy(new DisseminationAntiEntropyRequest
            {
                Sender = transport.Peers[0],
                Digests = new() { [source.Name] = [Assert.Single(receiver.Digests)] },
            }, cancellationToken);
            Assert.Empty(GetAntiEntropyResponseValues(converged));

            var heartbeatTarget = CreateMembershipSnapshot(
                target.Version.Value,
                target.Entries[successor].WithIAmAliveTime(DateTime.UnixEpoch.AddSeconds(20)));
            await ((IMembershipManager)sourceManager).ProcessGossipSnapshot(heartbeatTarget, cancellationToken);
            response = await protocol.ReceiveAntiEntropy(new DisseminationAntiEntropyRequest
            {
                Sender = transport.Peers[0],
                Digests = new() { [source.Name] = [Assert.Single(receiver.Digests)] },
            }, cancellationToken);
            full = Assert.Single(GetAntiEntropyResponseValues(response)).Value;
            Assert.Equal((0L, target.Version.Value), (full.FromVersion, full.ToVersion));
            Assert.Equal(DisseminationApplyResult.Applied, await receiver.ApplyValueAsync(full, cancellationToken));
            AssertMembershipState(heartbeatTarget, receiverManager.MembershipTableSnapshot);
            Assert.Equal(SiloStatus.Dead, receiverManager.MembershipTableSnapshot.GetSiloStatus(retired));
            Assert.Equal(Assert.Single(source.Digests), Assert.Single(receiver.Digests));

            response = await protocol.ReceiveAntiEntropy(new DisseminationAntiEntropyRequest
            {
                Sender = transport.Peers[0],
                Digests = new() { [source.Name] = [Assert.Single(receiver.Digests)] },
            }, cancellationToken);
            Assert.Empty(GetAntiEntropyResponseValues(response));
        }
        finally
        {
            await protocol.StopAsync(cancellationToken);
        }
    }

    private static MembershipTableManager CreateMembershipReviewManager(
        SiloAddress local,
        out IFatalErrorHandler fatalErrorHandler,
        IMembershipTable membershipTable = null!)
    {
        var timer = Substitute.For<IAsyncTimer>();
        var timers = Substitute.For<IAsyncTimerFactory>();
        timers.Create(default, default!, default!).ReturnsForAnyArgs(timer);
        fatalErrorHandler = Substitute.For<IFatalErrorHandler>();
        return new MembershipTableManager(
            new FakeLocalSiloDetails(local),
            Options.Create(new ClusterMembershipOptions()),
            membershipTable ?? Substitute.For<IMembershipTable>(),
            fatalErrorHandler,
            Substitute.For<IMembershipGossiper>(),
            NullLogger<MembershipTableManager>.Instance,
            timers,
            new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance),
            new FakeTimeProvider());
    }

    private static MembershipDisseminationNamespace CreateMembershipNamespace(IMembershipManager manager, Serializer serializer)
    {
        var options = new ClusterMembershipOptions();
        options.Dissemination.Enabled = true;
        return new(manager, new TestOptionsMonitor<ClusterMembershipOptions>(options), serializer);
    }

    private static DisseminationValue GetMembershipRepair(MembershipDisseminationNamespace ns, long? fromVersion)
    {
        var repair = ns.CreateRepair(new(DisseminationKey.Default, fromVersion, 1024 * 1024, 1024 * 1024));
        Assert.Equal(DisseminationRepairStatus.Produced, repair.Status);
        return repair.Value;
    }
}
