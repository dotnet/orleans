#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Dissemination;
using Xunit;

namespace UnitTests.Dissemination;

public partial class DisseminationProtocolTests
{
    [Theory]
    [InlineData(1, 4)]
    [InlineData(8, 4)]
    [InlineData(32, 6)]
    [InlineData(100, 10)]
    [InlineData(256, 4)]
    public void AggregationTopologyHasOneRootAndLinearDistributionEdges(int count, int fanout)
    {
        var members = CreateSilos(count).ToImmutableArray();
        var overlay = new DisseminationOverlayOptions { AggregationFanOutFactor = fanout };
        var snapshots = members.ToDictionary(
            silo => silo,
            silo => new DisseminationMembershipSnapshot(new MembershipVersion(1), silo, members, overlay));
        Assert.Equal(count - 1, snapshots.Values.Sum(snapshot => snapshot.AggregationChildren.Length));
        Assert.Equal(members.Skip(1), snapshots.Values.SelectMany(snapshot => snapshot.AggregationChildren).Order());
        foreach (var (silo, snapshot) in snapshots)
        {
            Assert.DoesNotContain(silo, snapshot.AggregationChildren);
            Assert.Equal(snapshot.AggregationChildren.Length, snapshot.AggregationChildren.Distinct().Count());
            Assert.InRange(snapshot.AggregationChildren.Length, 0, fanout);
            Assert.All(snapshot.AggregationChildren, child => Assert.True(child.CompareTo(silo) > 0));
            Assert.Equal(snapshot.AggregationChildren, snapshot.GetForwardingTargets(DisseminationRoutingMode.AggregationTree));
            if (snapshot.IsAggregationRoot)
            {
                Assert.Equal(members[0], silo);
                Assert.Equal(snapshot.AggregationChildren, snapshot.GetOriginatorTargets(DisseminationRoutingMode.AggregationTree));
            }
            else
            {
                Assert.Equal(members[0], Assert.Single(snapshot.GetOriginatorTargets(DisseminationRoutingMode.AggregationTree)));
            }
        }

        var reached = new HashSet<SiloAddress> { members[0] };
        var pending = new Queue<SiloAddress>(reached);
        while (pending.TryDequeue(out var silo))
        {
            foreach (var peer in snapshots[silo].AggregationChildren)
            {
                if (reached.Add(peer))
                {
                    pending.Enqueue(peer);
                }
            }
        }

        Assert.Equal(members.Order(), reached.Order());
    }

    [Theory]
    [InlineData(8, 4)]
    [InlineData(32, 6)]
    [InlineData(100, 10)]
    [InlineData(256, 4)]
    public async Task AggregationTreeDeliversWholeRoundsWithLinearBatchCount(int count, int fanout)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var members = CreateSilos(count);
        var clock = new FakeTimeProvider();
        var nodes = new Dictionary<SiloAddress, (FakeNamespace Namespace, FakeTransport Transport, DisseminationProtocol Protocol)>();
        foreach (var member in members)
        {
            var ns = new FakeNamespace(member)
            {
                RoutingMode = DisseminationRoutingMode.AggregationTree,
                MembershipScope = DisseminationMembershipScope.ActiveMembers,
            };
            ns.AggregationPeriod = TimeSpan.FromHours(1);
            var transport = new FakeTransport(member, members.Where(peer => !peer.Equals(member)).ToArray());
            var protocol = CreateProtocol(transport, ns, options =>
            {
                options.Overlay.AggregationFanOutFactor = fanout;
                options.MaxConcurrentSends = 1;
            }, clock);
            nodes.Add(member, (ns, transport, protocol));
            transport.SendBroadcastResponseHandler = (peer, batch, token) =>
            {
                lock (transport.BroadcastBatches)
                {
                    transport.BroadcastBatches.Add((peer, batch));
                }
                return nodes[peer].Protocol.ReceiveBroadcast(batch, token);
            };
        }

        try
        {
            for (var round = 1; round <= 3; round++)
            {
                var before = nodes.Values.Sum(node => node.Transport.BroadcastBatches.Count);
                foreach (var member in members)
                {
                    var node = nodes[member];
                    node.Namespace.SetValue(member, round);
                    Assert.True(await node.Protocol.Publish(node.Namespace, member, round, cancellationToken));
                }

                // Finish producer ingresses before flushing the root, then drain distribution in tree order.
                for (var index = members.Length - 1; index >= 0; index--)
                {
                    await nodes[members[index]].Protocol.FlushPendingBroadcast(cancellationToken);
                }
                foreach (var member in members)
                {
                    await nodes[member].Protocol.FlushPendingBroadcast(cancellationToken);
                }

                foreach (var node in nodes.Values)
                {
                    foreach (var origin in members)
                    {
                        Assert.Equal(round, node.Namespace.GetVersion(origin));
                    }
                }

                var batches = nodes.Values.Sum(node => node.Transport.BroadcastBatches.Count) - before;
                Assert.Equal(2 * (count - 1), batches);
                Assert.Empty(nodes.Values.SelectMany(node => node.Transport.AntiEntropyRequests));
            }
        }
        finally
        {
            foreach (var node in nodes.Values)
            {
                node.Namespace.Options.Enabled = false;
            }
            foreach (var node in nodes.Values)
            {
                await node.Protocol.StopAsync(cancellationToken);
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(14)]
    public async Task RootAggregationBatchesOnceAndCrossesMultipleLevelsWithoutAnotherDelay(int originIndex)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var members = CreateSilos(15);
        var root = members[0];
        var origin = members[originIndex];
        var clock = new FakeTimeProvider();
        var ingested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nodes = new Dictionary<SiloAddress, (FakeNamespace Namespace, FakeTransport Transport, DisseminationProtocol Protocol)>();
        foreach (var member in members)
        {
            var ns = new FakeNamespace(member)
            {
                RoutingMode = DisseminationRoutingMode.AggregationTree,
                MembershipScope = DisseminationMembershipScope.ActiveMembers,
                ApplyObserved = new(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            ns.AggregationPeriod = TimeSpan.FromMilliseconds(500);
            var transport = new FakeTransport(member, members.Where(peer => !peer.Equals(member)).ToArray());
            var protocol = CreateProtocol(transport, ns, options =>
            {
                options.Overlay.AggregationFanOutFactor = 2;
                options.MaxConcurrentSends = 1;
            }, clock);
            nodes.Add(member, (ns, transport, protocol));
            transport.SendBroadcastResponseHandler = async (peer, batch, token) =>
            {
                lock (transport.BroadcastBatches)
                {
                    transport.BroadcastBatches.Add((peer, batch));
                }
                var response = await nodes[peer].Protocol.ReceiveBroadcast(batch, token);
                if (member.Equals(origin) && peer.Equals(root))
                {
                    ingested.TrySetResult();
                }
                return response;
            };
        }

        try
        {
            var start = clock.GetTimestamp();
            var source = nodes[origin];
            source.Namespace.SetValue("value", 1);
            Assert.True(await source.Protocol.Publish(source.Namespace, "value", 1, cancellationToken));
            await ingested.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.Equal(1, nodes[root].Namespace.GetVersion("value"));
            Assert.Equal(1, nodes.Values.Sum(node => node.Transport.BroadcastBatches.Count));

            clock.Advance(TimeSpan.FromMilliseconds(499));
            Assert.All(members.Where(silo => !silo.Equals(root) && !silo.Equals(origin)),
                silo => Assert.Equal(0, nodes[silo].Namespace.GetVersion("value")));
            clock.Advance(TimeSpan.FromMilliseconds(1));
            await Task.WhenAll(members.Where(silo => !silo.Equals(origin))
                .Select(silo => nodes[silo].Namespace.ApplyObserved!.Task))
                .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

            Assert.All(nodes.Values, node => Assert.Equal(1, node.Namespace.GetVersion("value")));
            Assert.Equal(TimeSpan.FromMilliseconds(500), clock.GetElapsedTime(start));
        }
        finally
        {
            foreach (var node in nodes.Values)
            {
                node.Namespace.Options.Enabled = false;
            }
            foreach (var node in nodes.Values)
            {
                await node.Protocol.StopAsync(cancellationToken);
            }
        }
    }

    [Fact]
    public async Task MembershipBroadcastsDrainAheadOfAggregatedValues()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var local = CreateSilo(40821);
        var peer = CreateSilo(40822);
        var transport = new FakeTransport(local, peer);
        var membership = new FakeNamespace(local, "membership");
        var load = new FakeNamespace(local, "load", DisseminationMembershipScope.ActiveMembers)
        {
            RoutingMode = DisseminationRoutingMode.AggregationTree,
        };
        membership.SetValue("value", 1);
        load.SetValue("value", 2);
        var queue = CreateBroadcastQueue(transport, [load, membership], options => options.MaxBatchItems = 1);
        try
        {
            var accepted = BeforeBroadcastPumpsRun(() => (
                Load: queue.Notify(peer, load, "value"),
                Membership: queue.Notify(peer, membership, "value")));
            Assert.True(accepted.Load);
            Assert.True(accepted.Membership);
            await queue.FlushPendingBroadcast(cancellationToken);

            Assert.Equal(
                new[] { (membership.Name, 1L), (load.Name, 2L) },
                transport.BroadcastBatches.Select(batch => (
                    Assert.Single(batch.Batch.Values.Keys),
                    Assert.Single(GetBroadcastValues(batch.Batch)).Value.ToVersion)));
            Assert.All(transport.BroadcastBatches, batch => Assert.Equal(peer, batch.Peer));
        }
        finally
        {
            await queue.StopAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task AggregationDistributionRequiresBroadcastAcknowledgmentBeforeSuppressingKnownValue()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var members = CreateSilos(2);
        var ns = new FakeNamespace(members[0])
        {
            RoutingMode = DisseminationRoutingMode.AggregationTree,
            MembershipScope = DisseminationMembershipScope.ActiveMembers,
        };
        ns.AggregationPeriod = TimeSpan.FromMilliseconds(500);
        var transport = new FakeTransport(members[0], members[1]);
        var queue = CreateBroadcastQueue(transport, [ns], timeProvider: new FakeTimeProvider());
        try
        {
            ns.SetValue("value", 1);
            Assert.True(BeforeBroadcastPumpsRun(() =>
            {
                var accepted = queue.Notify(members[1], ns, "value", force: false);
                queue.ObservePeerVersion(members[1], ns.Name, "value", 1);
                Assert.Empty(transport.BroadcastBatches);
                return accepted;
            }));
            await queue.FlushPendingBroadcast(cancellationToken);

            var batch = Assert.Single(transport.BroadcastBatches).Batch;
            Assert.Equal(1, Assert.Single(GetBroadcastValues(batch)).Value.ToVersion);

            Assert.True(queue.Notify(members[1], ns, "value", force: false));
            await queue.FlushPendingBroadcast(cancellationToken);
            Assert.Single(transport.BroadcastBatches);
        }
        finally
        {
            ns.Options.Enabled = false;
            await queue.StopAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task FormerAggregationRootRedirectsIngressToCurrentRootImmediately()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var members = CreateSilos(7);
        var local = members[1];
        var sender = members[^1];
        var clock = new FakeTimeProvider();
        var ns = new FakeNamespace(local)
        {
            RoutingMode = DisseminationRoutingMode.AggregationTree,
            MembershipScope = DisseminationMembershipScope.ActiveMembers,
        };
        ns.AggregationPeriod = TimeSpan.FromMilliseconds(500);
        var transport = new FakeTransport(local, members.Where(peer => !peer.Equals(local)).ToArray());
        var forwarded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.SendBroadcastResponseHandler = (peer, batch, _) =>
        {
            transport.BroadcastBatches.Add((peer, batch));
            forwarded.TrySetResult();
            return Task.FromResult(FakeTransport.CreateAcknowledgment(batch));
        };
        var protocol = CreateProtocol(transport, ns, options => options.Overlay.AggregationFanOutFactor = 2, clock);
        var start = clock.GetTimestamp();
        try
        {
            await protocol.ReceiveBroadcast(new()
            {
                Sender = sender,
                Values = CreateValueGroups([ns.CreateItem(sender, sender, 1)]),
            }, cancellationToken);
            await forwarded.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            await protocol.FlushPendingBroadcast(cancellationToken);

            var sent = Assert.Single(transport.BroadcastBatches);
            Assert.Equal(members[0], sent.Peer);
            Assert.Equal(sender, Assert.Single(GetBroadcastValues(sent.Batch)).Value.Key.Value);
            Assert.Equal(TimeSpan.Zero, clock.GetElapsedTime(start));
        }
        finally
        {
            ns.Options.Enabled = false;
            await protocol.StopAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task AggregationRelayForwardsWholeBatchAtomicallyWithoutCoalescing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var members = CreateSilos(7);
        var local = members[1];
        var clock = new FakeTimeProvider();
        var ns = new FakeNamespace(local)
        {
            RoutingMode = DisseminationRoutingMode.AggregationTree,
            MembershipScope = DisseminationMembershipScope.ActiveMembers,
        };
        ns.AggregationPeriod = TimeSpan.FromMilliseconds(500);
        var transport = new FakeTransport(local, members.Where(peer => !peer.Equals(local)).ToArray());
        var forwarded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        transport.SendBroadcastResponseHandler = (peer, batch, _) =>
        {
            lock (transport.BroadcastBatches)
            {
                transport.BroadcastBatches.Add((peer, batch));
                count++;
                if (count == 2)
                {
                    forwarded.TrySetResult();
                }
            }
            return Task.FromResult(FakeTransport.CreateAcknowledgment(batch));
        };
        var protocol = CreateProtocol(transport, ns, options => options.Overlay.AggregationFanOutFactor = 2, clock);
        var values = Enumerable.Range(0, 64).Select(index => ns.CreateItem(members[0], $"key-{index}", 1)).ToArray();
        var start = clock.GetTimestamp();
        try
        {
            await protocol.ReceiveBroadcast(new()
            {
                Sender = members[0],
                Values = CreateValueGroups(values),
            }, cancellationToken);
            await forwarded.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            await protocol.FlushPendingBroadcast(cancellationToken);

            Assert.Equal(TimeSpan.Zero, clock.GetElapsedTime(start));
            Assert.Equal(new[] { members[3], members[4] }, transport.BroadcastBatches.Select(batch => batch.Peer).Order());
            Assert.All(transport.BroadcastBatches, batch =>
            {
                Assert.Equal(64, GetBroadcastValues(batch.Batch).Count());
                Assert.All(GetBroadcastValues(batch.Batch), value => Assert.Equal(1, value.Value.ToVersion));
            });
        }
        finally
        {
            ns.Options.Enabled = false;
            await protocol.StopAsync(cancellationToken);
        }
    }

    [Fact]
    public void RootAggregationRequiresAddressOrderedActiveMembership()
    {
        var local = CreateSilo(40811);
        var ns = new FakeNamespace(local) { RoutingMode = DisseminationRoutingMode.AggregationTree };

        var exception = Assert.Throws<ArgumentException>(() => CreateProtocol(new FakeTransport(local), ns));

        Assert.Equal("disseminationNamespaces", exception.ParamName);
    }

    [Fact]
    public async Task QueueBatchesAcceptedUpdatesAcrossInFlightSend()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var local = CreateSilo(40801);
        var peer = CreateSilo(40802);
        var clock = new FakeTimeProvider();
        var ns = new FakeNamespace(local)
        {
            RoutingMode = DisseminationRoutingMode.AggregationTree,
            MembershipScope = DisseminationMembershipScope.ActiveMembers,
        };
        var transport = new FakeTransport(local, peer);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timestamps = new List<long>();
        transport.SendBroadcastResponseHandler = async (_, batch, _) =>
        {
            lock (transport.BroadcastBatches)
            {
                transport.BroadcastBatches.Add((peer, batch));
            }
            timestamps.Add(clock.GetTimestamp());
            if (timestamps.Count == 1)
            {
                firstStarted.SetResult();
                await releaseFirst.Task;
            }
            else
            {
                secondStarted.TrySetResult();
            }
            return FakeTransport.CreateAcknowledgment(batch);
        };
        var queue = CreateBroadcastQueue(transport, [ns], timeProvider: clock);
        try
        {
            var start = clock.GetTimestamp();
            for (var key = 0; key < 100; key++)
            {
                ns.SetValue($"key-{key}", 1);
            }
            Assert.True(queue.NotifyBatch(peer, ns,
                [.. Enumerable.Range(0, 100).Select(key => new DisseminationBroadcastQueue.KeyNotification($"key-{key}", 1, true))]));
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            var firstFlush = queue.FlushPendingBroadcast(cancellationToken);
            Assert.Equal(100, GetBroadcastValues(Assert.Single(transport.BroadcastBatches).Batch).Count());
            Assert.Equal(TimeSpan.Zero, clock.GetElapsedTime(start, timestamps[0]));
            Assert.All(GetBroadcastValues(transport.BroadcastBatches[0].Batch), value => Assert.Equal(1, value.Value.ToVersion));

            for (var key = 0; key < 100; key++)
            {
                ns.SetValue($"key-{key}", 2);
            }
            Assert.True(queue.NotifyBatch(peer, ns,
                [.. Enumerable.Range(0, 100).Select(key => new DisseminationBroadcastQueue.KeyNotification($"key-{key}", 2, true))]));
            Assert.Single(transport.BroadcastBatches);
            releaseFirst.SetResult();
            await firstFlush.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

            Assert.Equal(TimeSpan.Zero, clock.GetElapsedTime(start, timestamps[1]));
            Assert.Equal(2, transport.BroadcastBatches.Count);
            Assert.Equal(100, GetBroadcastValues(transport.BroadcastBatches[1].Batch).Count());
            Assert.All(GetBroadcastValues(transport.BroadcastBatches[1].Batch), value => Assert.Equal(2, value.Value.ToVersion));
        }
        finally
        {
            releaseFirst.TrySetResult();
            ns.Options.Enabled = false;
            await queue.StopAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }
    }
}
