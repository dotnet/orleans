using System.Collections.Immutable;
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
    [InlineData(1)]
    [InlineData(2)]
    public async Task MembershipNamespaceFullSnapshotPreservesMixedLivenessInRealManager(long incomingVersion)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
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
        using var manager = CreateMembershipReviewManager(CreateSilo(39500), out _);
        await ((IMembershipManager)manager).ProcessGossipSnapshot(previous, cancellationToken);
        var ns = CreateMembershipNamespace((IMembershipManager)manager, serializer);
        var value = new DisseminationValue(
            DisseminationKey.Default,
            0,
            incomingVersion,
            serializer.SerializeToArray(new MembershipTableSnapshotUpdate { Snapshot = incoming }));

        Assert.True(incoming.IsSuccessorTo(previous));
        Assert.Equal(DisseminationApplyResult.Applied, await ns.ApplyValueAsync(value, cancellationToken));

        var expected = CreateMembershipSnapshot(
            incomingVersion,
            incoming.Entries[newer],
            previous.Entries[stale]);
        AssertMembershipState(expected, manager.MembershipTableSnapshot);
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(5), incoming.Entries[stale].IAmAliveTime);
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(10), previous.Entries[newer].IAmAliveTime);

        // Repairs must contain the manager's merged state, not the unmerged incoming snapshot.
        var repair = Assert.Single(ns.CreateRepair(MembershipReviewRepairRequest(incomingVersion)).Values);
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
    public async Task MembershipNamespaceCompleteDiffPrunesDivergentInventoryAndRepairsDownstream(bool receiverAlreadyAtTargetVersion)
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

        var value = Assert.Single(source.CreateRepair(MembershipReviewRepairRequest(sourceBaseline.Version)).Values);
        Assert.Equal((sourceBaseline.Version, target.Version.Value), (value.FromVersion, value.ToVersion));
        var update = Assert.IsType<MembershipTableSnapshotUpdate>(
            serializer.Deserialize<MembershipTableSnapshotUpdate>(value.Payload));
        var diff = Assert.IsType<MembershipTableSnapshotDiff>(update.Diff);
        Assert.True(diff.IncludesAllEntries);
        Assert.Empty(diff.RemovedSilos);
        Assert.Equal(successor, Assert.Single(diff.UpdatedEntries).SiloAddress);

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
        var forwarded = Assert.Single(receiver.CreateRepair(MembershipReviewRepairRequest(sourceBaseline.Version)).Values);
        Assert.True(forwarded.FromVersion == 0 || forwarded.FromVersion == sourceBaseline.Version);
        Assert.Equal(target.Version.Value, forwarded.ToVersion);
        Assert.Equal(DisseminationApplyResult.Applied, await downstream.ApplyValueAsync(forwarded, cancellationToken));
        AssertMembershipState(target, downstreamManager.MembershipTableSnapshot);
    }

    [Fact]
    public async Task MembershipNamespaceCompleteDiffPreservesRealManagerLocalDeathEntry()
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
            Diff = new MembershipTableSnapshotDiff(
                previous.Version,
                new MembershipVersion(2),
                [previous.Entries[peer]],
                [],
                includesAllEntries: true),
        };
        var value = new DisseminationValue(DisseminationKey.Default, 1, 2, serializer.SerializeToArray(update));

        Assert.Equal(DisseminationApplyResult.Applied, await ns.ApplyValueAsync(value, cancellationToken));

        var expected = CreateMembershipSnapshot(2, localEntry.WithStatus(SiloStatus.Dead), previous.Entries[peer]);
        AssertMembershipState(expected, manager.MembershipTableSnapshot);
        Assert.Equal(SiloStatus.Dead, manager.CurrentStatus);
        fatalErrorHandler.Received(1).OnFatalException(manager, Arg.Any<string>(), null);
        var repair = Assert.Single(ns.CreateRepair(MembershipReviewRepairRequest(2)).Values);
        Assert.Equal(0, repair.FromVersion);
        var repaired = Assert.IsType<MembershipTableSnapshotUpdate>(
            serializer.Deserialize<MembershipTableSnapshotUpdate>(repair.Payload));
        AssertMembershipState(expected, Assert.IsType<MembershipTableSnapshot>(repaired.Snapshot));
    }

    [Fact]
    public async Task MembershipNamespaceDiffWireCompatibilityPreservesLegacyPartialUpdates()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var retained = CreateSilo(39531);
        var updated = CreateSilo(39532);
        var removed = CreateSilo(39533);
        var previous = CreateMembershipSnapshot(
            1,
            CreateMembershipEntry(retained, SiloStatus.Active, DateTime.UnixEpoch),
            CreateMembershipEntry(updated, SiloStatus.Active, DateTime.UnixEpoch),
            CreateMembershipEntry(removed, SiloStatus.Dead, DateTime.UnixEpoch));
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer>();
        var legacy = new MembershipReviewLegacyDiff
        {
            BaseVersion = previous.Version,
            Version = new MembershipVersion(2),
            UpdatedEntries = [previous.Entries[updated].WithIAmAliveTime(DateTime.UnixEpoch.AddSeconds(10))],
            RemovedSilos = [removed],
        };

        // Root codecs omit the type name when the expected and actual types agree, allowing
        // these two schemas to exercise both missing-field and unknown-field wire compatibility.
        var decoded = Assert.IsType<MembershipTableSnapshotDiff>(
            serializer.Deserialize<MembershipTableSnapshotDiff>(serializer.SerializeToArray(legacy)));
        Assert.False(decoded.IncludesAllEntries);
        Assert.Equal(legacy.BaseVersion, decoded.BaseVersion);
        Assert.Equal(legacy.Version, decoded.Version);
        Assert.Equal(legacy.RemovedSilos, decoded.RemovedSilos);
        Assert.Equal(updated, Assert.Single(decoded.UpdatedEntries).SiloAddress);
        using var manager = CreateMembershipReviewManager(CreateSilo(39530), out _);
        await ((IMembershipManager)manager).ProcessGossipSnapshot(previous, cancellationToken);
        var ns = CreateMembershipNamespace((IMembershipManager)manager, serializer);
        var value = new DisseminationValue(
            DisseminationKey.Default,
            1,
            2,
            serializer.SerializeToArray(new MembershipTableSnapshotUpdate { Diff = decoded }));

        Assert.Equal(DisseminationApplyResult.Applied, await ns.ApplyValueAsync(value, cancellationToken));
        var expected = CreateMembershipSnapshot(2, previous.Entries[retained], legacy.UpdatedEntries[0]);
        AssertMembershipState(expected, manager.MembershipTableSnapshot);

        var complete = new MembershipTableSnapshotDiff(
            legacy.BaseVersion,
            legacy.Version,
            [.. expected.Entries.Values],
            legacy.RemovedSilos,
            includesAllEntries: true);
        var oldPeer = Assert.IsType<MembershipReviewLegacyDiff>(
            serializer.Deserialize<MembershipReviewLegacyDiff>(serializer.SerializeToArray(complete)));
        Assert.Equal(complete.BaseVersion, oldPeer.BaseVersion);
        Assert.Equal(complete.Version, oldPeer.Version);
        Assert.Equal(complete.RemovedSilos, oldPeer.RemovedSilos);
        AssertMembershipState(
            expected,
            CreateMembershipSnapshot(oldPeer.Version.Value, [.. oldPeer.UpdatedEntries]));
    }

    [Fact]
    public async Task MembershipNamespaceLegacyInventoryDivergenceConvergesAfterHeartbeatWithFullRepair()
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
        var delta = Assert.Single(source.CreateRepair(MembershipReviewRepairRequest(1)).Values);
        Assert.Equal((1L, 2L), (delta.FromVersion, delta.ToVersion));
        var update = Assert.IsType<MembershipTableSnapshotUpdate>(
            serializer.Deserialize<MembershipTableSnapshotUpdate>(delta.Payload));
        var legacy = Assert.IsType<MembershipReviewLegacyDiff>(
            serializer.Deserialize<MembershipReviewLegacyDiff>(
                serializer.SerializeToArray(Assert.IsType<MembershipTableSnapshotDiff>(update.Diff))));
        var legacyUpdate = new MembershipTableSnapshotUpdate
        {
            Diff = new MembershipTableSnapshotDiff(legacy.BaseVersion, legacy.Version, legacy.UpdatedEntries, legacy.RemovedSilos),
        };
        var legacyValue = new DisseminationValue(DisseminationKey.Default, 1, 2, serializer.SerializeToArray(legacyUpdate));
        Assert.Equal(DisseminationApplyResult.Applied, await receiver.ApplyValueAsync(legacyValue, cancellationToken));
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
            // Inventory-only cleanup need not be published by the manager: retired entries are already dead.
            Assert.Equal(DisseminationApplyResult.Duplicate, await receiver.ApplyValueAsync(full, cancellationToken));

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
        out IFatalErrorHandler fatalErrorHandler)
    {
        var timer = Substitute.For<IAsyncTimer>();
        var timers = Substitute.For<IAsyncTimerFactory>();
        timers.Create(default, default!, default!).ReturnsForAnyArgs(timer);
        fatalErrorHandler = Substitute.For<IFatalErrorHandler>();
        return new MembershipTableManager(
            new FakeLocalSiloDetails(local),
            Options.Create(new ClusterMembershipOptions()),
            Substitute.For<IMembershipTable>(),
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

    private static DisseminationRepairRequest MembershipReviewRepairRequest(long? fromVersion) =>
        new(DisseminationKey.Default, fromVersion, toVersion: null, maxItemCount: 1, maxBatchBytes: 1024 * 1024, maxPayloadBytes: 1024 * 1024);

    [GenerateSerializer]
    internal sealed class MembershipReviewLegacyDiff
    {
        [Id(0)]
        public MembershipVersion BaseVersion { get; init; }

        [Id(1)]
        public MembershipVersion Version { get; init; }

        [Id(2)]
        public ImmutableArray<MembershipEntry> UpdatedEntries { get; init; }

        [Id(3)]
        public ImmutableArray<SiloAddress> RemovedSilos { get; init; }
    }
}
