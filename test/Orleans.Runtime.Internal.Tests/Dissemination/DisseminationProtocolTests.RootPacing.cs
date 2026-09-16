using Microsoft.Extensions.Time.Testing;
using Orleans.Configuration;
using Orleans.Runtime.Dissemination;
using Xunit;

namespace UnitTests.Dissemination;

public partial class DisseminationProtocolTests
{
    [Fact]
    public async Task MembershipTrafficCannotFlushUnadmittedRootLoadUpdates()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var local = CreateSilo(41301);
        var peer = CreateSilo(41302);
        var clock = new FakeTimeProvider();
        var load = new FakeNamespace(local, new DisseminationNamespace("paced-load"))
        {
            RoutingMode = DisseminationRoutingMode.AggregationTree,
            MembershipScope = DisseminationMembershipScope.ActiveMembers,
        };
        load.Options.MaxCoalescingDelay = TimeSpan.FromMilliseconds(25);
        var membership = new FakeNamespace(local, new DisseminationNamespace("urgent-membership"));
        membership.Options.Priority = DisseminationPriority.High;
        var transport = new FakeTransport(local, peer);
        var firstLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstMembership = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondMembership = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.SendBroadcastResponseHandler = (target, batch, _) =>
        {
            lock (transport.BroadcastBatches)
            {
                transport.BroadcastBatches.Add((target, batch));
            }
            foreach (var value in GetBroadcastValues(batch))
            {
                if (value.Value.Key == new DisseminationKey("load"))
                {
                    (value.Value.ToVersion == 1 ? firstLoad : secondLoad).TrySetResult();
                }
                else
                {
                    (value.Value.ToVersion == 1 ? firstMembership : secondMembership).TrySetResult();
                }
            }
            return Task.FromResult(FakeTransport.CreateAcknowledgment(batch));
        };
        var protocol = CreateProtocol(transport, [load, membership], timeProvider: clock);
        try
        {
            load.SetValue("load", 1);
            Assert.True(await protocol.Publish(load, "load", 1, cancellationToken));
            membership.SetValue("membership", 1);
            Assert.True(await protocol.Publish(membership, "membership", 1, cancellationToken));
            clock.Advance(TimeSpan.Zero);
            await firstMembership.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.False(firstLoad.Task.IsCompleted);
            clock.Advance(TimeSpan.FromMilliseconds(24));
            Assert.False(firstLoad.Task.IsCompleted);
            clock.Advance(TimeSpan.FromMilliseconds(1));
            await firstLoad.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

            load.SetValue("load", 2);
            Assert.True(await protocol.Publish(load, "load", 2, cancellationToken));
            membership.SetValue("membership", 2);
            Assert.True(await protocol.Publish(membership, "membership", 2, cancellationToken));
            clock.Advance(TimeSpan.Zero);
            await secondMembership.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.False(secondLoad.Task.IsCompleted);
            clock.Advance(TimeSpan.FromMilliseconds(199));
            Assert.False(secondLoad.Task.IsCompleted);
            clock.Advance(TimeSpan.FromMilliseconds(1));
            await secondLoad.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }
        finally
        {
            load.Options.Enabled = false;
            membership.Options.Enabled = false;
            await protocol.StopAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task RootPendingStateHandsOffImmediatelyWhenTheRootChanges()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var newRoot = CreateSilo(41311);
        var local = CreateSilo(41312);
        var peer = CreateSilo(41313);
        var clock = new FakeTimeProvider();
        var ns = new FakeNamespace(local)
        {
            RoutingMode = DisseminationRoutingMode.AggregationTree,
            MembershipScope = DisseminationMembershipScope.ActiveMembers,
        };
        ns.Options.MaxCoalescingDelay = TimeSpan.FromMilliseconds(25);
        var transport = new FakeTransport(local, peer);
        var handedOff = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.SendBroadcastResponseHandler = (target, batch, _) =>
        {
            Assert.Equal(newRoot, target);
            if (GetBroadcastValues(batch).Any(value => value.Value.Key == new DisseminationKey("pending")))
            {
                handedOff.TrySetResult();
            }
            return Task.FromResult(FakeTransport.CreateAcknowledgment(batch));
        };
        var protocol = CreateProtocol(transport, ns, timeProvider: clock);
        var start = clock.GetTimestamp();
        try
        {
            ns.SetValue("pending", 1);
            Assert.True(await protocol.Publish(ns, "pending", 1, cancellationToken));
            transport.Peers.Add(newRoot);
            ns.SetValue("new", 1);
            Assert.True(await protocol.Publish(ns, "new", 1, cancellationToken));
            clock.Advance(TimeSpan.Zero);
            await handedOff.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.Equal(TimeSpan.Zero, clock.GetElapsedTime(start));
        }
        finally
        {
            ns.Options.Enabled = false;
            await protocol.StopAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task RootAcceptsAndDistributesACompleteTwoThousandSiloInventory()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var members = CreateSilos(2000);
        var local = members[0];
        var ns = new FakeNamespace(local)
        {
            RoutingMode = DisseminationRoutingMode.AggregationTree,
            MembershipScope = DisseminationMembershipScope.ActiveMembers,
        };
        var defaults = new DeploymentLoadPublisherOptions().Dissemination;
        ns.Options.MaxPendingItemCount = defaults.MaxPendingItemCount;
        ns.Options.MaxCoalescingDelay = defaults.MaxCoalescingDelay;
        var transport = new FakeTransport(local, members[1..]);
        var protocol = CreateProtocol(transport, ns, timeProvider: new FakeTimeProvider());
        try
        {
            var values = members.Select(member => ns.CreateItem(member, member, 1)).ToArray();
            var response = await protocol.ReceiveBroadcast(new()
            {
                Sender = members[1],
                Values = CreateValueGroups(values),
            }, cancellationToken);
            Assert.Equal(members.Length, response.Acknowledgments[ns.Name].Count);
            await protocol.FlushPendingBroadcast(cancellationToken);

            Assert.Equal(8, transport.BroadcastBatches.Count);
            Assert.Equal(members[1..9], transport.BroadcastBatches.Select(batch => batch.Peer).Order());
            Assert.All(transport.BroadcastBatches, batch =>
            {
                Assert.Equal(members.Length, GetBroadcastValues(batch.Batch).Count());
                Assert.All(GetBroadcastValues(batch.Batch), value => Assert.Equal(1, value.Value.ToVersion));
            });
        }
        finally
        {
            ns.Options.Enabled = false;
            await protocol.StopAsync(cancellationToken);
        }
    }
}
