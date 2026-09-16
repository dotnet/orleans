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
    public void AggregationTopologyHasLinearSymmetricEdges(int count, int fanout)
    {
        var members = CreateSilos(count).ToImmutableArray();
        var snapshots = members.ToDictionary(
            silo => silo,
            silo => new DisseminationMembershipSnapshot(new MembershipVersion(1), silo, members, CreateOverlayOptions(fanout)));
        Assert.Equal(2 * (count - 1), snapshots.Values.Sum(snapshot => snapshot.AggregationTreeTargets.Length));
        foreach (var (silo, snapshot) in snapshots)
        {
            Assert.DoesNotContain(silo, snapshot.AggregationTreeTargets);
            Assert.Equal(snapshot.AggregationTreeTargets.Length, snapshot.AggregationTreeTargets.Distinct().Count());
            Assert.InRange(snapshot.AggregationTreeTargets.Length, 0, fanout + 1);
            foreach (var peer in snapshot.AggregationTreeTargets)
            {
                Assert.Contains(silo, snapshots[peer].AggregationTreeTargets);
            }
        }

        var reached = new HashSet<SiloAddress> { members[0] };
        var pending = new Queue<SiloAddress>(reached);
        while (pending.TryDequeue(out var silo))
        {
            foreach (var peer in snapshots[silo].AggregationTreeTargets)
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
            var ns = new FakeNamespace(member) { RoutingMode = DisseminationRoutingMode.AggregationTree };
            ns.Options.MaxCoalescingDelay = TimeSpan.FromHours(1);
            var transport = new FakeTransport(member, members.Where(peer => !peer.Equals(member)).ToArray());
            var protocol = CreateProtocol(transport, ns, options =>
            {
                options.Overlay.FanOutFactor = _ => fanout;
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

                // Explicit phases isolate aggregation from timer phase/order: gather, then distribute.
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
                Assert.InRange(batches, count - 1, 4 * (count - 1));
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

    [Fact]
    public async Task AggregationWindowBatchesStaggeredKeysAndPreservesNextWindow()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var local = CreateSilo(40801);
        var peer = CreateSilo(40802);
        var clock = new FakeTimeProvider();
        var ns = new FakeNamespace(local) { RoutingMode = DisseminationRoutingMode.AggregationTree };
        ns.Options.MaxCoalescingDelay = new DeploymentLoadPublisherOptions().Dissemination.MaxCoalescingDelay;
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
                Assert.True(queue.Notify(peer, ns, $"key-{key}"));
                clock.Advance(TimeSpan.FromMilliseconds(1));
            }
            clock.Advance(TimeSpan.FromMilliseconds(149));
            Assert.Empty(transport.BroadcastBatches);
            clock.Advance(TimeSpan.FromMilliseconds(1));
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            var firstFlush = queue.FlushPendingBroadcast(cancellationToken);
            Assert.Equal(100, GetBroadcastValues(Assert.Single(transport.BroadcastBatches).Batch).Count());
            Assert.Equal(TimeSpan.FromMilliseconds(250), clock.GetElapsedTime(start, timestamps[0]));

            for (var key = 0; key < 100; key++)
            {
                ns.SetValue($"key-{key}", 2);
                Assert.True(queue.Notify(peer, ns, $"key-{key}"));
                clock.Advance(TimeSpan.FromMilliseconds(1));
            }
            releaseFirst.SetResult();
            await firstFlush.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            clock.Advance(TimeSpan.FromMilliseconds(149));
            Assert.Single(transport.BroadcastBatches);
            clock.Advance(TimeSpan.FromMilliseconds(1));
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

            Assert.Equal(TimeSpan.FromMilliseconds(500), clock.GetElapsedTime(start, timestamps[1]));
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
