#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Orleans.Runtime;
using Orleans.Runtime.Dissemination;
using Orleans.Serialization;
using Xunit;

namespace UnitTests.Dissemination;

public partial class DisseminationProtocolTests
{
    [Fact]
    public void MembershipBroadcastDeltaIsSparseWhileRepairRemainsFull()
    {
        var members = Enumerable.Range(43000, 2000).Select(CreateSilo).ToArray();
        var initial = CreateMembershipSnapshot(1, members.Select(silo =>
            CreateMembershipEntry(silo, SiloStatus.Active, DateTime.UnixEpoch)).ToArray());
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer>();
        var manager = new FakeMembershipManager(initial);
        var ns = CreateMembershipNamespace(manager, serializer);
        var request = new DisseminationRepairRequest(DisseminationKey.Default, 1, 1024 * 1024, 1024 * 1024);
        var probe = ns.CreateBroadcast(request, baseline: null);
        Assert.Equal(DisseminationRepairStatus.Produced, probe.Status);
        Assert.Empty(ReadMembershipDelta(serializer, probe.Value).UpdatedEntries);
        var baseline = Assert.IsAssignableFrom<DisseminationBroadcastState>(probe.BroadcastState);
        manager.CurrentSnapshot = new(new MembershipVersion(2),
            initial.Entries.SetItem(members[999], initial.Entries[members[999]].WithStatus(SiloStatus.ShuttingDown)));

        var broadcast = ns.CreateBroadcast(request, baseline);
        var full = ns.CreateRepair(request);

        Assert.Equal(DisseminationRepairStatus.Produced, broadcast.Status);
        Assert.Equal(DisseminationRepairStatus.Produced, full.Status);
        var delta = ReadMembershipDelta(serializer, broadcast.Value);
        Assert.Equal((1L, 2L), (broadcast.Value.FromVersion, broadcast.Value.ToVersion));
        var changed = Assert.Single(delta.UpdatedEntries);
        Assert.Equal(members[999], changed.SiloAddress);
        Assert.Equal(SiloStatus.ShuttingDown, changed.Status);
        Assert.Empty(delta.RemovedSilos);
        Assert.Equal(0, full.Value.FromVersion);
        var repair = Assert.IsType<MembershipTableSnapshotUpdate>(serializer.Deserialize<MembershipTableSnapshotUpdate>(full.Value.Payload));
        Assert.Null(repair.Delta);
        Assert.Equal(2000, Assert.IsType<MembershipTableSnapshot>(repair.Snapshot).Entries.Count);
        Assert.True(broadcast.Value.Payload.Length * 10 < full.Value.Payload.Length,
            $"Sparse broadcast {broadcast.Value.Payload.Length} bytes, full repair {full.Value.Payload.Length} bytes.");
        Assert.Equal(broadcast.Value.Payload, ns.CreateBroadcast(request, baseline).Value.Payload);
    }

    [Fact]
    public async Task MembershipBroadcastCoalescesFromAcknowledgedSnapshot()
    {
        var local = CreateSilo(45001);
        var peer = CreateSilo(45002);
        var removed = CreateSilo(45003);
        var added = CreateSilo(45004);
        var initial = CreateMembershipSnapshot(1,
            CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch),
            CreateMembershipEntry(peer, SiloStatus.Active, DateTime.UnixEpoch),
            CreateMembershipEntry(removed, SiloStatus.Dead, DateTime.UnixEpoch));
        await using var link = new MembershipDeltaLink(local, peer, initial, initial);
        await link.Send();
        // Metadata can return to its prior value across skipped observations; compare with the actual baseline.
        var temporaryEntry = CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch);
        temporaryEntry.HostName = "temporary";
        var temporary = new MembershipTableSnapshot(new MembershipVersion(2),
            initial.Entries.SetItem(local, temporaryEntry));
        var final = new MembershipTableSnapshot(new MembershipVersion(3),
            initial.Entries.Remove(removed)
                .SetItem(peer, initial.Entries[peer].WithIAmAliveTime(DateTime.UnixEpoch.AddSeconds(10)))
                .Add(added, CreateMembershipEntry(added, SiloStatus.Joining, DateTime.UnixEpoch)));
        var flush = BeforeBroadcastPumpsRun(() =>
        {
            link.SourceManager.CurrentSnapshot = temporary;
            Assert.True(link.Queue.Notify(peer, link.Source, DisseminationKey.Default));
            link.SourceManager.CurrentSnapshot = final;
            Assert.True(link.Queue.Notify(peer, link.Source, DisseminationKey.Default));
            return link.Queue.FlushPendingBroadcast(TestContext.Current.CancellationToken);
        });

        await flush.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(2, link.Batches.Count);
        var value = Assert.Single(GetBroadcastValues(link.Batches[1])).Value;
        var delta = ReadMembershipDelta(link.Serializer, value);
        Assert.Equal((1L, 3L), (value.FromVersion, value.ToVersion));
        Assert.Equal(new[] { peer, added }, delta.UpdatedEntries.Select(entry => entry.SiloAddress).Order());
        Assert.Equal(removed, Assert.Single(delta.RemovedSilos));
        AssertMembershipState(final, link.TargetManager.CurrentSnapshot);
    }

    [Fact]
    public async Task MembershipBroadcastCapturesBaselineBeforeAcknowledgment()
    {
        var local = CreateSilo(45101);
        var peer = CreateSilo(45102);
        var initial = CreateMembershipSnapshot(1,
            CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch),
            CreateMembershipEntry(peer, SiloStatus.Active, DateTime.UnixEpoch));
        await using var link = new MembershipDeltaLink(local, peer, initial, initial);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        link.BeforeResponse = async (_, index, token) =>
        {
            if (index == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
        };
        try
        {
            Assert.True(link.Queue.Notify(peer, link.Source, DisseminationKey.Default));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            link.SourceManager.CurrentSnapshot = new(initial.Version, initial.Entries.SetItem(
                local, initial.Entries[local].WithIAmAliveTime(DateTime.UnixEpoch.AddSeconds(10))));
            Assert.True(link.Queue.Notify(peer, link.Source, DisseminationKey.Default));
            var flush = link.Queue.FlushPendingBroadcast(TestContext.Current.CancellationToken);
            release.TrySetResult();
            await flush.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.Equal(2, link.Batches.Count);
            var delta = ReadMembershipDelta(link.Serializer, Assert.Single(GetBroadcastValues(link.Batches[1])).Value);
            Assert.Equal(DateTime.UnixEpoch.AddSeconds(10), Assert.Single(delta.UpdatedEntries).IAmAliveTime);
            AssertMembershipState(link.SourceManager.CurrentSnapshot, link.TargetManager.CurrentSnapshot);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task MembershipBroadcastGapUsesFullAntiEntropyRepair()
    {
        var local = CreateSilo(45201);
        var peer = CreateSilo(45202);
        var initial = CreateMembershipSnapshot(1,
            CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch),
            CreateMembershipEntry(peer, SiloStatus.Active, DateTime.UnixEpoch));
        var source = new MembershipTableSnapshot(new MembershipVersion(3),
            initial.Entries.SetItem(local, initial.Entries[local].WithIAmAliveTime(DateTime.UnixEpoch.AddSeconds(10))));
        await using var link = new MembershipDeltaLink(local, peer, source, initial);
        await link.Send();
        var probe = Assert.Single(GetBroadcastValues(Assert.Single(link.Batches))).Value;
        Assert.Equal((3L, 3L), (probe.FromVersion, probe.ToVersion));
        Assert.Equal(1, link.TargetManager.CurrentSnapshot.Version.Value);
        Assert.False(Assert.Single(link.Responses).AllVersionsAcknowledged);

        var repair = await link.SourceProtocol.ReceiveAntiEntropy(new DisseminationAntiEntropyRequest
        {
            Sender = peer,
            SupportedNamespaces = [link.Source.Name],
            Digests = new() { [link.Source.Name] = [.. link.Target.Digests] },
        }, TestContext.Current.CancellationToken);
        var full = Assert.Single(repair.Values[link.Source.Name]).Value;
        Assert.Equal(0, full.FromVersion);
        Assert.Null(Assert.IsType<MembershipTableSnapshotUpdate>(
            link.Serializer.Deserialize<MembershipTableSnapshotUpdate>(full.Payload)).Delta);
        Assert.Equal(DisseminationApplyResult.Applied,
            await link.Target.ApplyValueAsync(full, TestContext.Current.CancellationToken));
        await link.Send();

        AssertMembershipState(source, link.TargetManager.CurrentSnapshot);
        Assert.All(link.Batches.SelectMany(GetBroadcastValues), item => Assert.True(item.Value.FromVersion > 0));
        Assert.True(link.Responses[^1].AllVersionsAcknowledged);
    }

    [Fact]
    public async Task MembershipDeltaRetransmitsCumulativeChangesAfterLostAcknowledgment()
    {
        var local = CreateSilo(45601);
        var peer = CreateSilo(45602);
        var initial = CreateMembershipSnapshot(1,
            CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch),
            CreateMembershipEntry(peer, SiloStatus.Active, DateTime.UnixEpoch));
        await using var link = new MembershipDeltaLink(local, peer, initial, initial);
        await link.Send();
        link.BeforeResponse = (_, index, _) => index == 2
            ? Task.FromException(new InvalidOperationException("Acknowledgment lost."))
            : Task.CompletedTask;
        link.SourceManager.CurrentSnapshot = new(initial.Version, initial.Entries.SetItem(
            local, initial.Entries[local].WithIAmAliveTime(DateTime.UnixEpoch.AddSeconds(10))));
        await link.Send();
        link.SourceManager.CurrentSnapshot = new(initial.Version, link.SourceManager.CurrentSnapshot.Entries.SetItem(
            peer, initial.Entries[peer].WithIAmAliveTime(DateTime.UnixEpoch.AddSeconds(20))));

        await link.Send();

        Assert.Equal(3, link.Batches.Count);
        var delta = ReadMembershipDelta(link.Serializer, Assert.Single(GetBroadcastValues(link.Batches[2])).Value);
        Assert.Equal(new[] { local, peer }, delta.UpdatedEntries.Select(entry => entry.SiloAddress).Order());
        AssertMembershipState(link.SourceManager.CurrentSnapshot, link.TargetManager.CurrentSnapshot);
    }

    [Fact]
    public async Task AheadMembershipRelayBroadcastsLatestDeltaFromChildBaseline()
    {
        var root = CreateSilo(45801);
        var relay = CreateSilo(45802);
        var child = CreateSilo(45803);
        var members = new[] { root, relay, child };
        var initial = CreateMembershipSnapshot(1, members.Select(silo =>
            CreateMembershipEntry(silo, SiloStatus.Active, DateTime.UnixEpoch)).ToArray());
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer>();
        var managers = members.ToDictionary(silo => silo, _ => new FakeMembershipManager(initial));
        var namespaces = members.ToDictionary(silo => silo, silo => CreateMembershipNamespace(managers[silo], serializer));
        var transports = members.ToDictionary(silo => silo, silo => new FakeTransport(silo, members.Where(other => !other.Equals(silo)).ToArray()));
        var protocols = members.ToDictionary(silo => silo, silo => CreateProtocol(
            transports[silo], [namespaces[silo]],
            options => options.Overlay.FanOutFactor = static _ => 1));
        var sent = new Dictionary<SiloAddress, List<DisseminationBroadcastBatch>>
        {
            [root] = [],
            [relay] = [],
            [child] = [],
        };
        foreach (var silo in members)
        {
            transports[silo].SendBroadcastResponseHandler = (peer, batch, token) =>
            {
                sent[silo].Add(batch);
                return protocols[peer].ReceiveBroadcast(batch, token);
            };
        }

        var token = TestContext.Current.CancellationToken;
        try
        {
            Assert.True(await protocols[root].Publish(namespaces[root], DisseminationKey.Default, 1, token));
            await protocols[root].FlushPendingBroadcast(token);
            await protocols[relay].FlushPendingBroadcast(token);
            Assert.Single(sent[root]);
            Assert.Single(sent[relay]);

            var second = new MembershipTableSnapshot(new MembershipVersion(2),
                initial.Entries.SetItem(root, initial.Entries[root].WithStatus(SiloStatus.ShuttingDown)));
            var third = new MembershipTableSnapshot(new MembershipVersion(3),
                initial.Entries.SetItem(root, initial.Entries[root].WithStatus(SiloStatus.Stopping)));
            managers[relay].CurrentSnapshot = third;
            managers[root].CurrentSnapshot = second;
            Assert.True(await protocols[root].Publish(namespaces[root], DisseminationKey.Default, 2, token));
            await protocols[root].FlushPendingBroadcast(token);
            await protocols[relay].FlushPendingBroadcast(token);

            Assert.Equal(2, sent[root].Count);
            Assert.Equal(2, sent[relay].Count);
            var rootDelta = ReadMembershipDelta(serializer, Assert.Single(GetBroadcastValues(sent[root][1])).Value);
            var relayDelta = ReadMembershipDelta(serializer, Assert.Single(GetBroadcastValues(sent[relay][1])).Value);
            Assert.Equal((1L, 2L), (rootDelta.BaseVersion.Value, rootDelta.Version.Value));
            Assert.Equal((1L, 3L), (relayDelta.BaseVersion.Value, relayDelta.Version.Value));
            Assert.Equal(SiloStatus.Stopping, Assert.Single(relayDelta.UpdatedEntries).Status);
            AssertMembershipState(third, managers[child].CurrentSnapshot);
            Assert.Empty(sent[child]);
            Assert.All(transports.Values, transport => Assert.Empty(transport.AntiEntropyRequests));
        }
        finally
        {
            var canceled = new CancellationToken(canceled: true);
            try
            {
                await Task.WhenAll(protocols.Values.Select(protocol => protocol.StopAsync(canceled)))
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException) when (canceled.IsCancellationRequested)
            {
            }
        }
    }

    [Fact]
    public async Task MembershipDeltaRejectsIntermediateViewAndMergesTargetReplay()
    {
        var local = CreateSilo(45301);
        var inactive = CreateSilo(45302);
        var initial = CreateMembershipSnapshot(1,
            CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch),
            CreateMembershipEntry(inactive, SiloStatus.Dead, DateTime.UnixEpoch));
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer>();
        var manager = new FakeMembershipManager(new MembershipTableSnapshot(new MembershipVersion(2), initial.Entries));
        var ns = CreateMembershipNamespace(manager, serializer);
        var updated = initial.Entries[local].WithIAmAliveTime(DateTime.UnixEpoch.AddSeconds(10));
        var value = CreateMembershipDelta(serializer, 1, 3, [updated], [inactive]);

        Assert.Equal(DisseminationApplyResult.Rejected,
            await ns.ApplyValueAsync(value, TestContext.Current.CancellationToken));
        Assert.Equal(2, manager.CurrentSnapshot.Version.Value);
        Assert.Contains(inactive, manager.CurrentSnapshot.Entries.Keys);

        var canonical = initial.Entries[local].WithStatus(SiloStatus.ShuttingDown);
        canonical.HostName = "canonical";
        manager.CurrentSnapshot = CreateMembershipSnapshot(3, canonical);
        Assert.Equal(DisseminationApplyResult.Applied,
            await ns.ApplyValueAsync(value, TestContext.Current.CancellationToken));
        Assert.Equal(SiloStatus.ShuttingDown, manager.CurrentSnapshot.Entries[local].Status);
        Assert.Equal("canonical", manager.CurrentSnapshot.Entries[local].HostName);
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(10), manager.CurrentSnapshot.Entries[local].IAmAliveTime);
        Assert.DoesNotContain(inactive, manager.CurrentSnapshot.Entries.Keys);
        Assert.Equal(DisseminationApplyResult.Duplicate,
            await ns.ApplyValueAsync(value, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MembershipFullRepairMergesIncomparableCleanupAndHeartbeatState()
    {
        var local = CreateSilo(45401);
        var first = CreateSilo(45402);
        var second = CreateSilo(45403);
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer>();
        var sourceManager = new FakeMembershipManager(CreateMembershipSnapshot(7,
            CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch, DateTime.UnixEpoch.AddSeconds(30)),
            CreateMembershipEntry(first, SiloStatus.Dead, DateTime.UnixEpoch)));
        var receiverManager = new FakeMembershipManager(CreateMembershipSnapshot(7,
            CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch, DateTime.UnixEpoch.AddSeconds(40)),
            CreateMembershipEntry(second, SiloStatus.Dead, DateTime.UnixEpoch)));
        var source = CreateMembershipNamespace(sourceManager, serializer);
        var receiver = CreateMembershipNamespace(receiverManager, serializer);

        Assert.Equal(DisseminationApplyResult.Applied,
            await receiver.ApplyValueAsync(GetMembershipRepair(source, 7), TestContext.Current.CancellationToken));
        Assert.Equal(local, Assert.Single(receiverManager.CurrentSnapshot.Entries).Key);
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(40), receiverManager.CurrentSnapshot.Entries[local].IAmAliveTime);
        Assert.Equal(DisseminationApplyResult.Applied,
            await source.ApplyValueAsync(GetMembershipRepair(receiver, 7), TestContext.Current.CancellationToken));
        AssertMembershipState(receiverManager.CurrentSnapshot, sourceManager.CurrentSnapshot);
    }

    [Fact]
    public async Task MembershipDeltaDoesNotAcknowledgeUnrelatedOwnerRefresh()
    {
        var local = CreateSilo(45501);
        var initial = CreateMembershipSnapshot(1, CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch));
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer>();
        var manager = new FakeMembershipManager(initial);
        manager.ProcessGossipSnapshotHandler = (_, _) =>
        {
            manager.CurrentSnapshot = new(new MembershipVersion(2), initial.Entries);
            return Task.CompletedTask;
        };
        var ns = CreateMembershipNamespace(manager, serializer);
        var value = CreateMembershipDelta(serializer, 1, 2,
            [initial.Entries[local].WithIAmAliveTime(DateTime.UnixEpoch.AddSeconds(10))], []);

        Assert.Equal(DisseminationApplyResult.Rejected,
            await ns.ApplyValueAsync(value, TestContext.Current.CancellationToken));
        Assert.Equal(DateTime.UnixEpoch, manager.CurrentSnapshot.Entries[local].IAmAliveTime);

        var peer = CreateSilo(45502);
        var protocol = CreateProtocol(new FakeTransport(local, peer), [ns]);
        try
        {
            var response = await protocol.ReceiveBroadcast(new()
            {
                Sender = peer,
                SupportsCompactAcknowledgments = true,
                Values = new() { [ns.Name] = [new() { Value = value, TimeToLive = TimeSpan.FromSeconds(30) }] },
            }, TestContext.Current.CancellationToken);
            Assert.False(response.AllVersionsAcknowledged);
            Assert.Equal(2, Assert.Single(response.Acknowledgments[ns.Name]).Version);
        }
        finally
        {
            await protocol.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task MembershipAntiEntropyRejectsDeltaResponses()
    {
        var local = CreateSilo(45701);
        var peer = CreateSilo(45702);
        var initial = CreateMembershipSnapshot(1,
            CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch),
            CreateMembershipEntry(peer, SiloStatus.Active, DateTime.UnixEpoch));
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer>();
        var manager = new FakeMembershipManager(initial);
        var applied = 0;
        manager.ProcessGossipSnapshotHandler = (_, _) =>
        {
            applied++;
            return Task.CompletedTask;
        };
        var ns = CreateMembershipNamespace(manager, serializer);
        var delta = CreateMembershipDelta(serializer, 1, 2,
            [initial.Entries[peer].WithIAmAliveTime(DateTime.UnixEpoch.AddSeconds(10))], []);
        var transport = new FakeTransport(local, peer);
        transport.ExchangeAntiEntropyHandler = (_, _, _) => ValueTask.FromResult(new DisseminationAntiEntropyResponse
        {
            Sender = peer,
            Values = new() { [ns.Name] = [new() { Value = delta, TimeToLive = TimeSpan.FromSeconds(30) }] },
        });
        var protocol = CreateProtocol(transport, [ns]);
        try
        {
            await protocol.RunAntiEntropyRound(TestContext.Current.CancellationToken);

            Assert.Single(transport.AntiEntropyRequests);
            Assert.Equal(0, applied);
            Assert.Same(initial, manager.CurrentSnapshot);
        }
        finally
        {
            await protocol.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    private static MembershipTableSnapshotDelta ReadMembershipDelta(Serializer serializer, DisseminationValue value)
    {
        var update = Assert.IsType<MembershipTableSnapshotUpdate>(serializer.Deserialize<MembershipTableSnapshotUpdate>(value.Payload));
        Assert.Null(update.Snapshot);
        var delta = Assert.IsType<MembershipTableSnapshotDelta>(update.Delta);
        Assert.Equal(value.FromVersion, delta.BaseVersion.Value);
        Assert.Equal(value.ToVersion, delta.Version.Value);
        return delta;
    }

    private static DisseminationValue CreateMembershipDelta(
        Serializer serializer,
        long from,
        long to,
        ImmutableArray<MembershipEntry> entries,
        ImmutableArray<SiloAddress> removed) =>
        new(DisseminationKey.Default, from, to, serializer.SerializeToArray(new MembershipTableSnapshotUpdate
        {
            Delta = new(new MembershipVersion(from), new MembershipVersion(to), entries, removed),
        }));

    private sealed class MembershipDeltaLink : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly SiloAddress _peer;
        private readonly DisseminationProtocol _targetProtocol;

        public MembershipDeltaLink(
            SiloAddress local,
            SiloAddress peer,
            MembershipTableSnapshot source,
            MembershipTableSnapshot target)
        {
            _peer = peer;
            _services = new ServiceCollection().AddSerializer().BuildServiceProvider();
            Serializer = _services.GetRequiredService<Serializer>();
            SourceManager = new(source);
            TargetManager = new(target);
            Source = CreateMembershipNamespace(SourceManager, Serializer);
            Target = CreateMembershipNamespace(TargetManager, Serializer);
            var transport = new FakeTransport(local, peer);
            SourceProtocol = CreateProtocol(transport, [Source], timeProvider: Clock);
            _targetProtocol = CreateProtocol(new FakeTransport(peer, local), [Target], timeProvider: Clock);
            transport.SendBroadcastResponseHandler = async (_, batch, token) =>
            {
                Batches.Add(batch);
                var response = await _targetProtocol.ReceiveBroadcast(batch, token);
                Responses.Add(response);
                if (BeforeResponse is { } before)
                {
                    await before(batch, Batches.Count, token);
                }

                return response;
            };
            Queue = CreateBroadcastQueue(transport, [Source], timeProvider: Clock);
        }

        public Serializer Serializer { get; }
        public FakeTimeProvider Clock { get; } = new();
        public FakeMembershipManager SourceManager { get; }
        public FakeMembershipManager TargetManager { get; }
        public MembershipDisseminationNamespace Source { get; }
        public MembershipDisseminationNamespace Target { get; }
        public DisseminationProtocol SourceProtocol { get; }
        public DisseminationBroadcastQueue Queue { get; }
        public List<DisseminationBroadcastBatch> Batches { get; } = [];
        public List<DisseminationBroadcastResponse> Responses { get; } = [];
        public Func<DisseminationBroadcastBatch, int, CancellationToken, Task>? BeforeResponse { get; set; }

        public async Task Send()
        {
            var token = TestContext.Current.CancellationToken;
            var flush = BeforeBroadcastPumpsRun(() =>
            {
                Assert.True(Queue.Notify(_peer, Source, DisseminationKey.Default));
                return Queue.FlushPendingBroadcast(token);
            });
            await flush.WaitAsync(TimeSpan.FromSeconds(5), token);
        }

        public async ValueTask DisposeAsync()
        {
            var token = new CancellationToken(canceled: true);
            var stops = new[]
            {
                Queue.StopAsync(token),
                SourceProtocol.StopAsync(token),
                _targetProtocol.StopAsync(token),
            };
            try
            {
                await Task.WhenAll(stops).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            finally
            {
                _services.Dispose();
            }
        }
    }
}
