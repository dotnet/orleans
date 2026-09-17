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
    [InlineData(false)]
    [InlineData(true)]
    public async Task MembershipNamespaceFullSnapshotPrunesDivergentInventoryAndRepairsDownstream(bool receiverAlreadyAtTargetVersion)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var retired = CreateSilo(39511);
        var successor = SiloAddress.New(retired.Endpoint, retired.Generation + 1);
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer>();
        var table = new InMemoryMembershipTable(services.GetRequiredService<DeepCopier>());
        Assert.True(table.Insert(
            CreateMembershipEntry(retired, SiloStatus.Active, DateTime.UnixEpoch),
            table.ReadTableVersion().Next()));
        var staleGenerationSnapshot = MembershipTableSnapshot.Create(table.ReadAll());
        var retiredRow = Assert.Single(table.ReadAll().Members);
        Assert.True(table.Update(
            retiredRow.Item1.WithStatus(SiloStatus.Dead),
            retiredRow.Item2,
            table.ReadTableVersion().Next()));
        Assert.True(table.Insert(
            CreateMembershipEntry(successor, SiloStatus.Active, DateTime.UnixEpoch, DateTime.UnixEpoch.AddSeconds(10)),
            table.ReadTableVersion().Next()));
        var beforeCleanup = MembershipTableSnapshot.Create(table.ReadAll());
        using var sourceManager = CreateMembershipReviewManager(CreateSilo(39510), out _);
        using var receiverManager = CreateMembershipReviewManager(CreateSilo(39512), out _);
        await ((IMembershipManager)sourceManager).ProcessGossipSnapshot(beforeCleanup, cancellationToken);
        await ((IMembershipManager)receiverManager).ProcessGossipSnapshot(beforeCleanup, cancellationToken);

        var tableVersion = table.ReadTableVersion();
        table.CleanupDefunctSiloEntries(DateTimeOffset.UnixEpoch.AddSeconds(1));
        Assert.Equal(tableVersion, table.ReadTableVersion());
        Assert.DoesNotContain(table.ReadAll().Members, entry => entry.Item1.SiloAddress.Equals(retired));
        table.UpdateIAmAlive(beforeCleanup.Entries[successor].WithIAmAliveTime(DateTime.UnixEpoch.AddSeconds(20)));
        var afterCleanup = MembershipTableSnapshot.Update(beforeCleanup, table.ReadAll());
        Assert.Equal(beforeCleanup.Version, afterCleanup.Version);
        Assert.True(afterCleanup.IsSuccessorTo(beforeCleanup));
        await ((IMembershipManager)sourceManager).ProcessGossipSnapshot(afterCleanup, cancellationToken);
        var source = CreateMembershipNamespace((IMembershipManager)sourceManager, serializer);
        var receiver = CreateMembershipNamespace((IMembershipManager)receiverManager, serializer);
        var sourceBaseline = Assert.Single(source.Digests);
        var receiverBaseline = Assert.Single(receiver.Digests);
        Assert.Equal(sourceBaseline.Version, receiverBaseline.Version);
        Assert.NotEqual(sourceBaseline.Fingerprint, receiverBaseline.Fingerprint);

        var row = Assert.Single(table.ReadAll().Members);
        Assert.True(table.Update(
            row.Item1.WithIAmAliveTime(DateTime.UnixEpoch.AddSeconds(30)),
            row.Item2,
            table.ReadTableVersion().Next()));
        var target = MembershipTableSnapshot.Update(afterCleanup, table.ReadAll());
        await ((IMembershipManager)sourceManager).ProcessGossipSnapshot(target, cancellationToken);
        if (receiverAlreadyAtTargetVersion)
        {
            await ((IMembershipManager)receiverManager).ProcessGossipSnapshot(
                new MembershipTableSnapshot(target.Version, beforeCleanup.Entries),
                cancellationToken);
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

    [Fact]
    public async Task MembershipNamespaceInventoryDivergenceConvergesWithoutHeartbeatWithFullRepair()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var retired = CreateSilo(39541);
        var successor = SiloAddress.New(retired.Endpoint, retired.Generation + 1);
        var receiverBaseline = CreateMembershipSnapshot(
            1,
            CreateMembershipEntry(retired, SiloStatus.Dead, DateTime.UnixEpoch),
            CreateMembershipEntry(successor, SiloStatus.Active, DateTime.UnixEpoch));
        var sourceBaseline = CreateMembershipSnapshot(1, receiverBaseline.Entries[successor]);
        var target = CreateMembershipSnapshot(
            2,
            sourceBaseline.Entries[successor].WithIAmAliveTime(DateTime.UnixEpoch.AddSeconds(10)));
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer>();
        using var sourceManager = CreateMembershipReviewManager(CreateSilo(39540), out _);
        using var receiverManager = CreateMembershipReviewManager(CreateSilo(39542), out _);
        await ((IMembershipManager)sourceManager).ProcessGossipSnapshot(sourceBaseline, cancellationToken);
        await ((IMembershipManager)receiverManager).ProcessGossipSnapshot(receiverBaseline, cancellationToken);
        var source = CreateMembershipNamespace((IMembershipManager)sourceManager, serializer);
        var receiver = CreateMembershipNamespace((IMembershipManager)receiverManager, serializer);
        _ = Assert.Single(source.Digests);
        await ((IMembershipManager)sourceManager).ProcessGossipSnapshot(target, cancellationToken);
        await ((IMembershipManager)receiverManager).ProcessGossipSnapshot(
            CreateMembershipSnapshot(2, receiverBaseline.Entries[retired], target.Entries[successor]), cancellationToken);
        Assert.Equal(SiloStatus.Dead, receiverManager.MembershipTableSnapshot.GetSiloStatus(retired));
        Assert.True(receiverManager.MembershipTableSnapshot.Entries.ContainsKey(retired));
        Assert.Equal(target.Version, receiverManager.MembershipTableSnapshot.Version);

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
            Assert.Equal((0L, 2L), (full.FromVersion, full.ToVersion));
            Assert.Equal(DisseminationApplyResult.Applied, await receiver.ApplyValueAsync(full, cancellationToken));
            AssertMembershipState(target, receiverManager.MembershipTableSnapshot);
            Assert.Equal(Assert.Single(source.Digests), Assert.Single(receiver.Digests));
            Assert.Equal(DisseminationApplyResult.Duplicate, await receiver.ApplyValueAsync(full, cancellationToken));
            var converged = await protocol.ReceiveAntiEntropy(new DisseminationAntiEntropyRequest
            {
                Sender = transport.Peers[0],
                Digests = new() { [source.Name] = [Assert.Single(receiver.Digests)] },
            }, cancellationToken);
            Assert.Empty(GetAntiEntropyResponseValues(converged));

            var heartbeatTarget = CreateMembershipSnapshot(
                2,
                target.Entries[successor].WithIAmAliveTime(DateTime.UnixEpoch.AddSeconds(20)));
            await ((IMembershipManager)sourceManager).ProcessGossipSnapshot(heartbeatTarget, cancellationToken);
            response = await protocol.ReceiveAntiEntropy(new DisseminationAntiEntropyRequest
            {
                Sender = transport.Peers[0],
                Digests = new() { [source.Name] = [Assert.Single(receiver.Digests)] },
            }, cancellationToken);
            full = Assert.Single(GetAntiEntropyResponseValues(response)).Value;
            Assert.Equal((0L, 2L), (full.FromVersion, full.ToVersion));
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
