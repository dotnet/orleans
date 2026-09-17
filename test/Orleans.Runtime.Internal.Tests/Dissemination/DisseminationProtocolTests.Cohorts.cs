using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Dissemination;
using Orleans.Serialization;
using Xunit;

namespace UnitTests.Dissemination;

public partial class DisseminationProtocolTests
{
    [Fact]
    public async Task HeldCohortReceiptKeepsMembershipPeerAdmissionAvailable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var peer = CreateSilo(41301);
        var local = CreateSilo(41302);
        var load = CreateCohortNamespace(local);
        var membership = new FakeNamespace(local, new DisseminationNamespace("urgent-membership"));
        var transport = new FakeTransport(local, peer);
        var ingressStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heldReceipt = new TaskCompletionSource<DisseminationPublicationReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
        var membershipSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.PublishAggregatedHandler = (_, _, _) =>
        {
            ingressStarted.TrySetResult();
            return heldReceipt.Task;
        };
        transport.SendBroadcastResponseHandler = (target, batch, _) =>
        {
            Assert.Equal(peer, target);
            Assert.Equal(membership.Name, Assert.Single(batch.Values.Keys));
            membershipSent.TrySetResult();
            return Task.FromResult(FakeTransport.CreateAcknowledgment(batch));
        };
        var protocol = CreateProtocol(transport, [load, membership], options => options.MaxConcurrentSends = 1);
        try
        {
            load.SetValue(local, 1);
            var publication = protocol.PublishAggregated(load, local, 1, cancellationToken).AsTask();
            await ingressStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            membership.SetValue("membership", 1);
            Assert.True(await protocol.Publish(membership, "membership", 1, cancellationToken));
            await membershipSent.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.False(publication.IsCompleted);
            heldReceipt.SetResult(new(true, TimeSpan.FromMilliseconds(750)));
            Assert.Equal(new DisseminationPublicationReceipt(true, TimeSpan.FromMilliseconds(750)), await publication);
        }
        finally
        {
            heldReceipt.TrySetResult(default);
            load.Options.Enabled = false;
            membership.Options.Enabled = false;
            await protocol.StopAsync(cancellationToken);
        }
    }

    [Theory]
    [InlineData(4)]
    [InlineData(10)]
    public async Task CompleteCohortIncludesRootAndUsesOneIngressAndDistributionTree(int count)
    {
        var token = TestContext.Current.CancellationToken;
        var members = CreateSilos(count);
        var clock = new FakeTimeProvider();
        var startedAt = clock.GetTimestamp();
        var namespaces = members.Select(CreateCohortNamespace).ToArray();
        var transports = members.Select(local => new FakeTransport(local, members.Where(peer => !peer.Equals(local)).ToArray())).ToArray();
        var protocols = transports.Select((transport, index) => CreateProtocol(
            transport, namespaces[index], options => options.Overlay.AggregationFanOutFactor = 2, clock)).ToArray();
        for (var index = 0; index < count; index++)
        {
            transports[index].PublishAggregatedHandler = (peer, request, cancellationToken) =>
            {
                Assert.Equal(members[0], peer);
                return protocols[0].ReceivePublication(request, cancellationToken);
            };
            var source = transports[index];
            source.SendBroadcastResponseHandler = async (peer, batch, cancellationToken) =>
            {
                lock (source.BroadcastBatches)
                {
                    source.BroadcastBatches.Add((peer, batch));
                }
                return await protocols[Array.IndexOf(members, peer)].ReceiveBroadcast(batch, cancellationToken);
            };
            namespaces[index].SetValue(members[index], 1);
        }

        try
        {
            var remoteReceipts = Enumerable.Range(1, count - 1)
                .Select(index => protocols[index].PublishAggregated(namespaces[index], members[index], 1, token).AsTask())
                .ToArray();
            Assert.All(remoteReceipts, receipt => Assert.False(receipt.IsCompleted));
            var localReceipt = protocols[0].PublishAggregated(namespaces[0], members[0], 1, token).AsTask();
            var receipts = await Task.WhenAll(remoteReceipts.Append(localReceipt)).WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.All(receipts, receipt =>
            {
                Assert.True(receipt.Accepted);
                Assert.Equal(TimeSpan.FromSeconds(1), receipt.NextPublicationDelay);
            });
            for (var index = 0; index < count; index++)
            {
                await protocols[index].FlushPendingBroadcast(token);
            }

            Assert.Equal(count - 1, transports.Sum(transport => transport.PublicationRequests.Count));
            Assert.Equal(count - 1, transports.Sum(transport => transport.BroadcastBatches.Count));
            Assert.All(namespaces, ns => Assert.All(members, member => Assert.Equal(1, ns.GetVersion(member))));
            Assert.Equal(TimeSpan.Zero, clock.GetElapsedTime(startedAt));
        }
        finally
        {
            foreach (var ns in namespaces)
            {
                ns.Options.Enabled = false;
            }
            await Task.WhenAll(protocols.Select(protocol => protocol.StopAsync(token)));
        }
    }

    [Fact]
    public async Task StoppingRootSealsHeldIngressBeforeDrainingProtocolAdmissions()
    {
        var token = TestContext.Current.CancellationToken;
        var local = CreateSilo(41401);
        var peer = CreateSilo(41402);
        var ns = CreateCohortNamespace(local);
        var clock = new FakeTimeProvider();
        var startedAt = clock.GetTimestamp();
        var protocol = CreateProtocol(new FakeTransport(local, peer), ns, timeProvider: clock);
        try
        {
            var request = new DisseminationPublicationRequest { Sender = peer, Namespace = ns.Name, Value = ns.CreateItem(peer, peer, 1) };
            var receipt = protocol.ReceivePublication(request, token);
            Assert.False(receipt.IsCompleted);
            Assert.Equal(1, ns.GetVersion(peer));
            await protocol.StopAsync(token).WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.True((await receipt).Accepted);
            Assert.Equal(TimeSpan.Zero, clock.GetElapsedTime(startedAt));
        }
        finally
        {
            await protocol.StopAsync(token);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledPublicationReleasesLocalAttemptBeforeNonCooperativeRpcReturns(bool stopProtocol)
    {
        var token = TestContext.Current.CancellationToken;
        var root = CreateSilo(41411);
        var local = CreateSilo(41412);
        var ns = CreateCohortNamespace(local);
        var transport = new FakeTransport(local, root);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<DisseminationPublicationReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.PublishAggregatedHandler = (_, request, _) =>
        {
            started.TrySetResult();
            return request.Value.Value.ToVersion == 1 ? late.Task : Task.FromResult(new DisseminationPublicationReceipt(true, TimeSpan.FromSeconds(1)));
        };
        var protocol = CreateProtocol(transport, ns);
        try
        {
            using var cancellation = new CancellationTokenSource();
            ns.SetValue(local, 1);
            var first = protocol.PublishAggregated(ns, local, 1, cancellation.Token).AsTask();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            if (stopProtocol)
            {
                await protocol.StopAsync(token).WaitAsync(TimeSpan.FromSeconds(5), token);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
                Assert.False((await protocol.PublishAggregated(ns, local, 1, token)).Accepted);
            }
            else
            {
                cancellation.Cancel();
                var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
                Assert.Equal(cancellation.Token, exception.CancellationToken);
                ns.SetValue(local, 2);
                Assert.True((await protocol.PublishAggregated(ns, local, 2, token)).Accepted);
                Assert.Equal(2, transport.PublicationRequests.Count);
            }
            Assert.False(late.Task.IsCompleted);
        }
        finally
        {
            late.TrySetResult(default);
            await protocol.StopAsync(token);
        }
    }

    [Fact]
    public async Task PublicationToFormerRootRejectsBeforeOwnerMutation()
    {
        var token = TestContext.Current.CancellationToken;
        var currentRoot = CreateSilo(41431);
        var local = CreateSilo(41432);
        var producer = CreateSilo(41433);
        var ns = CreateCohortNamespace(local);
        var transport = new FakeTransport(local, currentRoot, producer);
        var protocol = CreateProtocol(transport, ns);
        try
        {
            var receipt = await protocol.ReceivePublication(new()
            {
                Sender = producer,
                Namespace = ns.Name,
                Value = ns.CreateItem(producer, producer, 1),
            }, token);
            Assert.False(receipt.Accepted);
            Assert.Equal(0, ns.GetVersion(producer));
            Assert.Empty(ns.ApplyCounts);
            Assert.Empty(transport.BroadcastBatches);
        }
        finally
        {
            await protocol.StopAsync(token);
        }
    }

    [Fact]
    public void CohortPublicationWireContractPreservesPayloadAndSchedulingReceipt()
    {
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer>();
        var producer = CreateSilo(41421);
        var ns = CreateCohortNamespace(producer);
        var request = new DisseminationPublicationRequest { Sender = producer, Namespace = ns.Name, Value = ns.CreateItem(producer, producer, 7) };
        var decoded = Assert.IsType<DisseminationPublicationRequest>(serializer.Deserialize<DisseminationPublicationRequest>(serializer.SerializeToArray(request)));
        Assert.Equal(request.Sender, decoded.Sender);
        Assert.Equal(request.Namespace, decoded.Namespace);
        Assert.Equal(request.Value.Value.Key, decoded.Value.Value.Key);
        Assert.Equal(7, decoded.Value.Value.ToVersion);
        Assert.Equal(request.Value.Value.Payload.ToArray(), decoded.Value.Value.Payload.ToArray());
        Assert.Equal(request.Value.TimeToLive, decoded.Value.TimeToLive);
        var receipt = new DisseminationPublicationReceipt(true, TimeSpan.FromMilliseconds(975));
        Assert.Equal(receipt, serializer.Deserialize<DisseminationPublicationReceipt>(serializer.SerializeToArray(receipt)));
    }

    private static FakeNamespace CreateCohortNamespace(SiloAddress local)
    {
        var result = new FakeNamespace(local)
        {
            RoutingMode = DisseminationRoutingMode.AggregationTree,
            MembershipScope = DisseminationMembershipScope.ActiveMembers,
        };
        return result;
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
        ns.AggregationPeriod = TimeSpan.FromMilliseconds(25);
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
        ns.AggregationPeriod = new DeploymentLoadPublisherOptions().DeploymentLoadPublisherRefreshTime;
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
