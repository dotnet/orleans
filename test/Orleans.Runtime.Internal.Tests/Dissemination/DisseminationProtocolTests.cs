#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using CsCheck;
using Microsoft.Accordant;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Configuration;
using Orleans.Metadata;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Runtime.Diagnostics;
using Orleans.Runtime.Dissemination;
using Orleans.Runtime.MembershipService;
using Orleans.Serialization;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;
using Xunit;

namespace UnitTests.Dissemination;

[TestCategory("BVT"), TestCategory("Dissemination")]
public class DisseminationProtocolTests
{
    [Fact]
    public async Task PublishSendsGossipToDeterministicTreeChildren()
    {
        var local = CreateSilo(11111);
        var peers = Enumerable.Range(11112, 6).Select(CreateSilo).ToArray();
        var transport = new FakeTransport(local, peers);
        var topic = new FakeTopic(local);
        var protocol = CreateProtocol(transport, topic, options => options.Overlay.FanOutFactor = static _ => 2);
        var item = topic.CreateItem(local, FakeTopic.DefaultKey, sequence: 1);

        var result = await protocol.Publish(topic.Name, item, peers, CancellationToken.None);
        await protocol.FlushPendingGossip(CancellationToken.None);

        Assert.True(result);
        var expectedChildren = GetOriginatorTreeTargets(local, peers, fanout: 2);
        Assert.Equal(expectedChildren, transport.GossipBatches.Select(batch => batch.Peer));
        Assert.All(transport.GossipBatches, batch => Assert.Equal(item.Digest, GetGossipValues(batch.Batch).Single().Digest));
    }

    [Fact]
    public async Task PublishQueuesTreeGossipWithoutCapabilityProbing()
    {
        var local = CreateSilo(11111);
        var peers = Enumerable.Range(11112, 6).Select(CreateSilo).ToArray();
        var transport = new FakeTransport(local, peers);
        var topic = new FakeTopic(local);
        var protocol = CreateProtocol(transport, topic, options => options.Overlay.FanOutFactor = static _ => 2);
        var item = topic.CreateItem(local, FakeTopic.DefaultKey, sequence: 1);

        var result = await protocol.Publish(topic.Name, item, peers, CancellationToken.None);
        await protocol.FlushPendingGossip(CancellationToken.None);

        Assert.True(result);
        Assert.Equal(GetOriginatorTreeTargets(local, peers, fanout: 2), transport.GossipBatches.Select(batch => batch.Peer));
    }

    [Fact]
    public async Task PublishRejectsInvalidValuesBeforeQueueing()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var topic = new FakeTopic(local);
        var protocol = CreateProtocol(transport, topic);
        topic.SetValue("obsolete", version: 10);

        var expired = new DisseminationValue
        {
            Digest = new DisseminationTopicDigest("expired", version: 1),
            Root = local,
            ExpiresAt = DateTimeOffset.UnixEpoch,
            Payload = BitConverter.GetBytes(1L),
        };
        var obsolete = topic.CreateItem(local, "obsolete", sequence: 5);

        Assert.False(await protocol.Publish(topic.Name, expired, [peer], CancellationToken.None));
        Assert.False(await protocol.Publish(topic.Name, obsolete, [peer], CancellationToken.None));
        await protocol.FlushPendingGossip(CancellationToken.None);

        Assert.Empty(transport.GossipBatches);
        Assert.Equal(
            new[] { expired.Digest, obsolete.Digest },
            topic.FallbackDigests);
    }

    [Fact]
    public async Task PublishAttemptsJoiningParticipantAndReliesOnSendBackoff()
    {
        var local = CreateSilo(11111);
        var joining = CreateSilo(11112);
        var active = CreateSilo(11113);
        var transport = new FakeTransport(local, joining, active);
        transport.PeerStatuses[joining] = SiloStatus.Joining;
        transport.SendGossipHandler = (target, batch, cancellationToken) =>
        {
            if (Equals(target, joining))
            {
                throw new InvalidOperationException("joining peer is not yet reachable");
            }

            transport.GossipBatches.Add((target, batch));
            return Task.CompletedTask;
        };

        var topic = new FakeTopic(local)
        {
            MembershipScope = DisseminationMembershipScope.AllMembers,
        };
        var protocol = CreateProtocol(transport, topic, options =>
        {
            options.FailureBackoff = TimeSpan.FromSeconds(5);
            options.Overlay.FanOutFactor = static _ => 2;
        });
        var value = topic.CreateItem(local, FakeTopic.DefaultKey, sequence: 1);

        var result = await protocol.Publish(topic.Name, value, targetPeers: null, CancellationToken.None);
        await protocol.FlushPendingGossip(CancellationToken.None);

        Assert.True(result);
        Assert.Equal(new[] { active }, transport.GossipBatches.Select(batch => batch.Peer));
    }

    [Fact]
    public async Task SendFailureUsesFailureBackoff()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var timeProvider = new TestTimeProvider();
        var sendCount = 0;
        transport.SendGossipHandler = (target, batch, cancellationToken) =>
        {
            if (Interlocked.Increment(ref sendCount) == 1)
            {
                timeProvider.Advance(TimeSpan.FromSeconds(10));
                throw new InvalidOperationException("transient send failure");
            }

            transport.GossipBatches.Add((target, batch));
            return Task.CompletedTask;
        };

        var topic = new FakeTopic(local);
        var protocol = CreateProtocol(transport, topic, options =>
        {
            options.FailureBackoff = TimeSpan.FromSeconds(5);
        }, timeProvider);
        var item = topic.CreateItem(local, FakeTopic.DefaultKey, sequence: 1);

        var firstResult = await protocol.Publish(topic.Name, item, new[] { peer }, CancellationToken.None);
        await protocol.FlushPendingGossip(CancellationToken.None);
        var secondResult = await protocol.Publish(topic.Name, item, new[] { peer }, CancellationToken.None);
        await protocol.FlushPendingGossip(CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromSeconds(5));
        var thirdResult = await protocol.Publish(topic.Name, item, new[] { peer }, CancellationToken.None);
        await protocol.FlushPendingGossip(CancellationToken.None);

        Assert.True(firstResult);
        Assert.True(secondResult);
        Assert.True(thirdResult);
        Assert.Equal(2, sendCount);
        Assert.Single(transport.GossipBatches);
    }

    [Fact]
    public async Task MembershipRefreshPrunesFailureBackoffForRemovedPeers()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var sendCount = 0;
        transport.SendGossipHandler = (target, batch, cancellationToken) =>
        {
            if (++sendCount == 1)
            {
                throw new InvalidOperationException("peer failed before removal");
            }

            transport.GossipBatches.Add((target, batch));
            return Task.CompletedTask;
        };

        var topic = new FakeTopic(local);
        var protocol = CreateProtocol(transport, topic, options =>
        {
            options.FailureBackoff = TimeSpan.FromMinutes(1);
            options.Overlay.FanOutFactor = static _ => 1;
        }, new TestTimeProvider());

        Assert.True(await protocol.Publish(topic.Name, topic.CreateItem(local, "before-removal", sequence: 1), targetPeers: null, CancellationToken.None));
        await protocol.FlushPendingGossip(CancellationToken.None);
        Assert.Equal(1, sendCount);

        transport.Peers.Remove(peer);
        Assert.True(await protocol.Publish(topic.Name, topic.CreateItem(local, "during-removal", sequence: 2), targetPeers: null, CancellationToken.None));

        transport.Peers.Add(peer);
        Assert.True(await protocol.Publish(topic.Name, topic.CreateItem(local, "after-return", sequence: 3), targetPeers: null, CancellationToken.None));
        await protocol.FlushPendingGossip(CancellationToken.None);

        Assert.Equal(2, sendCount);
        Assert.Single(transport.GossipBatches);
    }

    [Fact]
    public async Task MembershipRefreshDropsPendingGossipForRemovedPeers()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var topic = new FakeTopic(local);
        topic.Options.MaxCoalescingDelay = TimeSpan.FromMinutes(1);
        var protocol = CreateProtocol(transport, topic, options => options.Overlay.FanOutFactor = static _ => 1);

        Assert.True(await protocol.Publish(topic.Name, topic.CreateItem(local, "before-removal", sequence: 1), targetPeers: null, CancellationToken.None));

        transport.Peers.Remove(peer);
        Assert.True(await protocol.Publish(topic.Name, topic.CreateItem(local, "during-removal", sequence: 2), targetPeers: null, CancellationToken.None));
        await protocol.FlushPendingGossip(CancellationToken.None);

        Assert.Empty(transport.GossipBatches);
    }

    [Fact]
    public async Task ReceiveGossipForwardsOnlyToLocalTreeChildren()
    {
        var silos = Enumerable.Range(11111, 8).Select(CreateSilo).OrderBy(static silo => silo).ToArray();
        var root = silos[0];
        var local = silos[1];
        var peers = silos.Where(silo => !Equals(silo, local)).ToArray();
        var transport = new FakeTransport(local, peers);
        var topic = new FakeTopic(local);
        var protocol = CreateProtocol(transport, topic, options => options.Overlay.FanOutFactor = static _ => 2);
        var item = topic.CreateItem(root, FakeTopic.DefaultKey, sequence: 1);

        await protocol.ReceiveGossip(CreateGossipBatch(root, item), CancellationToken.None);
        await protocol.FlushPendingGossip(CancellationToken.None);

        var expectedChildren = GetForwardingTreeTargets(local, root, peers, fanout: 2, sender: root);
        Assert.Equal(expectedChildren, transport.GossipBatches.Select(batch => batch.Peer));
    }

    [Fact]
    public async Task DuplicateGossipDoesNotForwardAgain()
    {
        var silos = Enumerable.Range(11111, 8).Select(CreateSilo).OrderBy(static silo => silo).ToArray();
        var root = silos[0];
        var local = silos[1];
        var peers = silos.Where(silo => !Equals(silo, local)).ToArray();
        var transport = new FakeTransport(local, peers);
        var topic = new FakeTopic(local);
        var protocol = CreateProtocol(transport, topic, options => options.Overlay.FanOutFactor = static _ => 2);
        var item = topic.CreateItem(root, FakeTopic.DefaultKey, sequence: 1);
        var batch = CreateGossipBatch(root, item);

        await protocol.ReceiveGossip(batch, CancellationToken.None);
        await protocol.ReceiveGossip(batch, CancellationToken.None);
        await protocol.FlushPendingGossip(CancellationToken.None);

        var expectedChildren = GetForwardingTreeTargets(local, root, peers, fanout: 2, sender: root);
        Assert.Equal(expectedChildren.Count, transport.GossipBatches.Count);
        Assert.Equal(1, topic.ApplyCounts[item.Digest.Key]);
    }

    [Fact]
    public async Task ReceiveGossipRejectsTopicApplyExceptionAndContinuesBatch()
    {
        var local = CreateSilo(11111);
        var sender = CreateSilo(11112);
        var transport = new FakeTransport(local, sender);
        var appliedKeys = new List<string>();
        var topic = new FakeTopic(local)
        {
            ApplyValueHandler = (value, _) =>
            {
                if (value.Digest.Key == "malformed")
                {
                    throw new InvalidOperationException("Malformed payload.");
                }

                appliedKeys.Add(value.Digest.Key);
                return ValueTask.FromResult(DisseminationApplyResult.Applied);
            },
        };
        await using var protocol = CreateProtocol(transport, topic);

        await protocol.ReceiveGossip(
            CreateGossipBatch(
                sender,
                topic.CreateItem(sender, "malformed", sequence: 1),
                topic.CreateItem(sender, "valid", sequence: 1)),
            CancellationToken.None);

        Assert.Equal(new[] { "valid" }, appliedKeys);
    }

    [Fact]
    public async Task ReceiveGossipRejectsUnrelatedOperationCanceledExceptionAndContinuesBatch()
    {
        var local = CreateSilo(11111);
        var sender = CreateSilo(11112);
        var transport = new FakeTransport(local, sender);
        var appliedKeys = new List<string>();
        var topic = new FakeTopic(local)
        {
            ApplyValueHandler = (value, _) =>
            {
                if (value.Digest.Key == "canceled")
                {
                    throw new OperationCanceledException("Topic operation canceled independently.");
                }

                appliedKeys.Add(value.Digest.Key);
                return ValueTask.FromResult(DisseminationApplyResult.Applied);
            },
        };
        await using var protocol = CreateProtocol(transport, topic);

        await protocol.ReceiveGossip(
            CreateGossipBatch(
                sender,
                topic.CreateItem(sender, "canceled", sequence: 1),
                topic.CreateItem(sender, "valid", sequence: 1)),
            CancellationToken.None);

        Assert.Equal(new[] { "valid" }, appliedKeys);
    }

    [Fact]
    public async Task ReceiveGossipChecksCallerCancellationBeforeApplyingValue()
    {
        var local = CreateSilo(11111);
        var sender = CreateSilo(11112);
        var transport = new FakeTransport(local, sender);
        var applyAttempts = new List<string>();
        var topic = new FakeTopic(local)
        {
            ApplyValueHandler = (value, _) =>
            {
                applyAttempts.Add(value.Digest.Key);
                return ValueTask.FromResult(DisseminationApplyResult.Applied);
            },
        };
        await using var protocol = CreateProtocol(transport, topic);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => protocol.ReceiveGossip(
            CreateGossipBatch(
                sender,
                topic.CreateItem(sender, "first", sequence: 1),
                topic.CreateItem(sender, "second", sequence: 1)),
            cancellation.Token));

        Assert.Empty(applyAttempts);
    }

    [Fact]
    public async Task ReceiveGossipPropagatesCancellationRaisedDuringApplyBeforeForwarding()
    {
        var local = CreateSilo(11111);
        var sender = CreateSilo(11112);
        var peer = CreateSilo(11113);
        var transport = new FakeTransport(local, sender, peer);
        using var cancellation = new CancellationTokenSource();
        var topic = new FakeTopic(local)
        {
            ApplyValueHandler = (_, _) =>
            {
                cancellation.Cancel();
                return ValueTask.FromResult(DisseminationApplyResult.Applied);
            },
        };
        await using var protocol = CreateProtocol(transport, topic);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => protocol.ReceiveGossip(
            CreateGossipBatch(sender, topic.CreateItem(sender, "value", sequence: 1)),
            cancellation.Token));

        Assert.Empty(transport.GossipBatches);
    }

    [Fact]
    public async Task ReceiveGossipWithMissingRootRefreshesMembershipAndDoesNotForward()
    {
        var root = CreateSilo(11111);
        var local = CreateSilo(11112);
        var sender = CreateSilo(11113);
        var peer = CreateSilo(11114);
        var transport = new FakeTransport(local, sender, peer);
        var topic = new FakeTopic(local);
        var protocol = CreateProtocol(transport, topic, options => options.Overlay.FanOutFactor = static _ => 2);
        var item = topic.CreateItem(root, FakeTopic.DefaultKey, sequence: 1);

        await protocol.ReceiveGossip(CreateGossipBatch(sender, item), CancellationToken.None);
        await protocol.FlushPendingGossip(CancellationToken.None);

        Assert.Equal(1, topic.GetVersion(FakeTopic.DefaultKey));
        Assert.Equal(1, transport.RefreshMembershipCallCount);
        Assert.Empty(transport.GossipBatches);
    }

    [Fact]
    public async Task ReceiveGossipWithMissingRootForwardsAfterMembershipRefresh()
    {
        var local = CreateSilo(11111);
        var sender = CreateSilo(11112);
        var peer = CreateSilo(11113);
        var root = CreateSilo(11120);
        var transport = new FakeTransport(local, sender, peer);
        transport.RefreshMembershipHandler = _ =>
        {
            transport.Peers.Add(root);
            return Task.CompletedTask;
        };
        var topic = new FakeTopic(local);
        var protocol = CreateProtocol(transport, topic, options => options.Overlay.FanOutFactor = static _ => 2);
        var item = topic.CreateItem(root, FakeTopic.DefaultKey, sequence: 1);

        await protocol.ReceiveGossip(CreateGossipBatch(sender, item), CancellationToken.None);
        await protocol.FlushPendingGossip(CancellationToken.None);

        Assert.Equal(1, topic.GetVersion(FakeTopic.DefaultKey));
        Assert.Equal(1, transport.RefreshMembershipCallCount);
        Assert.Equal(new[] { peer }, transport.GossipBatches.Select(static batch => batch.Peer));
    }

    [Fact]
    public async Task ReceiveGossipContinuesAfterForwardFailure()
    {
        var local = CreateSilo(11111);
        var sender = CreateSilo(11112);
        var peer = CreateSilo(11113);
        var root = CreateSilo(11120);
        var transport = new FakeTransport(local, sender, peer);
        transport.RefreshMembershipHandler = _ =>
        {
            if (transport.RefreshMembershipCallCount == 1)
            {
                throw new InvalidOperationException("Transient membership refresh failure.");
            }

            transport.Peers.Add(root);
            return Task.CompletedTask;
        };
        var topic = new FakeTopic(local);
        await using var protocol = CreateProtocol(transport, topic, options => options.Overlay.FanOutFactor = static _ => 2);
        var failures = new List<DisseminationValueEvent>();
        using var subscription = DisseminationEvents.Listener.Subscribe(
            new ActionObserver<KeyValuePair<string, object?>>(entry =>
            {
                if (entry.Key == "Dissemination.ForwardFailure" && entry.Value is DisseminationValueEvent value)
                {
                    failures.Add(value);
                }
            }));

        await protocol.ReceiveGossip(
            CreateGossipBatch(
                sender,
                topic.CreateItem(root, "first", sequence: 1),
                topic.CreateItem(root, "second", sequence: 1)),
            CancellationToken.None);
        await protocol.FlushPendingGossip(CancellationToken.None);

        Assert.Equal(1, topic.GetVersion("first"));
        Assert.Equal(1, topic.GetVersion("second"));
        Assert.Equal(2, transport.RefreshMembershipCallCount);
        var forwarded = Assert.Single(transport.GossipBatches);
        Assert.Equal(peer, forwarded.Peer);
        Assert.Equal("second", Assert.Single(GetGossipValues(forwarded.Batch)).Digest.Key);
        var failure = Assert.Single(failures);
        Assert.Equal("exception", failure.Result);
        Assert.Null(failure.Peer);
    }

    [Fact]
    public async Task ReceiveGossipDiagnosesForwardQueueCapacityFailure()
    {
        var root = CreateSilo(11111);
        var local = CreateSilo(11112);
        var peer = CreateSilo(11113);
        var transport = new FakeTransport(local, root, peer);
        var firstSendStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstSend = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendCount = 0;
        transport.SendGossipHandler = async (target, batch, cancellationToken) =>
        {
            transport.GossipBatches.Add((target, batch));
            if (Interlocked.Increment(ref sendCount) == 1)
            {
                firstSendStarted.TrySetResult(true);
                await releaseFirstSend.Task.WaitAsync(cancellationToken);
            }
        };
        var topic = new FakeTopic(local);
        topic.Options.MaxPendingItemCount = 1;
        topic.Options.MaxCoalescingDelay = TimeSpan.FromMinutes(1);
        var protocol = CreateProtocol(transport, topic, options => options.Overlay.FanOutFactor = static _ => 1);
        var failures = new List<DisseminationValueEvent>();
        using var subscription = DisseminationEvents.Listener.Subscribe(
            new ActionObserver<KeyValuePair<string, object?>>(entry =>
            {
                if (entry.Key == "Dissemination.ForwardFailure" && entry.Value is DisseminationValueEvent value)
                {
                    failures.Add(value);
                }
            }));

        try
        {
            Assert.True(await protocol.Publish(topic.Name, topic.CreateItem(local, "in-flight", sequence: 1), [peer], CancellationToken.None));
            await firstSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.True(await protocol.Publish(topic.Name, topic.CreateItem(local, "queued", sequence: 1), [peer], CancellationToken.None));

            await protocol.ReceiveGossip(
                CreateGossipBatch(root, topic.CreateItem(root, "rejected", sequence: 1)),
                CancellationToken.None);

            var failure = Assert.Single(failures);
            Assert.Equal(topic.Name, failure.Topic);
            Assert.Equal("rejected", failure.Key);
            Assert.Equal("queue-capacity", failure.Result);
            Assert.Equal(peer, failure.Peer);
        }
        finally
        {
            releaseFirstSend.TrySetResult(true);
            await protocol.FlushPendingGossip(CancellationToken.None);
            await protocol.DisposeAsync();
        }
    }

    [Fact]
    public async Task TreeRoutingInvalidatesCachedTopologyWhenActivePeersChange()
    {
        var local = CreateSilo(11115);
        var initialPeers = Enumerable.Range(11112, 3).Select(CreateSilo).ToList();
        var transport = new FakeTransport(local, initialPeers.ToArray());
        var topic = new FakeTopic(local);
        var protocol = CreateProtocol(transport, topic, options => options.Overlay.FanOutFactor = static _ => 2);
        var item = topic.CreateItem(local, FakeTopic.DefaultKey, sequence: 1);

        var initialResult = await protocol.Publish(topic.Name, item, targetPeers: null, CancellationToken.None);
        await protocol.FlushPendingGossip(CancellationToken.None);
        var initialChildren = transport.GossipBatches.Select(batch => batch.Peer).ToArray();

        foreach (var peer in Enumerable.Range(11116, 8).Select(CreateSilo))
        {
            transport.Peers.Add(peer);
            var updatedChildren = GetOriginatorTreeTargets(local, transport.Peers, fanout: 2);
            if (!initialChildren.SequenceEqual(updatedChildren))
            {
                transport.GossipBatches.Clear();
                var updatedItem = topic.CreateItem(local, FakeTopic.DefaultKey, sequence: 2);

                var updatedResult = await protocol.Publish(topic.Name, updatedItem, targetPeers: null, CancellationToken.None);
                await protocol.FlushPendingGossip(CancellationToken.None);

                Assert.True(initialResult);
                Assert.True(updatedResult);
                Assert.Equal(updatedChildren, transport.GossipBatches.Select(batch => batch.Peer));
                return;
            }
        }

        throw new InvalidOperationException("The test did not find a peer set which changes the local tree children.");
    }

    [Fact]
    public async Task GossipBatchingCoalescesSamePeerValuesByLatestVersion()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var topic = new FakeTopic(local);
        var protocol = CreateProtocol(transport, topic);

        var first = await protocol.Publish(topic.Name, topic.CreateItem(local, FakeTopic.DefaultKey, sequence: 1), [peer], CancellationToken.None);
        var second = await protocol.Publish(topic.Name, topic.CreateItem(local, FakeTopic.DefaultKey, sequence: 2), [peer], CancellationToken.None);
        await protocol.FlushPendingGossip(CancellationToken.None);

        Assert.True(first);
        Assert.True(second);
        var batch = Assert.Single(transport.GossipBatches);
        Assert.Equal(peer, batch.Peer);
        var value = Assert.Single(GetGossipValues(batch.Batch));
        Assert.Equal(2, value.Digest.Version);
    }

    [Fact]
    public async Task GossipBatchingWakesScheduledFlushWhenBatchLimitIsReached()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var sent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.SendGossipHandler = (target, batch, cancellationToken) =>
        {
            transport.GossipBatches.Add((target, batch));
            sent.TrySetResult(true);
            return Task.CompletedTask;
        };

        var topic = new FakeTopic(local);
        topic.Options.MaxCoalescingDelay = TimeSpan.FromMinutes(1);
        var protocol = CreateProtocol(transport, topic, options => options.MaxBatchItems = 2);

        var first = await protocol.Publish(topic.Name, topic.CreateItem(local, "first", sequence: 1), [peer], CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
        var second = await protocol.Publish(topic.Name, topic.CreateItem(local, "second", sequence: 1), [peer], CancellationToken.None);

        await sent.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.True(first);
        Assert.True(second);
        var batch = Assert.Single(transport.GossipBatches);
        Assert.Equal(new[] { "first", "second" }, GetGossipValues(batch.Batch).Select(static value => value.Digest.Key));
    }

    [Fact]
    public async Task GossipBatchingRejectsNewKeysAtPendingTopicLimit()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var firstSendStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstSend = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendCount = 0;
        transport.SendGossipHandler = async (target, batch, cancellationToken) =>
        {
            transport.GossipBatches.Add((target, batch));
            if (Interlocked.Increment(ref sendCount) == 1)
            {
                firstSendStarted.TrySetResult(true);
                await releaseFirstSend.Task.WaitAsync(cancellationToken);
            }
        };
        var topic = new FakeTopic(local);
        topic.Options.MaxCoalescingDelay = TimeSpan.FromMinutes(1);
        topic.Options.MaxPendingItemCount = 1;
        var protocol = CreateProtocol(transport, topic);

        var first = await protocol.Publish(topic.Name, topic.CreateItem(local, "first", sequence: 1), [peer], CancellationToken.None);
        await firstSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        var second = await protocol.Publish(topic.Name, topic.CreateItem(local, "second", sequence: 1), [peer], CancellationToken.None);
        var third = await protocol.Publish(topic.Name, topic.CreateItem(local, "third", sequence: 1), [peer], CancellationToken.None);

        Assert.True(first);
        Assert.True(second);
        Assert.False(third);

        releaseFirstSend.SetResult(true);
        await protocol.FlushPendingGossip(CancellationToken.None);

        Assert.Equal(2, transport.GossipBatches.Count);
        Assert.Equal(
            new[] { "first", "second" },
            transport.GossipBatches.Select(batch => Assert.Single(GetGossipValues(batch.Batch)).Digest.Key));
    }

    [Fact]
    public async Task GossipBatchingDropsExpiredValuesBeforeSend()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var timeProvider = new TestTimeProvider();
        var transport = new FakeTransport(local, peer);
        var topic = new FakeTopic(local);
        topic.Options.MaxCoalescingDelay = TimeSpan.FromMinutes(1);
        var protocol = CreateProtocol(transport, topic, timeProvider: timeProvider);
        var item = new DisseminationValue
        {
            Digest = new DisseminationTopicDigest(FakeTopic.DefaultKey, version: 1),
            Root = local,
            ExpiresAt = timeProvider.GetUtcNow().AddSeconds(1),
            Payload = BitConverter.GetBytes(1L),
        };

        Assert.True(await protocol.Publish(topic.Name, item, [peer], CancellationToken.None));
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await protocol.FlushPendingGossip(CancellationToken.None);

        Assert.Empty(transport.GossipBatches);
    }

    [Fact]
    public async Task GossipSendsHonorConfiguredConcurrency()
    {
        var local = CreateSilo(11111);
        var peers = Enumerable.Range(11112, 4).Select(CreateSilo).ToArray();
        var transport = new FakeTransport(local, peers);
        var releaseSends = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var concurrencyReached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var inFlight = 0;
        var maxInFlight = 0;
        transport.SendGossipHandler = async (_, _, cancellationToken) =>
        {
            Interlocked.Increment(ref started);
            var current = Interlocked.Increment(ref inFlight);
            UpdateMaximum(ref maxInFlight, current);
            if (current == 2)
            {
                concurrencyReached.TrySetResult(true);
            }

            try
            {
                await releaseSends.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref inFlight);
            }
        };

        var topic = new FakeTopic(local);
        var protocol = CreateProtocol(transport, topic, options =>
        {
            options.MaxConcurrentSends = 2;
            options.Overlay.FanOutFactor = _ => peers.Length;
        });

        Assert.True(await protocol.Publish(
            topic.Name,
            topic.CreateItem(local, FakeTopic.DefaultKey, sequence: 1),
            peers,
            CancellationToken.None));
        var flushTask = protocol.FlushPendingGossip(CancellationToken.None);

        await concurrencyReached.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(2, Volatile.Read(ref started));
        Assert.Equal(2, Volatile.Read(ref maxInFlight));

        releaseSends.SetResult(true);
        await flushTask;

        Assert.Equal(peers.Length, started);
        Assert.Equal(2, maxInFlight);
    }

    [Fact]
    public async Task ScheduledAndExplicitFlushesShareConfiguredConcurrencyLimit()
    {
        var local = CreateSilo(11111);
        var firstPeer = CreateSilo(11112);
        var secondPeer = CreateSilo(11113);
        var transport = new FakeTransport(local, firstPeer, secondPeer);
        var firstSendStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstSend = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allSendsCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = 0;
        var maxInFlight = 0;
        var completed = 0;
        transport.SendGossipHandler = async (_, _, cancellationToken) =>
        {
            var current = Interlocked.Increment(ref inFlight);
            UpdateMaximum(ref maxInFlight, current);
            if (Interlocked.CompareExchange(ref completed, 0, 0) == 0)
            {
                firstSendStarted.TrySetResult(true);
                await releaseFirstSend.Task.WaitAsync(cancellationToken);
            }

            Interlocked.Decrement(ref inFlight);
            if (Interlocked.Increment(ref completed) == 2)
            {
                allSendsCompleted.TrySetResult(true);
            }
        };

        var topic = new FakeTopic(local);
        topic.Options.MaxCoalescingDelay = TimeSpan.FromMilliseconds(1);
        var protocol = CreateProtocol(transport, topic, options =>
        {
            options.MaxConcurrentSends = 1;
            options.Overlay.FanOutFactor = static _ => 1;
        });

        Assert.True(await protocol.Publish(topic.Name, topic.CreateItem(local, "first", sequence: 1), [firstPeer], CancellationToken.None));
        await firstSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.True(await protocol.Publish(topic.Name, topic.CreateItem(local, "second", sequence: 1), [secondPeer], CancellationToken.None));
        var explicitFlush = protocol.FlushPendingGossip(CancellationToken.None);

        Assert.Equal(1, Volatile.Read(ref maxInFlight));
        releaseFirstSend.SetResult(true);
        await explicitFlush;
        await allSendsCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(1, maxInFlight);
    }

    [Fact]
    public void FixedTreeRoutingSatisfiesReachabilityAndFanoutInvariants()
    {
        Gen.Select(Gen.Int[1, 64], Gen.Int[1, 8], Gen.Int[0, 63], static (count, fanout, rootSeed) =>
        {
            var rootIndex = rootSeed % count;
            return (Count: count, Fanout: fanout, RootIndex: rootIndex);
        }).Sample(testCase =>
        {
            var silos = CreateSilos(testCase.Count);
            var root = silos[testCase.RootIndex];
            var directTargets = GetOriginatorTreeTargets(root, silos.Where(silo => !Equals(silo, root)), testCase.Fanout);

            Assert.DoesNotContain(root, directTargets);
            Assert.Equal(directTargets.Count, directTargets.Distinct().Count());
            Assert.True(directTargets.Count <= Math.Min((testCase.Fanout * 2), Math.Max(0, testCase.Count - 1)));

            var reached = GetReachedParticipants(root, silos, testCase.Fanout);
            Assert.Equal(silos.OrderBy(static silo => silo), reached.OrderBy(static silo => silo));
        });
    }

    [Fact]
    public void AntiEntropyPeerSelectionUsesFixedTreeLevels()
    {
        Gen.Select(Gen.Int[2, 64], Gen.Int[1, 8], Gen.Int[0, 63], Gen.Int[1, 8], static (count, fanout, localSeed, peerCount) =>
        {
            var localIndex = localSeed % count;
            return (Count: count, Fanout: fanout, LocalIndex: localIndex, PeerCount: peerCount);
        }).Sample(testCase =>
        {
            var silos = CreateSilos(testCase.Count);
            var local = silos[testCase.LocalIndex];
            var transport = new FakeTransport(local, silos.Where(silo => !Equals(silo, local)).ToArray());
            var topic = new FakeTopic(local);
            topic.ExpectedKeys.Add(FakeTopic.DefaultKey);
            var protocol = CreateProtocol(transport, topic, options =>
            {
                options.Overlay.FanOutFactor = _ => testCase.Fanout;
                options.Overlay.AntiEntropyPeerCount = testCase.PeerCount;
            });

            var state = protocol.CreateAntiEntropyState();
            var peers = state.Topics[topic.Name].Peers;
            var expectedCandidates = GetAntiEntropyCandidateIndexes(testCase.LocalIndex, testCase.Count, testCase.Fanout)
                .Where(index => index != testCase.LocalIndex)
                .Select(index => silos[index])
                .ToHashSet();

            Assert.True(peers.Length <= Math.Min(testCase.PeerCount, Math.Max(0, expectedCandidates.Count)));
            Assert.DoesNotContain(local, peers);
            Assert.All(peers, peer => Assert.Contains(peer, expectedCandidates));
        });
    }

    [Fact]
    public void AntiEntropyPeerSelectionUsesTopicSpecificSalt()
    {
        const int fanout = 3;
        const int peerCount = 1;
        for (var count = 6; count < 32; count++)
        {
            var silos = CreateSilos(count);
            for (var localIndex = 0; localIndex < silos.Length; localIndex++)
            {
                var local = silos[localIndex];
                var expectedFirst = GetExpectedAntiEntropyPeers("topic-a", silos, localIndex, fanout, peerCount, round: 1);
                var expectedSecond = GetExpectedAntiEntropyPeers("topic-b", silos, localIndex, fanout, peerCount, round: 1);
                if (expectedFirst.SequenceEqual(expectedSecond))
                {
                    continue;
                }

                var transport = new FakeTransport(local, silos.Where(silo => !Equals(silo, local)).ToArray());
                var firstTopic = new FakeTopic(local, "topic-a");
                var secondTopic = new FakeTopic(local, "topic-b");
                firstTopic.ExpectedKeys.Add(FakeTopic.DefaultKey);
                secondTopic.ExpectedKeys.Add(FakeTopic.DefaultKey);
                var protocol = CreateProtocol(transport, new IDisseminationTopic[] { firstTopic, secondTopic }, options =>
                {
                    options.Overlay.FanOutFactor = static _ => fanout;
                    options.Overlay.AntiEntropyPeerCount = peerCount;
                });

                var state = protocol.CreateAntiEntropyState();

                Assert.Equal(expectedFirst, state.Topics[firstTopic.Name].Peers);
                Assert.Equal(expectedSecond, state.Topics[secondTopic.Name].Peers);
                return;
            }
        }

        throw new InvalidOperationException("The test did not find a topology where topic salt changes peer selection.");
    }

    [Fact]
    public void CreateAntiEntropyStateReturnsEmptyWhenDisseminationIsDisabled()
    {
        var local = CreateSilo(11111);
        var transport = new FakeTransport(local, CreateSilo(11112));
        var topic = new FakeTopic(local);
        var protocol = CreateProtocol(transport, topic, options => options.Enabled = false);

        var state = protocol.CreateAntiEntropyState();

        Assert.Empty(state.Topics);
    }

    [Fact]
    public async Task AntiEntropyResponseIncludesNewerLocalValues()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var topic = new FakeTopic(local);
        var protocol = CreateProtocol(transport, topic);
        topic.SetValue(FakeTopic.DefaultKey, version: 5);

        var response = await protocol.ReceiveAntiEntropy(new DisseminationAntiEntropyRequest
        {
            Sender = peer,
            DigestsByTopic = CreateAntiEntropyRequestDigests(
                topic.Name,
                new DisseminationTopicDigest(FakeTopic.DefaultKey, version: 3)),
        }, CancellationToken.None);

        var item = Assert.Single(GetAntiEntropyResponseValues(response));
        Assert.Equal(5, item.Digest.Version);
        Assert.False(response.Truncated);
    }

    [Fact]
    public async Task AntiEntropyResponseOnlyIncludesRequestedDigestKeys()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var topic = new FakeTopic(local);
        var protocol = CreateProtocol(transport, topic);
        topic.SetValue("requested", version: 5);
        topic.SetValue("omitted", version: 5);

        var response = await protocol.ReceiveAntiEntropy(new DisseminationAntiEntropyRequest
        {
            Sender = peer,
            DigestsByTopic = CreateAntiEntropyRequestDigests(
                topic.Name,
                new DisseminationTopicDigest("requested", version: 3)),
        }, CancellationToken.None);

        var item = Assert.Single(GetAntiEntropyResponseValues(response));
        Assert.Equal("requested", item.Digest.Key);
        Assert.Equal(new[] { topic.Name }, response.SupportedTopics);
    }

    [Fact]
    public async Task AntiEntropyResponseIsEmptyWhenDisseminationIsDisabled()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var topic = new FakeTopic(local);
        topic.SetValue(FakeTopic.DefaultKey, version: 5);
        var protocol = CreateProtocol(transport, topic, options => options.Enabled = false);

        var response = await protocol.ReceiveAntiEntropy(new DisseminationAntiEntropyRequest
        {
            Sender = peer,
            DigestsByTopic = CreateAntiEntropyRequestDigests(
                topic.Name,
                new DisseminationTopicDigest(FakeTopic.DefaultKey, version: 3)),
        }, CancellationToken.None);

        Assert.Equal(local, response.Sender);
        Assert.Empty(response.ValuesByTopic);
        Assert.False(response.Truncated);
    }

    [Fact]
    public async Task AntiEntropyResponseSkipsUnknownTopicsEmptyRequestsAndCurrentRemoteValues()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var topic = new FakeTopic(local);
        var emptyTopic = new FakeTopic(local, "empty");
        topic.SetValue("equal", version: 5);
        topic.SetValue("remote-newer", version: 5);
        var protocol = CreateProtocol(transport, new IDisseminationTopic[] { topic, emptyTopic });
        var digestsByTopic = ImmutableDictionary<string, ImmutableArray<DisseminationTopicDigest>>.Empty
            .Add("unknown", [new DisseminationTopicDigest("value", version: 1)])
            .Add("empty", [])
            .Add(topic.Name,
            [
                new DisseminationTopicDigest("equal", version: 5),
                new DisseminationTopicDigest("remote-newer", version: 6),
            ]);

        var response = await protocol.ReceiveAntiEntropy(new DisseminationAntiEntropyRequest
        {
            Sender = peer,
            DigestsByTopic = digestsByTopic,
        }, CancellationToken.None);

        Assert.Empty(response.ValuesByTopic);
        Assert.False(response.Truncated);
    }

    [Fact]
    public async Task AntiEntropyResponseSkipsNullAndOversizedValues()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var topic = new FakeTopic(local);
        topic.Options.MaxPayloadBytes = 8;
        topic.SetValue("null", version: 2);
        topic.SetValue("oversized", version: 2);
        topic.SetValue("valid", version: 2);
        topic.GetValueHandler = (digest, _) => digest.Key switch
        {
            "null" => null,
            "oversized" => new DisseminationValue
            {
                Digest = digest,
                Root = local,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
                Payload = new byte[9],
            },
            _ => topic.CreateItem(local, digest.Key, digest.Version),
        };
        var protocol = CreateProtocol(transport, topic);

        var response = await protocol.ReceiveAntiEntropy(new DisseminationAntiEntropyRequest
        {
            Sender = peer,
            DigestsByTopic = CreateAntiEntropyRequestDigests(
                topic.Name,
                new DisseminationTopicDigest("null", version: 1),
                new DisseminationTopicDigest("oversized", version: 1),
                new DisseminationTopicDigest("valid", version: 1)),
        }, CancellationToken.None);

        var value = Assert.Single(GetAntiEntropyResponseValues(response));
        Assert.Equal("valid", value.Digest.Key);
        Assert.False(response.Truncated);
    }

    [Fact]
    public async Task AntiEntropyResponseTruncatesAtItemLimit()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var topic = new FakeTopic(local);
        topic.SetValue("a", version: 2);
        topic.SetValue("b", version: 2);
        var protocol = CreateProtocol(transport, topic, options => options.MaxBatchItems = 1);

        var response = await protocol.ReceiveAntiEntropy(new DisseminationAntiEntropyRequest
        {
            Sender = peer,
            DigestsByTopic = CreateAntiEntropyRequestDigests(
                topic.Name,
                new DisseminationTopicDigest("a", version: 1),
                new DisseminationTopicDigest("b", version: 1)),
        }, CancellationToken.None);

        Assert.Single(GetAntiEntropyResponseValues(response));
        Assert.True(response.Truncated);
    }

    [Fact]
    public async Task AntiEntropyResponseTruncatesBeforeExceedingByteLimit()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var topic = new FakeTopic(local);
        topic.SetValue("a", version: 2);
        topic.SetValue("b", version: 2);
        var protocol = CreateProtocol(transport, topic, options => options.MaxBatchBytes = sizeof(long) + 1);

        var response = await protocol.ReceiveAntiEntropy(new DisseminationAntiEntropyRequest
        {
            Sender = peer,
            DigestsByTopic = CreateAntiEntropyRequestDigests(
                topic.Name,
                new DisseminationTopicDigest("a", version: 1),
                new DisseminationTopicDigest("b", version: 1)),
        }, CancellationToken.None);

        var value = Assert.Single(GetAntiEntropyResponseValues(response));
        Assert.Equal(sizeof(long), value.Payload.Length);
        Assert.True(response.Truncated);
    }

    [Fact]
    public async Task AntiEntropyDigestRequestsAreSuppressedUntilExpectedCadenceExpires()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var topic = new FakeTopic(local);
        var timeProvider = new TestTimeProvider();
        topic.Options.ExpectedUpdateCadence = TimeSpan.FromSeconds(2);
        var protocol = CreateProtocol(transport, topic, timeProvider: timeProvider);

        await protocol.ReceiveGossip(
            CreateGossipBatch(peer, topic.CreateItem(peer, FakeTopic.DefaultKey, sequence: 1)),
            CancellationToken.None);

        var recentState = protocol.CreateAntiEntropyState();
        Assert.Empty(recentState.Topics);
        var recentResponses = await protocol.ExchangeAntiEntropy(recentState, CancellationToken.None);
        Assert.Empty(recentResponses);
        Assert.Empty(transport.AntiEntropyRequests);

        timeProvider.Advance(TimeSpan.FromSeconds(2) - TimeSpan.FromMilliseconds(1));
        var beforeCadenceState = protocol.CreateAntiEntropyState();
        Assert.Empty(beforeCadenceState.Topics);

        timeProvider.Advance(TimeSpan.FromMilliseconds(1));
        var staleState = protocol.CreateAntiEntropyState();
        var digest = Assert.Single(staleState.Topics[topic.Name].Digests);
        Assert.Equal(FakeTopic.DefaultKey, digest.Key);
    }

    [Fact]
    public async Task AntiEntropyRequestsAreChunkedAtConfiguredItemLimit()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var topic = new FakeTopic(local);
        topic.SetValue("a", version: 1);
        topic.SetValue("b", version: 1);
        topic.SetValue("c", version: 1);
        var protocol = CreateProtocol(transport, topic, options =>
        {
            options.MaxBatchItems = 2;
            options.Overlay.AntiEntropyPeerCount = 1;
        });

        var responses = await protocol.ExchangeAntiEntropy(protocol.CreateAntiEntropyState(), CancellationToken.None);

        Assert.Empty(responses);
        Assert.Equal(2, transport.AntiEntropyRequests.Count);
        Assert.All(
            transport.AntiEntropyRequests,
            entry => Assert.InRange(entry.Request.DigestsByTopic.Values.Sum(static digests => digests.Length), 1, 2));
    }

    [Fact]
    public async Task EmptyAntiEntropyChunkDoesNotStarveLaterRepairChunk()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var topic = new FakeTopic(local);
        topic.SetValue("a", version: 1);
        topic.SetValue("b", version: 1);
        transport.ExchangeAntiEntropyHandler = (target, request) =>
        {
            var digest = request.DigestsByTopic.Values.SelectMany(static digests => digests).Single();
            return ValueTask.FromResult(new DisseminationAntiEntropyResponse
            {
                Sender = target,
                ValuesByTopic = digest.Key == "b"
                    ? CreateValueGroups(topic.CreateItem(target, digest.Key, sequence: 2))
                    : ImmutableDictionary<string, ImmutableArray<DisseminationValue>>.Empty,
                SupportedTopics = [topic.Name],
            });
        };
        var protocol = CreateProtocol(transport, topic, options =>
        {
            options.MaxBatchItems = 1;
            options.Overlay.AntiEntropyPeerCount = 1;
        });

        var responses = await protocol.ExchangeAntiEntropy(protocol.CreateAntiEntropyState(), CancellationToken.None);

        Assert.Equal(2, transport.AntiEntropyRequests.Count);
        var repaired = Assert.Single(responses);
        Assert.Equal("b", Assert.Single(GetAntiEntropyResponseValues(repaired)).Digest.Key);
    }

    [Fact]
    public async Task AntiEntropyRoundRetainsAtMostConfiguredItemsAcrossChunks()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var topic = new FakeTopic(local);
        topic.SetValue("a", version: 1);
        topic.SetValue("b", version: 1);
        topic.SetValue("c", version: 1);
        transport.ExchangeAntiEntropyHandler = (target, request) =>
        {
            var values = request.DigestsByTopic.Values
                .SelectMany(static digests => digests)
                .Select(digest => topic.CreateItem(target, digest.Key, sequence: 2))
                .ToArray();
            return ValueTask.FromResult(new DisseminationAntiEntropyResponse
            {
                Sender = target,
                ValuesByTopic = CreateValueGroups(values),
                SupportedTopics = [topic.Name],
            });
        };
        var protocol = CreateProtocol(transport, topic, options =>
        {
            options.MaxBatchItems = 2;
            options.Overlay.AntiEntropyPeerCount = 1;
        });

        var responses = await protocol.ExchangeAntiEntropy(protocol.CreateAntiEntropyState(), CancellationToken.None);

        Assert.Single(transport.AntiEntropyRequests);
        Assert.Equal(2, responses.Sum(static response => GetAntiEntropyResponseValues(response).Count()));
    }

    [Fact]
    public async Task PeerTopicConfirmationExpiresAndAuthoritativeResponseCanRemoveIt()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var timeProvider = new TestTimeProvider();
        var transport = new FakeTransport(local, peer);
        var topic = new FakeTopic(local);
        var protocol = CreateProtocol(transport, topic, timeProvider: timeProvider);

        await protocol.ReceiveAntiEntropy(new DisseminationAntiEntropyRequest
        {
            Sender = peer,
            DigestsByTopic = CreateAntiEntropyRequestDigests(
                topic.Name,
                new DisseminationTopicDigest(FakeTopic.DefaultKey, long.MinValue)),
        }, CancellationToken.None);
        Assert.Empty(protocol.GetUnconfirmedPeers(topic.Name, topic.MembershipScope));

        timeProvider.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(new[] { peer }, protocol.GetUnconfirmedPeers(topic.Name, topic.MembershipScope));

        await protocol.ReceiveAntiEntropy(new DisseminationAntiEntropyRequest
        {
            Sender = peer,
            DigestsByTopic = CreateAntiEntropyRequestDigests(
                topic.Name,
                new DisseminationTopicDigest(FakeTopic.DefaultKey, long.MinValue)),
        }, CancellationToken.None);
        transport.ExchangeAntiEntropyHandler = (target, _) => ValueTask.FromResult(new DisseminationAntiEntropyResponse
        {
            Sender = target,
            SupportedTopics = [],
        });
        topic.SetValue(FakeTopic.DefaultKey, version: 1);

        await protocol.ExchangeAntiEntropy(protocol.CreateAntiEntropyState(), CancellationToken.None);

        Assert.Equal(new[] { peer }, protocol.GetUnconfirmedPeers(topic.Name, topic.MembershipScope));
    }

    [Fact]
    public async Task AntiEntropyAppliesReturnedRepairItemsWithoutForwarding()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var topic = new FakeTopic(local);
        var protocol = CreateProtocol(transport, topic);
        topic.ExpectedKeys.Add(FakeTopic.DefaultKey);
        var repairItem = topic.CreateItem(peer, FakeTopic.DefaultKey, sequence: 7);
        transport.ExchangeAntiEntropyHandler = (target, request) => ValueTask.FromResult(new DisseminationAntiEntropyResponse
        {
            Sender = target,
            ValuesByTopic = CreateValueGroups(repairItem),
        });

        var state = protocol.CreateAntiEntropyState();
        var responses = await protocol.ExchangeAntiEntropy(state, CancellationToken.None);
        await protocol.ApplyAntiEntropyResponses(responses, CancellationToken.None);

        Assert.Equal(7, topic.GetVersion(FakeTopic.DefaultKey));
        Assert.Empty(transport.GossipBatches);
        Assert.Single(transport.AntiEntropyRequests);
        var digestsByTopic = Assert.Single(transport.AntiEntropyRequests[0].Request.DigestsByTopic);
        Assert.Equal(topic.Name, digestsByTopic.Key);
        var digest = Assert.Single(digestsByTopic.Value);
        Assert.Equal(FakeTopic.DefaultKey, digest.Key);
        Assert.Equal(long.MinValue, digest.Version);
    }

    [Fact]
    public async Task AntiEntropyAppliesValidItemsAfterFailedRepairItem()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var topic = new FakeTopic(local);
        var protocol = CreateProtocol(transport, topic);
        topic.SetValue(FakeTopic.DefaultKey, version: 1);
        var badRepairItem = new DisseminationValue
        {
            Digest = new DisseminationTopicDigest(FakeTopic.DefaultKey, version: 2),
            Root = peer,
            ExpiresAt = TimeProvider.System.GetUtcNow().AddMinutes(1),
            Payload = Array.Empty<byte>(),
        };
        var goodRepairItem = topic.CreateItem(peer, FakeTopic.DefaultKey, sequence: 3);

        await protocol.ApplyAntiEntropyResponses(new[]
        {
            new DisseminationAntiEntropyResponse
            {
                Sender = peer,
                ValuesByTopic = CreateValueGroups(badRepairItem, goodRepairItem),
            },
        }, CancellationToken.None);

        Assert.Equal(3, topic.GetVersion(FakeTopic.DefaultKey));
    }

    [Fact]
    public async Task AntiEntropyLoopContinuesAfterApplyFailure()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var topic = new FakeTopic(local);
        var service = CreateService(transport, topic, options => options.Overlay.AntiEntropyInterval = TimeSpan.FromMilliseconds(1));
        topic.SetValue(FakeTopic.DefaultKey, version: 1);

        var badRepairItem = new DisseminationValue
        {
            Digest = new DisseminationTopicDigest(FakeTopic.DefaultKey, version: 2),
            Root = peer,
            ExpiresAt = TimeProvider.System.GetUtcNow().AddMinutes(1),
            Payload = Array.Empty<byte>(),
        };
        var goodRepairItem = topic.CreateItem(peer, FakeTopic.DefaultKey, sequence: 3);
        var exchangeCount = 0;
        transport.ExchangeAntiEntropyHandler = (target, request) =>
        {
            var count = Interlocked.Increment(ref exchangeCount);
            return ValueTask.FromResult(new DisseminationAntiEntropyResponse
            {
                Sender = target,
                ValuesByTopic = count switch
                {
                    1 => CreateValueGroups(badRepairItem),
                    2 => CreateValueGroups(goodRepairItem),
                    _ => ImmutableDictionary<string, ImmutableArray<DisseminationValue>>.Empty,
                },
            });
        };

        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntil(() => topic.GetVersion(FakeTopic.DefaultKey) == 3);

            Assert.True(Volatile.Read(ref exchangeCount) >= 2);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task MonotonicDisseminationModelConformsToAccordantSpec()
    {
        var spec = CreateMonotonicDisseminationSpec();
        spec.ExecuteWith<MonotonicDisseminationHarness>()
            .BindAsync<long, ModelApplyResponse>("Receive", static (harness, version) => harness.Receive(version))
            .BindAsync<long, ModelRepairResponse>("RepairPeer", static (harness, peerVersion) => harness.RepairPeer(peerVersion));

        var receive = spec.GetOperation<long, ModelApplyResponse>("Receive");
        var repairPeer = spec.GetOperation<long, ModelRepairResponse>("RepairPeer");
        var inputs = new InputSet
        {
            receive.With(1, "Receive version 1"),
            receive.With(2, "Receive version 2"),
            receive.With(3, "Receive version 3"),
            repairPeer.With(0, "Repair peer with no value"),
            repairPeer.With(1, "Repair peer at version 1"),
            repairPeer.With(3, "Repair peer at version 3"),
        };
        var initialState = new MonotonicDisseminationState();
        var testCases = spec.GenerateTests(initialState, inputs, new TestGenerationOptions { MaxDepth = 4 });
        var harness = new MonotonicDisseminationHarness(CreateSilo(11111));
        var context = spec.CreateTestingContext();
        context.Register(harness);

        var results = await spec.RunTests(
            context,
            initialState,
            testCases,
            new TestExecutionOptions
            {
                BeforeEachAsync = testContext =>
                {
                    testContext.Context.Get<MonotonicDisseminationHarness>().Reset();
                    return Task.CompletedTask;
                },
            });

        var failures = results.Where(static result => !result.Success).ToArray();
        Assert.Empty(failures);
    }

    [Fact]
    public async Task MembershipTopicReturnsAndAppliesDiffWhenPeerVersionIsRetained()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var baseSnapshot = CreateMembershipSnapshot(
            version: 1,
            CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch));
        var updatedSnapshot = CreateMembershipSnapshot(
            version: 2,
            CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch),
            CreateMembershipEntry(peer, SiloStatus.Active, DateTime.UnixEpoch.AddSeconds(1)));
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var sourceManager = new FakeMembershipManager(baseSnapshot);
        var sourceTopic = CreateMembershipTopic(local, sourceManager, serializer);
        var peerDigest = Assert.Single(sourceTopic.GetDigests());
        sourceManager.CurrentSnapshot = updatedSnapshot;
        var localDigest = Assert.Single(sourceTopic.GetDigests());

        var value = await sourceTopic.GetValue(
            localDigest,
            peerDigest,
            CancellationToken.None);

        Assert.NotNull(value);
        var update = serializer.Deserialize<MembershipTableSnapshotUpdate>(value.Payload);
        Assert.NotNull(update);
        Assert.NotNull(update.Diff);
        Assert.Null(update.Snapshot);
        var receiverManager = new FakeMembershipManager(baseSnapshot);
        var receiverTopic = CreateMembershipTopic(peer, receiverManager, serializer);
        var result = await receiverTopic.ApplyValue(value, CancellationToken.None);

        Assert.Equal(DisseminationApplyResult.Applied, result);
        Assert.Equal(updatedSnapshot.Version, receiverManager.CurrentSnapshot.Version);
        Assert.True(receiverManager.CurrentSnapshot.Entries.ContainsKey(peer));
    }

    [Fact]
    public async Task MembershipTopicApplyValueRejectsWrongTopicKey()
    {
        var local = CreateSilo(11121);
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var snapshot = CreateMembershipSnapshot(1, CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch));
        var topic = CreateMembershipTopic(local, new FakeMembershipManager(snapshot), serializer);
        var value = new DisseminationValue
        {
            Digest = new DisseminationTopicDigest("not-cluster", 1),
            Root = local,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
            Payload = serializer.SerializeToArray(new MembershipTableSnapshotUpdate { Snapshot = snapshot }),
        };

        var result = await topic.ApplyValue(value, CancellationToken.None);

        Assert.Equal(DisseminationApplyResult.Rejected, result);
    }

    [Fact]
    public async Task MembershipTopicApplyValueRejectsNullDeserializedPayload()
    {
        var local = CreateSilo(11122);
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var snapshot = CreateMembershipSnapshot(1, CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch));
        var topic = CreateMembershipTopic(local, new FakeMembershipManager(snapshot), serializer);
        var value = new DisseminationValue
        {
            Digest = new DisseminationTopicDigest("cluster", 1),
            Root = local,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
            Payload = serializer.SerializeToArray<MembershipTableSnapshotUpdate>(null!),
        };

        var result = await topic.ApplyValue(value, CancellationToken.None);

        Assert.Equal(DisseminationApplyResult.Rejected, result);
    }

    [Fact]
    public async Task MembershipTopicApplyValueRejectsDiffWithMismatchedDigestVersion()
    {
        var local = CreateSilo(11132);
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var currentSnapshot = CreateMembershipSnapshot(1, CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch));
        var manager = new FakeMembershipManager(currentSnapshot);
        var topic = CreateMembershipTopic(local, manager, serializer);
        var diff = new MembershipTableSnapshotDiff(
            currentSnapshot.Version,
            new MembershipVersion(2),
            ImmutableArray<MembershipEntry>.Empty,
            ImmutableArray<SiloAddress>.Empty);
        var value = new DisseminationValue
        {
            Digest = new DisseminationTopicDigest("cluster", 3),
            Root = local,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
            Payload = serializer.SerializeToArray(new MembershipTableSnapshotUpdate { Diff = diff }),
        };

        var result = await topic.ApplyValue(value, CancellationToken.None);

        Assert.Equal(DisseminationApplyResult.Rejected, result);
        Assert.Equal(currentSnapshot.Version, manager.CurrentSnapshot.Version);
    }

    [Fact]
    public async Task MembershipTopicApplyValueRejectsSnapshotWithMismatchedDigestVersion()
    {
        var local = CreateSilo(11133);
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var currentSnapshot = CreateMembershipSnapshot(1, CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch));
        var manager = new FakeMembershipManager(currentSnapshot);
        var topic = CreateMembershipTopic(local, manager, serializer);
        var proposedSnapshot = CreateMembershipSnapshot(2, CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch));
        var value = new DisseminationValue
        {
            Digest = new DisseminationTopicDigest("cluster", 3),
            Root = local,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
            Payload = serializer.SerializeToArray(new MembershipTableSnapshotUpdate { Snapshot = proposedSnapshot }),
        };

        var result = await topic.ApplyValue(value, CancellationToken.None);

        Assert.Equal(DisseminationApplyResult.Rejected, result);
        Assert.Equal(currentSnapshot.Version, manager.CurrentSnapshot.Version);
    }

    [Fact]
    public async Task MembershipTopicApplyValueReturnsObsoleteThenDuplicateForStaleVersions()
    {
        var local = CreateSilo(11123);
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var currentSnapshot = CreateMembershipSnapshot(5, CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch));
        var manager = new FakeMembershipManager(currentSnapshot);
        var topic = CreateMembershipTopic(local, manager, serializer);
        var olderSnapshot = CreateMembershipSnapshot(4, CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch));
        var olderValue = new DisseminationValue
        {
            Digest = new DisseminationTopicDigest("cluster", olderSnapshot.Version.Value),
            Root = local,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
            Payload = serializer.SerializeToArray(new MembershipTableSnapshotUpdate { Snapshot = olderSnapshot }),
        };
        var sameVersionValue = new DisseminationValue
        {
            Digest = new DisseminationTopicDigest("cluster", currentSnapshot.Version.Value),
            Root = local,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
            Payload = serializer.SerializeToArray(new MembershipTableSnapshotUpdate { Snapshot = currentSnapshot }),
        };

        var obsoleteResult = await topic.ApplyValue(olderValue, CancellationToken.None);
        var duplicateResult = await topic.ApplyValue(sameVersionValue, CancellationToken.None);

        Assert.Equal(DisseminationApplyResult.Obsolete, obsoleteResult);
        Assert.Equal(DisseminationApplyResult.Duplicate, duplicateResult);
        Assert.Equal(currentSnapshot.Version, manager.CurrentSnapshot.Version);
    }

    [Fact]
    public async Task MembershipTopicDoesNotReportAppliedWhenConcurrentUpdateAdvancesPastSnapshot()
    {
        var local = CreateSilo(11123);
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var currentSnapshot = CreateMembershipSnapshot(1, CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch));
        var concurrentSnapshot = CreateMembershipSnapshot(3, CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch));
        var manager = new FakeMembershipManager(currentSnapshot);
        manager.ProcessGossipSnapshotHandler = _ =>
        {
            manager.CurrentSnapshot = concurrentSnapshot;
            return Task.FromResult(false);
        };
        var topic = CreateMembershipTopic(local, manager, serializer);
        var proposedSnapshot = CreateMembershipSnapshot(2, CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch));
        var value = new DisseminationValue
        {
            Digest = new DisseminationTopicDigest("cluster", proposedSnapshot.Version.Value),
            Root = local,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
            Payload = serializer.SerializeToArray(new MembershipTableSnapshotUpdate { Snapshot = proposedSnapshot }),
        };

        var result = await topic.ApplyValue(value, CancellationToken.None);

        Assert.Equal(DisseminationApplyResult.Obsolete, result);
        Assert.Equal(concurrentSnapshot.Version, manager.CurrentSnapshot.Version);
    }

    [Fact]
    public async Task MembershipTopicReportsDuplicateWhenConcurrentUpdateInstallsSameVersion()
    {
        var local = CreateSilo(11123);
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var currentSnapshot = CreateMembershipSnapshot(1, CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch));
        var proposedSnapshot = CreateMembershipSnapshot(2, CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch));
        var manager = new FakeMembershipManager(currentSnapshot);
        manager.ProcessGossipSnapshotHandler = snapshot =>
        {
            manager.CurrentSnapshot = snapshot;
            return Task.FromResult(false);
        };
        var topic = CreateMembershipTopic(local, manager, serializer);
        var value = new DisseminationValue
        {
            Digest = new DisseminationTopicDigest("cluster", proposedSnapshot.Version.Value),
            Root = local,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
            Payload = serializer.SerializeToArray(new MembershipTableSnapshotUpdate { Snapshot = proposedSnapshot }),
        };

        var result = await topic.ApplyValue(value, CancellationToken.None);

        Assert.Equal(DisseminationApplyResult.Duplicate, result);
        Assert.Equal(proposedSnapshot.Version, manager.CurrentSnapshot.Version);
    }

    [Fact]
    public async Task MembershipTopicApplyValueRejectsDiffWithMismatchedBaseVersion()
    {
        var local = CreateSilo(11124);
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var currentSnapshot = CreateMembershipSnapshot(5, CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch));
        var manager = new FakeMembershipManager(currentSnapshot);
        var topic = CreateMembershipTopic(local, manager, serializer);
        var diff = new MembershipTableSnapshotDiff(
            baseVersion: new MembershipVersion(1),
            version: new MembershipVersion(6),
            updatedEntries: ImmutableArray<MembershipEntry>.Empty,
            removedSilos: ImmutableArray<SiloAddress>.Empty);
        var value = new DisseminationValue
        {
            Digest = new DisseminationTopicDigest("cluster", diff.Version.Value),
            Root = local,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
            Payload = serializer.SerializeToArray(new MembershipTableSnapshotUpdate { Diff = diff }),
        };

        var result = await topic.ApplyValue(value, CancellationToken.None);

        Assert.Equal(DisseminationApplyResult.Rejected, result);
        Assert.Equal(currentSnapshot.Version, manager.CurrentSnapshot.Version);
    }

    [Fact]
    public async Task MembershipTopicApplyValueRejectsUpdateContainingSnapshotAndDiff()
    {
        var local = CreateSilo(11129);
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var currentSnapshot = CreateMembershipSnapshot(1, CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch));
        var manager = new FakeMembershipManager(currentSnapshot);
        var topic = CreateMembershipTopic(local, manager, serializer);
        var diff = new MembershipTableSnapshotDiff(
            currentSnapshot.Version,
            new MembershipVersion(2),
            ImmutableArray<MembershipEntry>.Empty,
            ImmutableArray<SiloAddress>.Empty);
        var value = new DisseminationValue
        {
            Digest = new DisseminationTopicDigest("cluster", 2),
            Root = local,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
            Payload = serializer.SerializeToArray(new MembershipTableSnapshotUpdate
            {
                Snapshot = currentSnapshot,
                Diff = diff,
            }),
        };

        var result = await topic.ApplyValue(value, CancellationToken.None);

        Assert.Equal(DisseminationApplyResult.Rejected, result);
        Assert.Equal(currentSnapshot.Version, manager.CurrentSnapshot.Version);
    }

    [Theory]
    [InlineData(4, 5, (int)DisseminationApplyResult.Obsolete)]
    [InlineData(5, 5, (int)DisseminationApplyResult.Duplicate)]
    public async Task MembershipTopicApplyDiffRejectsNonNewerVersion(
        long diffVersion,
        long currentVersion,
        int expected)
    {
        var local = CreateSilo(11130);
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var currentSnapshot = CreateMembershipSnapshot(currentVersion, CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch));
        var manager = new FakeMembershipManager(currentSnapshot);
        var topic = CreateMembershipTopic(local, manager, serializer);
        var diff = new MembershipTableSnapshotDiff(
            new MembershipVersion(currentVersion - 1),
            new MembershipVersion(diffVersion),
            ImmutableArray<MembershipEntry>.Empty,
            ImmutableArray<SiloAddress>.Empty);
        var value = new DisseminationValue
        {
            Digest = new DisseminationTopicDigest("cluster", diffVersion),
            Root = local,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
            Payload = serializer.SerializeToArray(new MembershipTableSnapshotUpdate { Diff = diff }),
        };

        var result = await topic.ApplyValue(value, CancellationToken.None);

        Assert.Equal((DisseminationApplyResult)expected, result);
        Assert.Equal(currentSnapshot.Version, manager.CurrentSnapshot.Version);
    }

    [Fact]
    public async Task MembershipTopicDiffPreservesNewerIAmAliveTimeAndAppliesSuspectTimeChange()
    {
        var local = CreateSilo(11131);
        var peer = CreateSilo(11132);
        var startTime = DateTime.UnixEpoch;
        var baseEntry = CreateMembershipEntry(peer, SiloStatus.Active, startTime);
        baseEntry.IAmAliveTime = startTime.AddMinutes(2);
        baseEntry.SuspectTimes = [Tuple.Create(local, startTime.AddSeconds(1))];
        var updatedEntry = CreateMembershipEntry(peer, SiloStatus.Active, startTime);
        updatedEntry.IAmAliveTime = startTime.AddMinutes(1);
        updatedEntry.SuspectTimes = [Tuple.Create(local, startTime.AddSeconds(2))];
        var baseSnapshot = CreateMembershipSnapshot(1, baseEntry);
        var updatedSnapshot = CreateMembershipSnapshot(2, updatedEntry);
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var sourceManager = new FakeMembershipManager(baseSnapshot);
        var sourceTopic = CreateMembershipTopic(local, sourceManager, serializer);
        var peerDigest = Assert.Single(sourceTopic.GetDigests());
        sourceManager.CurrentSnapshot = updatedSnapshot;
        var localDigest = Assert.Single(sourceTopic.GetDigests());

        var value = await sourceTopic.GetValue(localDigest, peerDigest, CancellationToken.None);
        Assert.NotNull(value);
        var receiverManager = new FakeMembershipManager(baseSnapshot);
        var receiverTopic = CreateMembershipTopic(peer, receiverManager, serializer);

        var result = await receiverTopic.ApplyValue(value, CancellationToken.None);

        Assert.Equal(DisseminationApplyResult.Applied, result);
        var appliedEntry = receiverManager.CurrentSnapshot.Entries[peer];
        Assert.Equal(baseEntry.IAmAliveTime, appliedEntry.IAmAliveTime);
        var suspectTime = Assert.Single(appliedEntry.SuspectTimes!);
        Assert.Equal(updatedEntry.SuspectTimes![0], suspectTime);
    }

    [Fact]
    public async Task MembershipTopicGetValueReturnsNullForWrongKeyOrStaleDigest()
    {
        var local = CreateSilo(11125);
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var snapshot = CreateMembershipSnapshot(3, CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch));
        var topic = CreateMembershipTopic(local, new FakeMembershipManager(snapshot), serializer);

        var wrongKeyResult = await topic.GetValue(new DisseminationTopicDigest("not-cluster", 3), peerDigest: null, CancellationToken.None);
        var staleDigestResult = await topic.GetValue(new DisseminationTopicDigest("cluster", 99), peerDigest: null, CancellationToken.None);

        Assert.Null(wrongKeyResult);
        Assert.Null(staleDigestResult);
    }

    [Fact]
    public async Task MembershipTopicOnFallbackRequiredRefreshesMembershipWhenEnabled()
    {
        var local = CreateSilo(11126);
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var snapshot = CreateMembershipSnapshot(1, CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch));
        var manager = new FakeMembershipManager(snapshot);
        var topic = CreateMembershipTopic(local, manager, serializer, options => options.Dissemination.FallbackEnabled = true);

        await topic.OnFallbackRequired(peer: null, new DisseminationTopicDigest("cluster", 7), CancellationToken.None);

        var refreshCall = Assert.Single(manager.RefreshCalls);
        Assert.Equal(new MembershipVersion(7), refreshCall);
    }

    [Fact]
    public async Task MembershipTopicOnFallbackRequiredSkipsRefreshWhenDisabled()
    {
        var local = CreateSilo(11127);
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var snapshot = CreateMembershipSnapshot(1, CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch));
        var manager = new FakeMembershipManager(snapshot);
        var topic = CreateMembershipTopic(local, manager, serializer, options => options.Dissemination.FallbackEnabled = false);

        await topic.OnFallbackRequired(peer: null, new DisseminationTopicDigest("cluster", 7), CancellationToken.None);

        Assert.Empty(manager.RefreshCalls);
    }

    [Fact]
    public async Task MembershipTopicSnapshotHistoryEvictionFallsBackToFullSnapshotForOldPeerVersion()
    {
        var local = CreateSilo(11128);
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var initialSnapshot = CreateMembershipSnapshot(1, CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch));
        var manager = new FakeMembershipManager(initialSnapshot);
        var topic = CreateMembershipTopic(local, manager, serializer);
        Assert.Single(topic.GetDigests());

        // Exceed the 32-entry snapshot history so that version 1 is evicted.
        for (var version = 2; version <= 40; version++)
        {
            manager.CurrentSnapshot = CreateMembershipSnapshot(version, CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch));
            Assert.Single(topic.GetDigests());
        }

        var latestDigest = Assert.Single(topic.GetDigests());
        var evictedPeerDigest = new DisseminationTopicDigest("cluster", 1);

        var value = await topic.GetValue(latestDigest, evictedPeerDigest, CancellationToken.None);

        Assert.NotNull(value);
        var update = serializer.Deserialize<MembershipTableSnapshotUpdate>(value.Payload);
        Assert.NotNull(update);
        Assert.Null(update.Diff);
        Assert.NotNull(update.Snapshot);
        Assert.Equal(40, update.Snapshot.Version.Value);
    }

    [Fact]
    public void ManifestHashIsIndependentOfDictionaryOrdering()
    {
        var manifest1 = CreateManifest(
            ("grain-b", "placement", "random"),
            ("grain-a", "placement", "local"));
        var manifest2 = CreateManifest(
            ("grain-a", "placement", "local"),
            ("grain-b", "placement", "random"));

        Assert.Equal(ManifestHashCalculator.ComputeHash(manifest1), ManifestHashCalculator.ComputeHash(manifest2));
    }

    [Fact]
    public void ManifestHashCalculator_ChangesWhenGrainPropertyValueChanges()
    {
        var original = CreateManifest(("grain-a", "placement", "local"));
        var changed = CreateManifest(("grain-a", "placement", "random"));

        Assert.NotEqual(ManifestHashCalculator.ComputeHash(original), ManifestHashCalculator.ComputeHash(changed));
    }

    [Fact]
    public void ManifestHashCalculator_ChangesWhenGrainIsAdded()
    {
        var original = CreateManifest(("grain-a", "placement", "local"));
        var withExtraGrain = CreateManifest(("grain-a", "placement", "local"), ("grain-b", "placement", "local"));

        Assert.NotEqual(ManifestHashCalculator.ComputeHash(original), ManifestHashCalculator.ComputeHash(withExtraGrain));
    }

    [Fact]
    public void ManifestHashCalculator_DistinguishesNestedStructuresWithSameFlattenedStrings()
    {
        var firstGrains = System.Collections.Immutable.ImmutableDictionary.CreateBuilder<GrainType, GrainProperties>();
        firstGrains[GrainType.Create("G1")] = new GrainProperties(
            System.Collections.Immutable.ImmutableDictionary.Create<string, string>(StringComparer.Ordinal).Add("K", "V"));
        firstGrains[GrainType.Create("G2")] = new GrainProperties(
            System.Collections.Immutable.ImmutableDictionary.Create<string, string>(StringComparer.Ordinal));
        var first = new GrainManifest(
            firstGrains.ToImmutable(),
            System.Collections.Immutable.ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty);

        var secondGrains = System.Collections.Immutable.ImmutableDictionary.CreateBuilder<GrainType, GrainProperties>();
        secondGrains[GrainType.Create("G1")] = new GrainProperties(
            System.Collections.Immutable.ImmutableDictionary.Create<string, string>(StringComparer.Ordinal));
        secondGrains[GrainType.Create("K")] = new GrainProperties(
            System.Collections.Immutable.ImmutableDictionary.Create<string, string>(StringComparer.Ordinal).Add("V", "G2"));
        var second = new GrainManifest(
            secondGrains.ToImmutable(),
            System.Collections.Immutable.ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty);

        Assert.NotEqual(ManifestHashCalculator.ComputeHash(first), ManifestHashCalculator.ComputeHash(second));
    }

    [Fact]
    public void ManifestHashCalculator_PreservesRawTypeBytesAndInvalidUtf16()
    {
        static GrainManifest Create(byte grainType, string propertyValue)
        {
            var grains = System.Collections.Immutable.ImmutableDictionary.CreateBuilder<GrainType, GrainProperties>();
            grains[new GrainType([grainType])] = new GrainProperties(
                System.Collections.Immutable.ImmutableDictionary.Create<string, string>(StringComparer.Ordinal)
                    .Add("value", propertyValue));
            return new GrainManifest(
                grains.ToImmutable(),
                System.Collections.Immutable.ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty);
        }

        Assert.NotEqual(
            ManifestHashCalculator.ComputeHash(Create(0x80, "value")),
            ManifestHashCalculator.ComputeHash(Create(0x81, "value")));
        Assert.NotEqual(
            ManifestHashCalculator.ComputeHash(Create(0x80, "\uD800")),
            ManifestHashCalculator.ComputeHash(Create(0x80, "\uD801")));
    }

    [Fact]
    public void ManifestHashCalculator_DistinguishesNullAndEmptyPropertyValues()
    {
        static GrainManifest Create(string? propertyValue)
        {
            var grains = System.Collections.Immutable.ImmutableDictionary.CreateBuilder<GrainType, GrainProperties>();
            grains[GrainType.Create("grain")] = new GrainProperties(
                System.Collections.Immutable.ImmutableDictionary.Create<string, string>(StringComparer.Ordinal)
                    .Add("value", propertyValue!));
            return new GrainManifest(
                grains.ToImmutable(),
                System.Collections.Immutable.ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty);
        }

        Assert.NotEqual(
            ManifestHashCalculator.ComputeHash(Create(propertyValue: null)),
            ManifestHashCalculator.ComputeHash(Create(string.Empty)));
    }

    [Fact]
    public void ManifestHashCalculator_DistinguishesDefaultAndEmptyTypeIdentifiers()
    {
        static GrainManifest CreateGrainManifest(GrainType grainType)
        {
            var grains = System.Collections.Immutable.ImmutableDictionary.CreateBuilder<GrainType, GrainProperties>();
            grains[grainType] = new GrainProperties(
                System.Collections.Immutable.ImmutableDictionary.Create<string, string>(StringComparer.Ordinal));
            return new GrainManifest(
                grains.ToImmutable(),
                System.Collections.Immutable.ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty);
        }

        static GrainManifest CreateInterfaceManifest(GrainInterfaceType interfaceType)
        {
            var interfaces = System.Collections.Immutable.ImmutableDictionary.CreateBuilder<GrainInterfaceType, GrainInterfaceProperties>();
            interfaces[interfaceType] = new GrainInterfaceProperties(
                System.Collections.Immutable.ImmutableDictionary.Create<string, string>(StringComparer.Ordinal));
            return new GrainManifest(
                System.Collections.Immutable.ImmutableDictionary<GrainType, GrainProperties>.Empty,
                interfaces.ToImmutable());
        }

        Assert.NotEqual(
            ManifestHashCalculator.ComputeHash(CreateGrainManifest(default)),
            ManifestHashCalculator.ComputeHash(CreateGrainManifest(new GrainType(Array.Empty<byte>()))));
        Assert.NotEqual(
            ManifestHashCalculator.ComputeHash(CreateInterfaceManifest(default)),
            ManifestHashCalculator.ComputeHash(CreateInterfaceManifest(
                new GrainInterfaceType(new IdSpan(Array.Empty<byte>())))));
    }

    [Fact]
    public void ManifestHashCalculator_IsDeterministicForEmptyManifest()
    {
        var empty = new GrainManifest(
            System.Collections.Immutable.ImmutableDictionary<GrainType, GrainProperties>.Empty,
            System.Collections.Immutable.ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty);

        var first = ManifestHashCalculator.ComputeHash(empty);
        var second = ManifestHashCalculator.ComputeHash(empty);

        Assert.Equal(first, second);
        Assert.NotEqual(default(ManifestHash), first);
    }

    [Fact]
    public void ManifestHashCalculator_ChangesWhenInterfacePropertiesChange()
    {
        static GrainManifest CreateManifestWithInterface(string propertyValue)
        {
            var interfaces = System.Collections.Immutable.ImmutableDictionary.CreateBuilder<GrainInterfaceType, GrainInterfaceProperties>();
            var properties = System.Collections.Immutable.ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            properties["version"] = propertyValue;
            interfaces[GrainInterfaceType.Create("test.interface")] = new GrainInterfaceProperties(properties.ToImmutable());
            return new GrainManifest(
                System.Collections.Immutable.ImmutableDictionary<GrainType, GrainProperties>.Empty,
                interfaces.ToImmutable());
        }

        var original = CreateManifestWithInterface("1");
        var changed = CreateManifestWithInterface("2");
        var identical = CreateManifestWithInterface("1");

        Assert.NotEqual(ManifestHashCalculator.ComputeHash(original), ManifestHashCalculator.ComputeHash(changed));
        Assert.Equal(ManifestHashCalculator.ComputeHash(original), ManifestHashCalculator.ComputeHash(identical));
        Assert.NotEqual(ManifestHashCalculator.ComputeHash(original), ManifestHashCalculator.ComputeHash(new GrainManifest(
            System.Collections.Immutable.ImmutableDictionary<GrainType, GrainProperties>.Empty,
            System.Collections.Immutable.ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty)));
    }

    [Fact]
    public void WirePayloadsRoundTripThroughSerializer()
    {
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var sender = CreateSilo(11111);
        var value = new DisseminationValue
        {
            Digest = new DisseminationTopicDigest(FakeTopic.DefaultKey, version: 1),
            Root = sender,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
            Payload = new byte[] { 1, 2, 3 },
        };

        var gossip = RoundTrip(serializer, new DisseminationGossipBatch
        {
            Sender = sender,
            ValuesByTopic = CreateValueGroups(value),
        });
        var request = RoundTrip(serializer, new DisseminationAntiEntropyRequest
        {
            Sender = sender,
            DigestsByTopic = CreateAntiEntropyRequestDigests(FakeTopic.DefaultName, value.Digest),
        });
        var response = RoundTrip(serializer, new DisseminationAntiEntropyResponse
        {
            Sender = sender,
            ValuesByTopic = CreateValueGroups(value),
            SupportedTopics = [FakeTopic.DefaultName],
        });
        var manifestSummary = RoundTrip(serializer, new ClusterManifestHashSummary(
            new MajorMinorVersion(1, 2),
            ImmutableDictionary<SiloAddress, ManifestHash>.Empty.Add(sender, new ManifestHash("hash"))));

        Assert.Equal(value.Digest, Assert.Single(GetGossipValues(gossip)).Digest);
        Assert.Equal(value.Digest, Assert.Single(request.DigestsByTopic.Values).Single());
        Assert.Equal(value.Digest, Assert.Single(GetAntiEntropyResponseValues(response)).Digest);
        Assert.Equal(new[] { FakeTopic.DefaultName }, response.SupportedTopics);
        Assert.Equal(new ManifestHash("hash"), manifestSummary.SiloManifestHashes[sender]);
    }

    [Fact]
    public void OptionsValidatorRejectsInvalidFanoutBounds()
    {
        var options = new DisseminationOptions();
        options.Overlay.MinFanOutFactor = 8;
        options.Overlay.MaxFanOutFactor = 4;
        var result = new DisseminationOptionsValidator().Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
    }

    [Fact]
    public void OptionsValidatorRejectsInvalidExpectedUpdateCadence()
    {
        var options = new DeploymentLoadPublisherOptions();
        options.Dissemination.ExpectedUpdateCadence = TimeSpan.Zero;
        var result = new DeploymentLoadPublisherOptionsValidator().Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
    }

    [Fact]
    public void TopicOptionsUseExpectedUpdateCadenceDefaults()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), new DeploymentLoadPublisherOptions().Dissemination.ExpectedUpdateCadence);
        Assert.Equal(TimeSpan.FromSeconds(10), new ClusterMembershipOptions().Dissemination.ExpectedUpdateCadence);
    }

    [Fact]
    public void DisseminationOptionsUseBatchDefaults()
    {
        var options = new DisseminationOptions();

        Assert.Equal(1024 * 1024, options.MaxBatchBytes);
        Assert.Equal(8 * 1024, options.MaxBatchItems);
        Assert.Equal(options.MaxBatchBytes, new DisseminationTopicOptions().MaxPayloadBytes);
    }

    [Fact]
    public void OptionsValidatorAcceptsDefaultOptions()
    {
        var result = new DisseminationOptionsValidator().Validate(Options.DefaultName, new DisseminationOptions());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void OptionsValidatorRejectsNonPositiveMaxConcurrentSends()
    {
        var options = new DisseminationOptions { MaxConcurrentSends = 0 };

        var result = new DisseminationOptionsValidator().Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
    }

    [Fact]
    public void OptionsValidatorRejectsNonPositiveFailureBackoff()
    {
        var options = new DisseminationOptions { FailureBackoff = TimeSpan.Zero };

        var result = new DisseminationOptionsValidator().Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
    }

    [Fact]
    public void OptionsValidatorRejectsNonPositiveMaxBatchBytes()
    {
        var options = new DisseminationOptions { MaxBatchBytes = 0 };

        var result = new DisseminationOptionsValidator().Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
    }

    [Fact]
    public void OptionsValidatorRejectsNonPositiveMaxBatchItems()
    {
        var options = new DisseminationOptions { MaxBatchItems = 0 };

        var result = new DisseminationOptionsValidator().Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
    }

    [Fact]
    public void OptionsValidatorRejectsNonPositiveTargetHopCount()
    {
        var options = new DisseminationOptions();
        options.Overlay.TargetHopCount = 0;

        var result = new DisseminationOptionsValidator().Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
    }

    [Fact]
    public void OptionsValidatorRejectsNonPositiveMinFanOutFactor()
    {
        var options = new DisseminationOptions();
        options.Overlay.MinFanOutFactor = 0;

        var result = new DisseminationOptionsValidator().Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
    }

    [Fact]
    public void OptionsValidatorRejectsNonPositiveAntiEntropyInterval()
    {
        var options = new DisseminationOptions();
        options.Overlay.AntiEntropyInterval = TimeSpan.Zero;

        var result = new DisseminationOptionsValidator().Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
    }

    [Fact]
    public void OptionsValidatorRejectsAntiEntropyIntervalBeyondTimerMaximum()
    {
        var options = new DisseminationOptions();
        options.Overlay.AntiEntropyInterval = TimeSpan.FromMilliseconds(uint.MaxValue);

        var result = new DisseminationOptionsValidator().Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
    }

    [Fact]
    public void OptionsValidatorRejectsNonPositiveAntiEntropyPeerCount()
    {
        var options = new DisseminationOptions();
        options.Overlay.AntiEntropyPeerCount = 0;

        var result = new DisseminationOptionsValidator().Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
    }

    [Fact]
    public void TopicOptionsValidatorAcceptsDefaultOptions()
    {
        var result = DisseminationTopicOptionsValidator.Validate("Test", new DisseminationTopicOptions());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void TopicOptionsValidatorRejectsNonPositiveMaxPendingItemCount()
    {
        var options = new DisseminationTopicOptions { MaxPendingItemCount = 0 };

        var result = DisseminationTopicOptionsValidator.Validate("Test", options);

        Assert.True(result.Failed);
    }

    [Fact]
    public void TopicOptionsValidatorRejectsNonPositiveMaxCoalescingDelay()
    {
        var options = new DisseminationTopicOptions { MaxCoalescingDelay = TimeSpan.Zero };

        var result = DisseminationTopicOptionsValidator.Validate("Test", options);

        Assert.True(result.Failed);
    }

    [Fact]
    public void TopicOptionsValidatorRejectsMaxCoalescingDelayBeyondTimerMaximum()
    {
        var options = new DisseminationTopicOptions
        {
            MaxCoalescingDelay = TimeSpan.FromMilliseconds(uint.MaxValue),
            StaleItemTtl = TimeSpan.FromMilliseconds(uint.MaxValue + 1d),
        };

        var result = DisseminationTopicOptionsValidator.Validate("Test", options);

        Assert.True(result.Failed);
    }

    [Fact]
    public void TopicOptionsValidatorRejectsStaleItemTtlNotGreaterThanCoalescingDelay()
    {
        var options = new DisseminationTopicOptions
        {
            MaxCoalescingDelay = TimeSpan.FromSeconds(5),
            StaleItemTtl = TimeSpan.FromSeconds(5),
        };

        var result = DisseminationTopicOptionsValidator.Validate("Test", options);

        Assert.True(result.Failed);
    }

    [Fact]
    public void TopicOptionsValidatorRejectsNonPositiveMaxPayloadBytes()
    {
        var options = new DisseminationTopicOptions { MaxPayloadBytes = 0 };

        var result = DisseminationTopicOptionsValidator.Validate("Test", options);

        Assert.True(result.Failed);
    }

    [Fact]
    public void ClusterMembershipOptionsDisseminationValidatorRejectsNonPositiveMaxPendingItemCount()
    {
        var options = new ClusterMembershipOptions();
        options.Dissemination.MaxPendingItemCount = 0;

        var result = new ClusterMembershipOptionsDisseminationValidator().Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
    }

    [Fact]
    public void ApplyDisseminatedRuntimeStatistics_AppliesNewerRejectsInactiveObsoleteAndDuplicate()
    {
        var local = CreateSilo(21001);
        var peer = CreateSilo(21002);
        var statusOracle = new FakeSiloStatusOracle();
        var publisher = CreateDeploymentLoadPublisher(local, statusOracle);
        var baseline = CreateStatistics(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var rejectedForInactive = publisher.ApplyDisseminatedRuntimeStatistics(peer, baseline);

        statusOracle.SetStatus(peer, SiloStatus.Active);
        var applied = publisher.ApplyDisseminatedRuntimeStatistics(peer, baseline);
        var duplicate = publisher.ApplyDisseminatedRuntimeStatistics(peer, baseline);
        var obsolete = publisher.ApplyDisseminatedRuntimeStatistics(peer, CreateStatistics(baseline.DateTime.AddSeconds(-1)));

        Assert.Equal(DisseminationApplyResult.Rejected, rejectedForInactive);
        Assert.Equal(DisseminationApplyResult.Applied, applied);
        Assert.Equal(DisseminationApplyResult.Duplicate, duplicate);
        Assert.Equal(DisseminationApplyResult.Obsolete, obsolete);
        Assert.Equal(baseline.DateTime, publisher.PeriodicStatistics[peer].DateTime);
    }

    [Fact]
    public async Task RuntimeStatisticsConcurrentNewerAndOlderUpdatesRemainMonotonic()
    {
        var local = CreateSilo(21019);
        var peer = CreateSilo(21020);
        using var statusCheckEntered = new ManualResetEventSlim();
        using var releaseStatusCheck = new ManualResetEventSlim();
        using var olderStatusCheckEntered = new ManualResetEventSlim();
        var statusCalls = 0;
        var statusOracle = new FakeSiloStatusOracle
        {
            GetStatusHandler = _ =>
            {
                if (Interlocked.Increment(ref statusCalls) == 1)
                {
                    statusCheckEntered.Set();
                    releaseStatusCheck.Wait();
                }
                else
                {
                    olderStatusCheckEntered.Set();
                }

                return SiloStatus.Active;
            },
        };
        statusOracle.SetStatus(peer, SiloStatus.Active);
        var publisher = CreateDeploymentLoadPublisher(local, statusOracle);
        var older = CreateStatistics(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var newer = CreateStatistics(older.DateTime.AddSeconds(1));

        var newerUpdate = Task.Run(
            () => publisher.ApplyDisseminatedRuntimeStatistics(peer, newer),
            TestContext.Current.CancellationToken);
        Assert.True(
            statusCheckEntered.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken),
            "The newer update did not enter the status/version boundary.");
        var olderUpdate = Task.Run(
            () => publisher.UpdateRuntimeStatistics(peer, older),
            TestContext.Current.CancellationToken);
        Assert.False(
            olderStatusCheckEntered.Wait(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken),
            "The older update entered the status/version boundary while the newer update still owned it.");
        releaseStatusCheck.Set();

        Assert.Equal(DisseminationApplyResult.Applied, await newerUpdate);
        await olderUpdate;
        Assert.Equal(newer.DateTime, publisher.PeriodicStatistics[peer].DateTime);
    }

    [Fact]
    public async Task RuntimeStatisticsTerminationRaceCannotResurrectRemovedSilo()
    {
        var local = CreateSilo(21021);
        var peer = CreateSilo(21022);
        using var statusCheckEntered = new ManualResetEventSlim();
        using var releaseStatusCheck = new ManualResetEventSlim();
        var blockStatusCheck = false;
        var statusOracle = new FakeSiloStatusOracle();
        statusOracle.GetStatusHandler = silo =>
        {
            var status = statusOracle.GetStoredStatus(silo);
            if (blockStatusCheck)
            {
                statusCheckEntered.Set();
                releaseStatusCheck.Wait();
            }

            return status;
        };
        statusOracle.SetStatus(peer, SiloStatus.Active);
        var publisher = CreateDeploymentLoadPublisher(local, statusOracle);
        var baseline = CreateStatistics(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(DisseminationApplyResult.Applied, publisher.ApplyDisseminatedRuntimeStatistics(peer, baseline));
        blockStatusCheck = true;
        var lateStatistics = CreateStatistics(baseline.DateTime.AddSeconds(1));

        var lateUpdate = Task.Run(
            () => publisher.ApplyDisseminatedRuntimeStatistics(peer, lateStatistics),
            TestContext.Current.CancellationToken);
        Assert.True(
            statusCheckEntered.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken),
            "The late update did not enter the status/version boundary.");
        statusOracle.SetStatus(peer, SiloStatus.Dead);
        var termination = Task.Run(
            () => publisher.OnSiloStatusChange(peer, SiloStatus.Dead),
            TestContext.Current.CancellationToken);
        releaseStatusCheck.Set();

        await lateUpdate;
        await termination;
        Assert.False(publisher.PeriodicStatistics.ContainsKey(peer));

        var postTermination = publisher.ApplyDisseminatedRuntimeStatistics(
            peer,
            CreateStatistics(lateStatistics.DateTime.AddSeconds(1)));
        Assert.Equal(DisseminationApplyResult.Rejected, postTermination);
        Assert.False(publisher.PeriodicStatistics.ContainsKey(peer));
    }

    [Fact]
    public void RuntimeStatisticsRemovedEventObservesCompletedRemoval()
    {
        var local = CreateSilo(21023);
        var peer = CreateSilo(21024);
        var statusOracle = new FakeSiloStatusOracle();
        statusOracle.SetStatus(peer, SiloStatus.Active);
        var publisher = CreateDeploymentLoadPublisher(local, statusOracle);
        var statistics = CreateStatistics(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(DisseminationApplyResult.Applied, publisher.ApplyDisseminatedRuntimeStatistics(peer, statistics));
        bool? entryPresentWhenRemovedEventRaised = null;
        using var subscription = DeploymentLoadPublisherEvents.AllEvents.Subscribe(
            new ActionObserver<DeploymentLoadPublisherEvents.DeploymentLoadPublisherEvent>(evt =>
            {
                if (evt is DeploymentLoadPublisherEvents.Removed removed && removed.RemovedSilo.Equals(peer))
                {
                    entryPresentWhenRemovedEventRaised = publisher.PeriodicStatistics.ContainsKey(peer);
                }
            }));

        statusOracle.SetStatus(peer, SiloStatus.Dead);
        publisher.OnSiloStatusChange(peer, SiloStatus.Dead);

        Assert.False(entryPresentWhenRemovedEventRaised);
    }

    [Fact]
    public async Task RuntimeStatisticsUpdateCallbackDoesNotHoldMutationLock()
    {
        var local = CreateSilo(21025);
        var peer = CreateSilo(21026);
        var statusOracle = new FakeSiloStatusOracle();
        statusOracle.SetStatus(peer, SiloStatus.Active);
        var publisher = CreateDeploymentLoadPublisher(local, statusOracle);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCount = 0;
        publisher.SubscribeToStatisticsChangeEvents(new DelegateStatisticsListener(
            onUpdate: (_, _) =>
            {
                if (Interlocked.Increment(ref callbackCount) == 1)
                {
                    callbackEntered.TrySetResult();
                    releaseCallback.Task.GetAwaiter().GetResult();
                }
            }));
        var first = CreateStatistics(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var second = CreateStatistics(first.DateTime.AddSeconds(1));

        var firstUpdate = Task.Run(
            () => publisher.ApplyDisseminatedRuntimeStatistics(peer, first),
            TestContext.Current.CancellationToken);
        await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        var secondResult = await Task.Run(
            () => publisher.ApplyDisseminatedRuntimeStatistics(peer, second),
            TestContext.Current.CancellationToken).WaitAsync(
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken);

        Assert.Equal(DisseminationApplyResult.Applied, secondResult);
        Assert.Equal(second.DateTime, publisher.PeriodicStatistics[peer].DateTime);
        Assert.Equal(1, Volatile.Read(ref callbackCount));
        releaseCallback.TrySetResult();
        await firstUpdate;
        Assert.Equal(2, callbackCount);
    }

    [Fact]
    public async Task RuntimeStatisticsRemovalCallbackDoesNotHoldMutationLockOrPermitResurrection()
    {
        var local = CreateSilo(21027);
        var peer = CreateSilo(21028);
        var statusOracle = new FakeSiloStatusOracle();
        statusOracle.SetStatus(peer, SiloStatus.Active);
        var publisher = CreateDeploymentLoadPublisher(local, statusOracle);
        var baseline = CreateStatistics(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(DisseminationApplyResult.Applied, publisher.ApplyDisseminatedRuntimeStatistics(peer, baseline));
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        publisher.SubscribeToStatisticsChangeEvents(new DelegateStatisticsListener(
            onRemove: _ =>
            {
                callbackEntered.TrySetResult();
                releaseCallback.Task.GetAwaiter().GetResult();
            }));
        statusOracle.SetStatus(peer, SiloStatus.Dead);

        var removal = Task.Run(
            () => publisher.OnSiloStatusChange(peer, SiloStatus.Dead),
            TestContext.Current.CancellationToken);
        await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        var lateResult = await Task.Run(
            () => publisher.ApplyDisseminatedRuntimeStatistics(
                peer,
                CreateStatistics(baseline.DateTime.AddSeconds(1))),
            TestContext.Current.CancellationToken).WaitAsync(
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken);

        Assert.Equal(DisseminationApplyResult.Rejected, lateResult);
        Assert.False(publisher.PeriodicStatistics.ContainsKey(peer));
        releaseCallback.TrySetResult();
        await removal;
    }

    [Fact]
    public void IsRuntimeStatisticsObsolete_ReflectsSiloActivityAndStatisticsRecency()
    {
        var local = CreateSilo(21003);
        var peer = CreateSilo(21004);
        var statusOracle = new FakeSiloStatusOracle();
        var publisher = CreateDeploymentLoadPublisher(local, statusOracle);

        Assert.True(publisher.IsRuntimeStatisticsObsolete(peer, DateTime.UtcNow.Ticks));

        statusOracle.SetStatus(peer, SiloStatus.Active);
        Assert.False(publisher.IsRuntimeStatisticsObsolete(peer, DateTime.UtcNow.Ticks));

        var current = CreateStatistics(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        publisher.ApplyDisseminatedRuntimeStatistics(peer, current);

        Assert.False(publisher.IsRuntimeStatisticsObsolete(peer, current.DateTime.Ticks + 1));
        Assert.True(publisher.IsRuntimeStatisticsObsolete(peer, current.DateTime.Ticks - 1));
        // Boundary: a request for the exact same timestamp as the stored statistics is not obsolete (strictly-greater comparison).
        Assert.False(publisher.IsRuntimeStatisticsObsolete(peer, current.DateTime.Ticks));
    }

    [Fact]
    public void GetActiveSilosForDissemination_ReturnsOnlyActiveSilos()
    {
        var local = CreateSilo(21005);
        var activePeer = CreateSilo(21006);
        var joiningPeer = CreateSilo(21007);
        var statusOracle = new FakeSiloStatusOracle();
        statusOracle.SetStatus(activePeer, SiloStatus.Active);
        statusOracle.SetStatus(joiningPeer, SiloStatus.Joining);
        var publisher = CreateDeploymentLoadPublisher(local, statusOracle);

        var activeSilos = publisher.GetActiveSilosForDissemination();

        Assert.Equal(new[] { activePeer }, activeSilos);
    }

    [Fact]
    public async Task TryPublishStatisticsViaDisseminationPublishesWhenEnabled()
    {
        using var serializerProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serializerProvider.GetRequiredService<Serializer>();
        var local = CreateSilo(21008);
        var peer = CreateSilo(21009);
        var statusOracle = new FakeSiloStatusOracle();
        statusOracle.SetStatus(local, SiloStatus.Active);
        statusOracle.SetStatus(peer, SiloStatus.Active);
        var services = new MutableServiceProvider();
        var grainFactory = new RecordingGrainFactory();
        var publisher = CreateDeploymentLoadPublisher(local, statusOracle, grainFactory, services);
        var topic = CreateDeploymentLoadTopic(
            publisher,
            serializer,
            options => options.Dissemination.Enabled = true);
        var transport = new FakeTransport(local, peer);
        var dissemination = CreateService(transport, [topic]);
        services.Add(dissemination);
        services.Add(topic);

        var result = await publisher.TryPublishStatisticsViaDissemination(
            CreateStatistics(DateTime.UtcNow));
        await dissemination.StopAsync(CancellationToken.None);

        Assert.True(result);
        var batch = Assert.Single(transport.GossipBatches);
        Assert.Equal(peer, batch.Peer);
        Assert.Single(GetGossipValues(batch.Batch));
    }

    [Fact]
    public async Task PublishStatistics_DisseminationSuccessSkipsLegacySendForConfirmedPeer()
    {
        using var serializerProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serializerProvider.GetRequiredService<Serializer>();
        var local = CreateSilo(21015);
        var peer = CreateSilo(21016);
        var statusOracle = new FakeSiloStatusOracle();
        statusOracle.SetStatus(local, SiloStatus.Active);
        statusOracle.SetStatus(peer, SiloStatus.Active);
        var services = new MutableServiceProvider();
        var grainFactory = new RecordingGrainFactory();
        var publisher = CreateDeploymentLoadPublisher(local, statusOracle, grainFactory, services);
        var topic = CreateDeploymentLoadTopic(
            publisher,
            serializer,
            options => options.Dissemination.Enabled = true);
        var transport = new FakeTransport(local, peer);
        var dissemination = CreateService(transport, [topic]);
        services.Add(dissemination);
        services.Add(topic);
        await dissemination.ReceiveAntiEntropy(new DisseminationAntiEntropyRequest
        {
            Sender = peer,
            DigestsByTopic = CreateAntiEntropyRequestDigests(
                topic.Name,
                new DisseminationTopicDigest(local.ToParsableString(), long.MinValue)),
        }, CancellationToken.None);

        await publisher.PublishStatistics();
        await dissemination.StopAsync(CancellationToken.None);

        Assert.Empty(grainFactory.SystemTargetRequests);
        Assert.Single(transport.GossipBatches);
    }

    [Fact]
    public async Task PublishStatistics_DisseminationSuccessUsesLegacySendForUnconfirmedPeer()
    {
        using var serializerProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serializerProvider.GetRequiredService<Serializer>();
        var local = CreateSilo(21017);
        var peer = CreateSilo(21018);
        var statusOracle = new FakeSiloStatusOracle();
        statusOracle.SetStatus(local, SiloStatus.Active);
        statusOracle.SetStatus(peer, SiloStatus.Active);
        var services = new MutableServiceProvider();
        var directTarget = new FakeDeploymentLoadPublisherTarget();
        var grainFactory = new RecordingGrainFactory
        {
            Resolver = (type, _) => type == typeof(IDeploymentLoadPublisher)
                ? directTarget
                : throw new NotSupportedException(),
        };
        var publisher = CreateDeploymentLoadPublisher(local, statusOracle, grainFactory, services);
        var topic = CreateDeploymentLoadTopic(
            publisher,
            serializer,
            options => options.Dissemination.Enabled = true);
        var transport = new FakeTransport(local, peer);
        var dissemination = CreateService(transport, [topic]);
        services.Add(dissemination);
        services.Add(topic);

        await publisher.PublishStatistics();
        await dissemination.StopAsync(CancellationToken.None);

        Assert.Single(transport.GossipBatches);
        var update = Assert.Single(directTarget.Updates);
        Assert.Equal(local, update.Source);
        Assert.Equal(peer, Assert.Single(grainFactory.SystemTargetRequests).Destination);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task TryPublishStatisticsViaDisseminationReturnsFalseWhenDependencyIsMissingOrTopicDisabled(
        bool registerDissemination,
        bool registerTopic,
        bool topicEnabled)
    {
        using var serializerProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serializerProvider.GetRequiredService<Serializer>();
        var local = CreateSilo(21010);
        var statusOracle = new FakeSiloStatusOracle();
        statusOracle.SetStatus(local, SiloStatus.Active);
        var services = new MutableServiceProvider();
        var publisher = CreateDeploymentLoadPublisher(local, statusOracle, serviceProvider: services);
        var topic = CreateDeploymentLoadTopic(
            publisher,
            serializer,
            options => options.Dissemination.Enabled = topicEnabled);
        var dissemination = CreateService(new FakeTransport(local), [topic]);
        if (registerDissemination)
        {
            services.Add(dissemination);
        }

        if (registerTopic)
        {
            services.Add(topic);
        }

        var result = await publisher.TryPublishStatisticsViaDissemination(
            CreateStatistics(DateTime.UtcNow));

        Assert.False(result);
    }

    [Fact]
    public async Task TryPublishStatisticsViaDisseminationReturnsFalseWhenProtocolDeclinesTopic()
    {
        using var serializerProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serializerProvider.GetRequiredService<Serializer>();
        var local = CreateSilo(21011);
        var statusOracle = new FakeSiloStatusOracle();
        statusOracle.SetStatus(local, SiloStatus.Active);
        var services = new MutableServiceProvider();
        var publisher = CreateDeploymentLoadPublisher(local, statusOracle, serviceProvider: services);
        var topic = CreateDeploymentLoadTopic(
            publisher,
            serializer,
            options => options.Dissemination.Enabled = true);
        services.Add(CreateService(new FakeTransport(local), []));
        services.Add(topic);

        var result = await publisher.TryPublishStatisticsViaDissemination(
            CreateStatistics(DateTime.UtcNow));

        Assert.False(result);
    }

    [Fact]
    public async Task TryPublishStatisticsViaDisseminationReturnsFalseWhenPublishThrows()
    {
        using var serializerProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serializerProvider.GetRequiredService<Serializer>();
        var local = CreateSilo(21012);
        var statusOracle = new FakeSiloStatusOracle();
        statusOracle.SetStatus(local, SiloStatus.Active);
        var services = new MutableServiceProvider();
        var publisher = CreateDeploymentLoadPublisher(local, statusOracle, serviceProvider: services);
        var topic = CreateDeploymentLoadTopic(
            publisher,
            serializer,
            options => options.Dissemination.Enabled = true);
        var transport = new FakeTransport(local)
        {
            GetMembershipHandler = static () => throw new InvalidOperationException("test failure"),
        };
        services.Add(CreateService(transport, [topic]));
        services.Add(topic);

        var result = await publisher.TryPublishStatisticsViaDissemination(
            CreateStatistics(DateTime.UtcNow));

        Assert.False(result);
    }

    [Fact]
    public async Task PublishStatisticsFallsBackToDirectSendWhenDisseminationThrows()
    {
        using var serializerProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serializerProvider.GetRequiredService<Serializer>();
        var local = CreateSilo(21013);
        var peer = CreateSilo(21014);
        var statusOracle = new FakeSiloStatusOracle();
        statusOracle.SetStatus(local, SiloStatus.Active);
        statusOracle.SetStatus(peer, SiloStatus.Active);
        var services = new MutableServiceProvider();
        var directTarget = new FakeDeploymentLoadPublisherTarget();
        var grainFactory = new RecordingGrainFactory
        {
            Resolver = (type, _) => type == typeof(IDeploymentLoadPublisher)
                ? directTarget
                : throw new NotSupportedException(),
        };
        var publisher = CreateDeploymentLoadPublisher(
            local,
            statusOracle,
            grainFactory,
            services);
        var topic = CreateDeploymentLoadTopic(
            publisher,
            serializer,
            options => options.Dissemination.Enabled = true);
        var transport = new FakeTransport(local, peer)
        {
            GetMembershipHandler = static () => throw new InvalidOperationException("test failure"),
        };
        services.Add(CreateService(transport, [topic]));
        services.Add(topic);

        await publisher.PublishStatistics();

        var update = Assert.Single(directTarget.Updates);
        Assert.Equal(local, update.Source);
        Assert.Equal(publisher.LocalRuntimeStatistics.DateTime, update.Statistics.DateTime);
        Assert.Equal(peer, Assert.Single(grainFactory.SystemTargetRequests).Destination);
        Assert.Equal(1, statusOracle.ApproximateStatusesRequests);
    }

    [Fact]
    public void DeploymentLoadTopic_CreateItemProducesDigestKeyedByOriginWithSerializedPayload()
    {
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var local = CreateSilo(21101);
        var publisher = CreateDeploymentLoadPublisher(local, new FakeSiloStatusOracle());
        var topic = CreateDeploymentLoadTopic(publisher, serializer);
        var statistics = CreateStatistics(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var item = topic.CreateItem(local, statistics);

        Assert.Equal(local.ToParsableString(), item.Digest.Key);
        Assert.Equal(statistics.DateTime.Ticks, item.Digest.Version);
        Assert.Equal(local, item.Root);
        var deserialized = serializer.Deserialize<SiloRuntimeStatistics>(item.Payload) ?? throw new InvalidOperationException("Expected non-null statistics.");
        Assert.Equal(statistics.DateTime, deserialized.DateTime);
        Assert.Equal(statistics.ActivationCount, deserialized.ActivationCount);
    }

    [Fact]
    public void DeploymentLoadTopic_GetDigestsSortsAndMarksStaleOrMissingActiveSilosAsMinVersion()
    {
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var local = CreateSilo(21102);
        var siloWithoutStats = CreateSilo(21103);
        var siloWithFreshStats = CreateSilo(21104);
        var statusOracle = new FakeSiloStatusOracle();
        statusOracle.SetStatus(siloWithFreshStats, SiloStatus.Active);
        statusOracle.SetStatus(siloWithoutStats, SiloStatus.Active);
        var publisher = CreateDeploymentLoadPublisher(local, statusOracle);
        var topic = CreateDeploymentLoadTopic(publisher, serializer);
        var freshStats = CreateStatistics(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        publisher.ApplyDisseminatedRuntimeStatistics(siloWithFreshStats, freshStats);

        var digests = topic.GetDigests();

        Assert.Equal(2, digests.Count);
        Assert.Equal(digests.OrderBy(d => d.Key, StringComparer.Ordinal).Select(d => d.Key), digests.Select(d => d.Key));
        var freshDigest = digests.Single(d => d.Key == siloWithFreshStats.ToParsableString());
        var missingDigest = digests.Single(d => d.Key == siloWithoutStats.ToParsableString());
        Assert.Equal(freshStats.DateTime.Ticks, freshDigest.Version);
        Assert.Equal(long.MinValue, missingDigest.Version);
    }

    [Fact]
    public async Task DeploymentLoadTopic_GetValueReturnsNullForMalformedKey()
    {
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var local = CreateSilo(21105);
        var publisher = CreateDeploymentLoadPublisher(local, new FakeSiloStatusOracle());
        var topic = CreateDeploymentLoadTopic(publisher, serializer);

        var value = await topic.GetValue(new DisseminationTopicDigest("not-a-silo-address", 1), peerDigest: null, CancellationToken.None);

        Assert.Null(value);
    }

    [Fact]
    public async Task DeploymentLoadTopic_GetValueReturnsNullWhenStatisticsMissingOrDigestIsNewerThanLocal()
    {
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var local = CreateSilo(21106);
        var peer = CreateSilo(21107);
        var statusOracle = new FakeSiloStatusOracle();
        statusOracle.SetStatus(peer, SiloStatus.Active);
        var publisher = CreateDeploymentLoadPublisher(local, statusOracle);
        var topic = CreateDeploymentLoadTopic(publisher, serializer);

        var missingResult = await topic.GetValue(new DisseminationTopicDigest(peer.ToParsableString(), 1), peerDigest: null, CancellationToken.None);

        var statistics = CreateStatistics(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        publisher.ApplyDisseminatedRuntimeStatistics(peer, statistics);
        var staleResult = await topic.GetValue(
            new DisseminationTopicDigest(peer.ToParsableString(), statistics.DateTime.Ticks + 1),
            peerDigest: null,
            CancellationToken.None);

        Assert.Null(missingResult);
        Assert.Null(staleResult);
    }

    [Fact]
    public async Task DeploymentLoadTopic_GetValueReturnsSerializedStatisticsWhenDigestIsCurrent()
    {
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var local = CreateSilo(21108);
        var peer = CreateSilo(21109);
        var statusOracle = new FakeSiloStatusOracle();
        statusOracle.SetStatus(peer, SiloStatus.Active);
        var publisher = CreateDeploymentLoadPublisher(local, statusOracle);
        var topic = CreateDeploymentLoadTopic(publisher, serializer);
        var statistics = CreateStatistics(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        publisher.ApplyDisseminatedRuntimeStatistics(peer, statistics);

        var value = await topic.GetValue(new DisseminationTopicDigest(peer.ToParsableString(), statistics.DateTime.Ticks), peerDigest: null, CancellationToken.None);

        Assert.NotNull(value);
        Assert.Equal(peer, value.Root);
        var roundTripped = serializer.Deserialize<SiloRuntimeStatistics>(value.Payload) ?? throw new InvalidOperationException("Expected non-null statistics.");
        Assert.Equal(statistics.DateTime, roundTripped.DateTime);
    }

    [Fact]
    public async Task DeploymentLoadTopic_ApplyValueRejectsMalformedKey()
    {
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var local = CreateSilo(21110);
        var publisher = CreateDeploymentLoadPublisher(local, new FakeSiloStatusOracle());
        var topic = CreateDeploymentLoadTopic(publisher, serializer);
        var value = new DisseminationValue
        {
            Digest = new DisseminationTopicDigest("not-a-silo-address", 1),
            Root = local,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
            Payload = serializer.SerializeToArray(CreateStatistics(DateTime.UtcNow)),
        };

        var result = await topic.ApplyValue(value, CancellationToken.None);

        Assert.Equal(DisseminationApplyResult.Rejected, result);
    }

    [Fact]
    public async Task DeploymentLoadTopic_ApplyValueRejectsNullDeserializedPayload()
    {
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var local = CreateSilo(21111);
        var peer = CreateSilo(21112);
        var statusOracle = new FakeSiloStatusOracle();
        statusOracle.SetStatus(peer, SiloStatus.Active);
        var publisher = CreateDeploymentLoadPublisher(local, statusOracle);
        var topic = CreateDeploymentLoadTopic(publisher, serializer);
        var value = new DisseminationValue
        {
            Digest = new DisseminationTopicDigest(peer.ToParsableString(), 1),
            Root = peer,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
            Payload = serializer.SerializeToArray<SiloRuntimeStatistics>(null!),
        };

        var result = await topic.ApplyValue(value, CancellationToken.None);

        Assert.Equal(DisseminationApplyResult.Rejected, result);
    }

    [Fact]
    public async Task DeploymentLoadTopic_ApplyValueRejectsDigestPayloadMismatch()
    {
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var local = CreateSilo(21115);
        var peer = CreateSilo(21116);
        var statusOracle = new FakeSiloStatusOracle();
        statusOracle.SetStatus(peer, SiloStatus.Active);
        var publisher = CreateDeploymentLoadPublisher(local, statusOracle);
        var topic = CreateDeploymentLoadTopic(publisher, serializer);
        var statistics = CreateStatistics(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var mismatchedRoot = new DisseminationValue
        {
            Digest = new DisseminationTopicDigest(peer.ToParsableString(), statistics.DateTime.Ticks),
            Root = local,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
            Payload = serializer.SerializeToArray(statistics),
        };
        var mismatchedVersion = new DisseminationValue
        {
            Digest = new DisseminationTopicDigest(peer.ToParsableString(), statistics.DateTime.Ticks + 1),
            Root = peer,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
            Payload = serializer.SerializeToArray(statistics),
        };

        var rootResult = await topic.ApplyValue(mismatchedRoot, CancellationToken.None);
        var versionResult = await topic.ApplyValue(mismatchedVersion, CancellationToken.None);

        Assert.Equal(DisseminationApplyResult.Rejected, rootResult);
        Assert.Equal(DisseminationApplyResult.Rejected, versionResult);
    }

    [Fact]
    public async Task DeploymentLoadTopic_ApplyValueDelegatesResultToPublisher()
    {
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var local = CreateSilo(21113);
        var peer = CreateSilo(21114);
        var statusOracle = new FakeSiloStatusOracle();
        var publisher = CreateDeploymentLoadPublisher(local, statusOracle);
        var topic = CreateDeploymentLoadTopic(publisher, serializer);

        DisseminationValue CreateValue(DateTime dateTime) => new()
        {
            Digest = new DisseminationTopicDigest(peer.ToParsableString(), dateTime.Ticks),
            Root = peer,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
            Payload = serializer.SerializeToArray(CreateStatistics(dateTime)),
        };

        var rejectedResult = await topic.ApplyValue(CreateValue(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)), CancellationToken.None);

        statusOracle.SetStatus(peer, SiloStatus.Active);
        var appliedResult = await topic.ApplyValue(CreateValue(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)), CancellationToken.None);
        var duplicateResult = await topic.ApplyValue(CreateValue(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)), CancellationToken.None);
        var obsoleteResult = await topic.ApplyValue(CreateValue(new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc)), CancellationToken.None);

        Assert.Equal(DisseminationApplyResult.Rejected, rejectedResult);
        Assert.Equal(DisseminationApplyResult.Applied, appliedResult);
        Assert.Equal(DisseminationApplyResult.Duplicate, duplicateResult);
        Assert.Equal(DisseminationApplyResult.Obsolete, obsoleteResult);
    }

    [Fact]
    public async Task DeploymentLoadTopic_OnFallbackRequiredRefreshesStatisticsWhenEnabledForValidSilo()
    {
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var local = CreateSilo(21115);
        var peer = CreateSilo(21116);
        var statusOracle = new FakeSiloStatusOracle();
        statusOracle.SetStatus(peer, SiloStatus.Active);
        var remoteStatistics = CreateStatistics(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var grainFactory = new RecordingGrainFactory
        {
            Resolver = (_, _) => new FakeSiloControl(() => Task.FromResult(remoteStatistics)),
        };
        var publisher = CreateDeploymentLoadPublisher(local, statusOracle, grainFactory);
        var topic = CreateDeploymentLoadTopic(publisher, serializer, configure: options => options.Dissemination.FallbackEnabled = true);

        await topic.OnFallbackRequired(peer: null, new DisseminationTopicDigest(peer.ToParsableString(), 1), CancellationToken.None);

        Assert.Single(grainFactory.SystemTargetRequests);
        Assert.Equal(remoteStatistics.DateTime, publisher.PeriodicStatistics[peer].DateTime);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task DeploymentLoadTopic_OnFallbackRequiredSkipsRefreshWhenDisabledOrKeyInvalid(bool fallbackEnabled, bool validKey)
    {
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var local = CreateSilo(21117);
        var peer = CreateSilo(21118);
        var statusOracle = new FakeSiloStatusOracle();
        statusOracle.SetStatus(peer, SiloStatus.Active);
        var grainFactory = new RecordingGrainFactory();
        var publisher = CreateDeploymentLoadPublisher(local, statusOracle, grainFactory);
        var topic = CreateDeploymentLoadTopic(publisher, serializer, configure: options => options.Dissemination.FallbackEnabled = fallbackEnabled);
        var key = validKey ? peer.ToParsableString() : "not-a-silo-address";

        await topic.OnFallbackRequired(peer: null, new DisseminationTopicDigest(key, 1), CancellationToken.None);

        Assert.Empty(grainFactory.SystemTargetRequests);
    }

    [Fact]
    public void OrleansTransport_GetMembershipOrdersAndFiltersByStatus()
    {
        var local = CreateSilo(21201);
        var active1 = CreateSilo(21202);
        var active2 = CreateSilo(21203);
        var joining = CreateSilo(21204);
        var shuttingDown = CreateSilo(21205);
        var stopping = CreateSilo(21206);
        var dead = CreateSilo(21207);
        var created = CreateSilo(21208);
        var snapshot = CreateMembershipSnapshot(
            1,
            CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch),
            CreateMembershipEntry(active2, SiloStatus.Active, DateTime.UnixEpoch.AddSeconds(2)),
            CreateMembershipEntry(active1, SiloStatus.Active, DateTime.UnixEpoch.AddSeconds(1)),
            CreateMembershipEntry(joining, SiloStatus.Joining, DateTime.UnixEpoch),
            CreateMembershipEntry(shuttingDown, SiloStatus.ShuttingDown, DateTime.UnixEpoch),
            CreateMembershipEntry(stopping, SiloStatus.Stopping, DateTime.UnixEpoch),
            CreateMembershipEntry(dead, SiloStatus.Dead, DateTime.UnixEpoch),
            CreateMembershipEntry(created, SiloStatus.Created, DateTime.UnixEpoch));
        var membershipManager = new FakeMembershipManager(snapshot);
        var transport = new OrleansDisseminationTransport(new FakeLocalSiloDetails(local), membershipManager, new RecordingGrainFactory());

        var membership = transport.GetMembership();

        Assert.Equal(new[] { local, active1, active2, joining, shuttingDown, stopping }, membership.AllMembers);
        Assert.DoesNotContain(dead, membership.AllMembers);
        Assert.DoesNotContain(created, membership.AllMembers);
        Assert.Equal(new[] { local, active1, active2 }, membership.ActiveMembers);
    }

    [Fact]
    public void OrleansTransport_GetMembershipCachesResultUntilMembershipVersionChanges()
    {
        var local = CreateSilo(21209);
        var snapshot = CreateMembershipSnapshot(1, CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch));
        var membershipManager = new FakeMembershipManager(snapshot);
        var transport = new OrleansDisseminationTransport(new FakeLocalSiloDetails(local), membershipManager, new RecordingGrainFactory());

        var first = transport.GetMembership();
        var second = transport.GetMembership();

        Assert.True(first.AllMembers == second.AllMembers);
        Assert.True(first.ActiveMembers == second.ActiveMembers);

        var peer = CreateSilo(21210);
        membershipManager.CurrentSnapshot = CreateMembershipSnapshot(
            2,
            CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch),
            CreateMembershipEntry(peer, SiloStatus.Active, DateTime.UnixEpoch));
        var third = transport.GetMembership();

        Assert.False(first.AllMembers == third.AllMembers);
        Assert.Equal(2, third.AllMembers.Length);
    }

    [Fact]
    public async Task OrleansTransport_RefreshMembershipDelegatesToMembershipManager()
    {
        var local = CreateSilo(21211);
        var snapshot = CreateMembershipSnapshot(1, CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch));
        var membershipManager = new FakeMembershipManager(snapshot);
        var transport = new OrleansDisseminationTransport(new FakeLocalSiloDetails(local), membershipManager, new RecordingGrainFactory());

        await transport.RefreshMembership(CancellationToken.None);

        var refreshCall = Assert.Single(membershipManager.RefreshCalls);
        Assert.Null(refreshCall);
    }

    [Fact]
    public async Task OrleansTransport_SendGossipDelegatesToDisseminationSystemTarget()
    {
        var local = CreateSilo(21212);
        var peer = CreateSilo(21213);
        var snapshot = CreateMembershipSnapshot(1, CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch));
        var membershipManager = new FakeMembershipManager(snapshot);
        DisseminationGossipBatch? receivedBatch = null;
        var grainFactory = new RecordingGrainFactory
        {
            Resolver = (_, _) => new FakeDisseminationSystemTarget(pushGossip: (batch, _) =>
            {
                receivedBatch = batch;
                return Task.CompletedTask;
            }),
        };
        var transport = new OrleansDisseminationTransport(new FakeLocalSiloDetails(local), membershipManager, grainFactory);
        var batchToSend = new DisseminationGossipBatch { Sender = local };

        await transport.SendGossip(peer, batchToSend, CancellationToken.None);

        var request = Assert.Single(grainFactory.SystemTargetRequests);
        Assert.Equal(typeof(IDisseminationSystemTarget), request.Interface);
        Assert.Equal(peer, request.Destination);
        Assert.Same(batchToSend, receivedBatch);
    }

    [Fact]
    public async Task OrleansTransport_ExchangeAntiEntropyDelegatesToDisseminationSystemTarget()
    {
        var local = CreateSilo(21214);
        var peer = CreateSilo(21215);
        var snapshot = CreateMembershipSnapshot(1, CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch));
        var membershipManager = new FakeMembershipManager(snapshot);
        var expectedResponse = new DisseminationAntiEntropyResponse { Sender = peer };
        var grainFactory = new RecordingGrainFactory
        {
            Resolver = (_, _) => new FakeDisseminationSystemTarget(exchangeAntiEntropy: (_, _) => Task.FromResult(expectedResponse)),
        };
        var transport = new OrleansDisseminationTransport(new FakeLocalSiloDetails(local), membershipManager, grainFactory);
        var request = new DisseminationAntiEntropyRequest { Sender = local };

        var response = await transport.ExchangeAntiEntropy(peer, request, CancellationToken.None);

        Assert.Same(expectedResponse, response);
    }

    [Fact]
    public async Task DisseminationService_PublishQueuesGossipAndStopAsyncFlushesIt()
    {
        var local = CreateSilo(21301);
        var peers = Enumerable.Range(21302, 4).Select(CreateSilo).ToArray();
        var transport = new FakeTransport(local, peers);
        var topic = new FakeTopic(local);
        var service = CreateService(transport, topic, options => options.Overlay.FanOutFactor = static _ => 2);
        var item = topic.CreateItem(local, FakeTopic.DefaultKey, sequence: 1);

        var result = await service.Publish(topic.Name, item, peers, CancellationToken.None);
        Assert.True(result);
        Assert.Empty(transport.GossipBatches);

        await service.StopAsync(CancellationToken.None);

        Assert.Equal(GetOriginatorTreeTargets(local, peers, fanout: 2), transport.GossipBatches.Select(batch => batch.Peer));
    }

    [Fact]
    public async Task DisseminationService_ReceiveGossipAppliesValuesToTopic()
    {
        var local = CreateSilo(21303);
        var sender = CreateSilo(21304);
        var transport = new FakeTransport(local, sender);
        var topic = new FakeTopic(local);
        var service = CreateService(transport, topic);
        var value = topic.CreateItem(sender, FakeTopic.DefaultKey, sequence: 5);
        var batch = CreateGossipBatch(sender, value);

        await service.ReceiveGossip(batch, CancellationToken.None);

        Assert.Equal(1, topic.ApplyCounts[FakeTopic.DefaultKey]);
        Assert.Equal(5, topic.GetVersion(FakeTopic.DefaultKey));
    }

    [Fact]
    public async Task DisseminationService_ReceiveAntiEntropyReturnsLocalValuesNewerThanRequestDigest()
    {
        var local = CreateSilo(21305);
        var peer = CreateSilo(21306);
        var transport = new FakeTransport(local, peer);
        var topic = new FakeTopic(local);
        topic.SetValue(FakeTopic.DefaultKey, 3);
        var service = CreateService(transport, topic);
        var request = new DisseminationAntiEntropyRequest
        {
            Sender = peer,
            DigestsByTopic = CreateAntiEntropyRequestDigests(topic.Name, new DisseminationTopicDigest(FakeTopic.DefaultKey, 1)),
        };

        var response = await service.ReceiveAntiEntropy(request, CancellationToken.None);

        var value = Assert.Single(GetAntiEntropyResponseValues(response));
        Assert.Equal(3, BitConverter.ToInt64(value.Payload.Span));
    }

    [Fact]
    public async Task DisseminationService_StopAsyncWithoutStartAsyncCompletesWithoutThrowing()
    {
        var local = CreateSilo(21309);
        var transport = new FakeTransport(local);
        var topic = new FakeTopic(local);
        var service = CreateService(transport, topic);

        await service.StopAsync(CancellationToken.None);

        Assert.True(service.IsProtocolDisposed);
    }

    [Fact]
    public async Task DisseminationService_DisposeAsyncStopsAndDisposesProtocol()
    {
        var local = CreateSilo(21312);
        var transport = new FakeTransport(local);
        var topic = new FakeTopic(local);
        var service = CreateService(transport, topic);
        await service.StartAsync(CancellationToken.None);

        await service.DisposeAsync();
        await service.DisposeAsync();

        Assert.False(service.IsAntiEntropyRunning);
        Assert.True(service.IsProtocolDisposed);
    }

    [Fact]
    public async Task DisseminationProtocol_DisposeAsyncDisposesOwnedResources()
    {
        var local = CreateSilo(21313);
        var protocol = CreateProtocol(new FakeTransport(local), new FakeTopic(local));

        await protocol.DisposeAsync();
        await protocol.DisposeAsync();

        Assert.True(protocol.IsDisposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => protocol.FlushPendingGossip(CancellationToken.None));
    }

    [Fact]
    public async Task DisseminationService_StartAsyncDoesNotStartAntiEntropyWhenDisabled()
    {
        var local = CreateSilo(21310);
        var transport = new FakeTransport(local);
        var topic = new FakeTopic(local);
        var options = new DisseminationOptions { Enabled = false };
        var optionsMonitor = new TestOptionsMonitor<DisseminationOptions>(options);
        var service = new DisseminationService(
            transport,
            optionsMonitor,
            [topic],
            TimeProvider.System,
            NullLogger<DisseminationProtocol>.Instance);

        await service.StartAsync(CancellationToken.None);

        Assert.False(service.IsAntiEntropyRunning);
        options.Enabled = true;
        optionsMonitor.Update(options);
        await WaitUntil(() => service.IsAntiEntropyRunning);
        Assert.True(service.IsAntiEntropyRunning);
        options.Enabled = false;
        optionsMonitor.Update(options);
        await WaitUntil(() => !service.IsAntiEntropyRunning);
        Assert.False(service.IsAntiEntropyRunning);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DisseminationService_StartAsyncStartsAntiEntropyWhenEnabled()
    {
        var local = CreateSilo(21311);
        var transport = new FakeTransport(local);
        var topic = new FakeTopic(local);
        var service = CreateService(transport, topic);

        await service.StartAsync(CancellationToken.None);

        Assert.True(service.IsAntiEntropyRunning);
        await service.StopAsync(CancellationToken.None);
        Assert.False(service.IsAntiEntropyRunning);
    }

    [Fact]
    public async Task DisseminationService_StopAsyncPropagatesItsCancellationTokenToPendingGossipSends()
    {
        var local = CreateSilo(21501);
        var peers = Enumerable.Range(21502, 3).Select(CreateSilo).ToArray();
        var transport = new FakeTransport(local, peers);
        var topic = new FakeTopic(local);
        topic.Options.MaxCoalescingDelay = TimeSpan.FromMinutes(1);
        var service = CreateService(transport, topic, options => options.Overlay.FanOutFactor = static _ => 3);
        var item = topic.CreateItem(local, FakeTopic.DefaultKey, sequence: 1);
        var observedTokens = new List<CancellationToken>();
        transport.SendGossipHandler = (target, batch, cancellationToken) =>
        {
            observedTokens.Add(cancellationToken);
            return Task.CompletedTask;
        };
        using var cts = new CancellationTokenSource();

        await service.Publish(topic.Name, item, peers, CancellationToken.None);
        await service.StopAsync(cts.Token);

        Assert.NotEmpty(observedTokens);
        Assert.All(observedTokens, token => Assert.Equal(cts.Token, token));
    }

    [Fact]
    public async Task DisseminationService_StopAsyncWaitsForScheduledFlushAndStopsBackgroundWork()
    {
        var local = CreateSilo(21510);
        var peer = CreateSilo(21511);
        var transport = new FakeTransport(local, peer);
        var sendStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSend = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.SendGossipHandler = async (target, batch, cancellationToken) =>
        {
            transport.GossipBatches.Add((target, batch));
            sendStarted.TrySetResult(true);
            await releaseSend.Task.WaitAsync(cancellationToken);
        };
        var topic = new FakeTopic(local);
        topic.Options.MaxCoalescingDelay = TimeSpan.FromMilliseconds(1);
        var service = CreateService(transport, topic);
        await service.StartAsync(CancellationToken.None);

        Assert.True(await service.Publish(
            topic.Name,
            topic.CreateItem(local, FakeTopic.DefaultKey, sequence: 1),
            [peer],
            CancellationToken.None));
        await sendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        var stopTask = service.StopAsync(CancellationToken.None);
        Assert.False(stopTask.IsCompleted);
        releaseSend.SetResult(true);
        await stopTask;

        Assert.False(service.IsAntiEntropyRunning);
        Assert.Single(transport.GossipBatches);
    }

    [Fact]
    public async Task DisseminationService_StopAsyncHonorsCancellationWhileAntiEntropyTransportIsBlocked()
    {
        var local = CreateSilo(21512);
        var peer = CreateSilo(21513);
        var transport = new FakeTransport(local, peer);
        var exchangeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exchangeCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseExchange = new TaskCompletionSource<DisseminationAntiEntropyResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.ExchangeAntiEntropyHandler = ExchangeAntiEntropy;

        async ValueTask<DisseminationAntiEntropyResponse> ExchangeAntiEntropy(
            SiloAddress target,
            DisseminationAntiEntropyRequest _)
        {
            exchangeStarted.TrySetResult(true);
            var response = await releaseExchange.Task;
            exchangeCompleted.TrySetResult(true);
            return response;
        }

        var topic = new FakeTopic(local);
        topic.SetValue(FakeTopic.DefaultKey, version: 1);
        var service = CreateService(
            transport,
            topic,
            options => options.Overlay.AntiEntropyInterval = TimeSpan.FromMilliseconds(1));
        await service.StartAsync(CancellationToken.None);
        await exchangeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.StopAsync(cts.Token));

        Assert.False(service.IsAntiEntropyRunning);
        Assert.True(service.HasOutstandingAntiEntropyTask);
        releaseExchange.TrySetResult(new DisseminationAntiEntropyResponse
        {
            Sender = peer,
            SupportedTopics = [topic.Name],
        });
        await exchangeCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await WaitUntil(() => !service.HasOutstandingAntiEntropyTask);
        await WaitUntil(() => service.IsProtocolDisposed);
        Assert.Equal(new[] { peer }, service.GetUnconfirmedPeers(topic.Name, topic.MembershipScope));
    }

    [Fact]
    public async Task DisseminationService_RunAntiEntropyPropagatesItsCancellationTokenToTransport()
    {
        var local = CreateSilo(21503);
        var peers = Enumerable.Range(21504, 3).Select(CreateSilo).ToArray();
        var transport = new FakeTransport(local, peers);
        var topic = new FakeTopic(local);
        topic.SetValue(FakeTopic.DefaultKey, 1);
        var service = CreateService(transport, topic, options => options.Overlay.AntiEntropyPeerCount = 3);
        using var cts = new CancellationTokenSource();

        await service.RunAntiEntropy(cts.Token);

        Assert.NotEmpty(transport.ExchangeAntiEntropyCancellationTokens);
        Assert.All(transport.ExchangeAntiEntropyCancellationTokens, token => Assert.Equal(cts.Token, token));
    }

    [Fact]
    public async Task MembershipGossiper_NoOpWhenNoPartners()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var gossiper = new MembershipGossiper(services, NullLogger<MembershipGossiper>.Instance);
        var local = CreateSilo(21401);
        var snapshot = CreateMembershipSnapshot(1, CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch));

        await gossiper.GossipToRemoteSilos(new List<SiloAddress>(), snapshot, local, SiloStatus.Active);
    }

    [Fact]
    public async Task MembershipGossiper_UsesOnlyDisseminationForConfirmedPeer()
    {
        var local = CreateSilo(21402);
        var peer = CreateSilo(21403);
        using var serializerProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serializerProvider.GetRequiredService<Serializer>();
        var snapshot = CreateMembershipSnapshot(1, CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch));
        var membershipManager = new FakeMembershipManager(snapshot);
        var membershipTopic = CreateMembershipTopic(local, membershipManager, serializer, options => options.Dissemination.Enabled = true);
        var transport = new FakeTransport(local, peer);
        var disseminationService = CreateService(transport, new IDisseminationTopic[] { membershipTopic });
        var services = new ServiceCollection()
            .AddSingleton(disseminationService)
            .AddSingleton(membershipTopic)
            .AddSingleton<ILocalSiloDetails>(new FakeLocalSiloDetails(local))
            .BuildServiceProvider();
        var gossiper = new MembershipGossiper(services, NullLogger<MembershipGossiper>.Instance);

        await disseminationService.ReceiveAntiEntropy(new DisseminationAntiEntropyRequest
        {
            Sender = peer,
            DigestsByTopic = CreateAntiEntropyRequestDigests(
                membershipTopic.Name,
                new DisseminationTopicDigest("cluster", long.MinValue)),
        }, CancellationToken.None);
        await gossiper.GossipToRemoteSilos(new List<SiloAddress> { peer }, snapshot, local, SiloStatus.Active);

        await disseminationService.StopAsync(CancellationToken.None);
        Assert.NotEmpty(transport.GossipBatches);
    }

    [Fact]
    public async Task MembershipGossiper_UsesLegacyGossipForUnconfirmedPeer()
    {
        var local = CreateSilo(21410);
        var peer = CreateSilo(21411);
        using var serializerProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serializerProvider.GetRequiredService<Serializer>();
        var snapshot = CreateMembershipSnapshot(1, CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch));
        var membershipTopic = CreateMembershipTopic(local, new FakeMembershipManager(snapshot), serializer, options => options.Dissemination.Enabled = true);
        var transport = new FakeTransport(local, peer);
        var disseminationService = CreateService(transport, new IDisseminationTopic[] { membershipTopic });
        var services = new ServiceCollection()
            .AddSingleton(disseminationService)
            .AddSingleton(membershipTopic)
            .AddSingleton<ILocalSiloDetails>(new FakeLocalSiloDetails(local))
            .BuildServiceProvider();
        var gossiper = new MembershipGossiper(services, NullLogger<MembershipGossiper>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => gossiper.GossipToRemoteSilos(new List<SiloAddress> { peer }, snapshot, local, SiloStatus.Active));

        await disseminationService.StopAsync(CancellationToken.None);
        Assert.NotEmpty(transport.GossipBatches);
    }

    [Fact]
    public async Task MembershipGossiper_FallsBackWhenDisseminationServiceNotRegistered()
    {
        var local = CreateSilo(21404);
        var peer = CreateSilo(21405);
        using var serializerProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serializerProvider.GetRequiredService<Serializer>();
        var snapshot = CreateMembershipSnapshot(1, CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch));
        var membershipTopic = CreateMembershipTopic(local, new FakeMembershipManager(snapshot), serializer, options => options.Dissemination.Enabled = true);
        var services = new ServiceCollection()
            .AddSingleton(membershipTopic)
            .AddSingleton<ILocalSiloDetails>(new FakeLocalSiloDetails(local))
            .BuildServiceProvider();
        var gossiper = new MembershipGossiper(services, NullLogger<MembershipGossiper>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => gossiper.GossipToRemoteSilos(new List<SiloAddress> { peer }, snapshot, local, SiloStatus.Active));
    }

    [Fact]
    public async Task MembershipGossiper_FallsBackWhenMembershipTopicNotRegistered()
    {
        var local = CreateSilo(21406);
        var peer = CreateSilo(21407);
        var transport = new FakeTransport(local, peer);
        var disseminationService = CreateService(transport, Array.Empty<IDisseminationTopic>());
        var snapshot = CreateMembershipSnapshot(1, CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch));
        var services = new ServiceCollection()
            .AddSingleton(disseminationService)
            .AddSingleton<ILocalSiloDetails>(new FakeLocalSiloDetails(local))
            .BuildServiceProvider();
        var gossiper = new MembershipGossiper(services, NullLogger<MembershipGossiper>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => gossiper.GossipToRemoteSilos(new List<SiloAddress> { peer }, snapshot, local, SiloStatus.Active));
    }

    [Fact]
    public async Task MembershipGossiper_FallsBackWhenMembershipTopicDisabled()
    {
        var local = CreateSilo(21408);
        var peer = CreateSilo(21409);
        using var serializerProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serializerProvider.GetRequiredService<Serializer>();
        var snapshot = CreateMembershipSnapshot(1, CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch));
        var membershipTopic = CreateMembershipTopic(local, new FakeMembershipManager(snapshot), serializer, options => options.Dissemination.Enabled = false);
        var transport = new FakeTransport(local, peer);
        var disseminationService = CreateService(transport, new IDisseminationTopic[] { membershipTopic });
        var services = new ServiceCollection()
            .AddSingleton(disseminationService)
            .AddSingleton(membershipTopic)
            .AddSingleton<ILocalSiloDetails>(new FakeLocalSiloDetails(local))
            .BuildServiceProvider();
        var gossiper = new MembershipGossiper(services, NullLogger<MembershipGossiper>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => gossiper.GossipToRemoteSilos(new List<SiloAddress> { peer }, snapshot, local, SiloStatus.Active));
    }

    private static DisseminationProtocol CreateProtocol(
        FakeTransport transport,
        FakeTopic topic,
        Action<DisseminationOptions>? configure = null,
        TimeProvider? timeProvider = null) =>
        CreateProtocol(transport, new IDisseminationTopic[] { topic }, configure, timeProvider);

    private static DisseminationProtocol CreateProtocol(
        FakeTransport transport,
        IReadOnlyList<IDisseminationTopic> topics,
        Action<DisseminationOptions>? configure = null,
        TimeProvider? timeProvider = null)
    {
        var options = new DisseminationOptions { Enabled = true };
        configure?.Invoke(options);
        return new DisseminationProtocol(
            transport,
            new TestOptionsMonitor<DisseminationOptions>(options),
            topics,
            timeProvider ?? TimeProvider.System,
            NullLogger<DisseminationProtocol>.Instance);
    }

    private static DisseminationService CreateService(
        FakeTransport transport,
        FakeTopic topic,
        Action<DisseminationOptions>? configure = null) =>
        CreateService(transport, new IDisseminationTopic[] { topic }, configure);

    private static DisseminationService CreateService(
        IDisseminationTransport transport,
        IReadOnlyList<IDisseminationTopic> topics,
        Action<DisseminationOptions>? configure = null,
        TimeProvider? timeProvider = null)
    {
        var options = new DisseminationOptions { Enabled = true };
        configure?.Invoke(options);
        return new DisseminationService(
            transport,
            new TestOptionsMonitor<DisseminationOptions>(options),
            topics,
            timeProvider ?? TimeProvider.System,
            NullLogger<DisseminationProtocol>.Instance);
    }

    private static SystemTargetShared CreateSystemTargetShared(SiloAddress localSilo) => new(
        runtimeClient: null!,
        new FakeLocalSiloDetails(localSilo),
        NullLoggerFactory.Instance,
        Options.Create(new SchedulingOptions()),
        grainReferenceActivator: null!,
        timerRegistry: null!,
        activations: new ActivationDirectory(CreateCatalogInstruments()),
        schedulerInstruments: CreateSchedulerInstruments(),
        grainInstruments: CreateGrainInstruments(),
        messagingInstruments: CreateMessagingInstruments(),
        messagingProcessingInstruments: CreateMessagingProcessingInstruments());

    private static CatalogInstruments CreateCatalogInstruments() => CreateInstruments<CatalogInstruments>();

    private static SchedulerInstruments CreateSchedulerInstruments() => CreateInstruments<SchedulerInstruments>();

    private static GrainInstruments CreateGrainInstruments() => CreateInstruments<GrainInstruments>();

    private static MessagingInstruments CreateMessagingInstruments() => CreateInstruments<MessagingInstruments>();

    private static MessagingProcessingInstruments CreateMessagingProcessingInstruments() => CreateInstruments<MessagingProcessingInstruments>();

    private static T CreateInstruments<T>() where T : class
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddSingleton<OrleansInstruments>();
        services.AddSingleton<T>();
        return services.BuildServiceProvider().GetRequiredService<T>();
    }

    private static DeploymentLoadPublisher CreateDeploymentLoadPublisher(
        SiloAddress local,
        FakeSiloStatusOracle statusOracle,
        IInternalGrainFactory? grainFactory = null,
        IServiceProvider? serviceProvider = null) =>
        new(
            new FakeLocalSiloDetails(local),
            statusOracle,
            Options.Create(new DeploymentLoadPublisherOptions()),
            grainFactory ?? new RecordingGrainFactory(),
            NullLoggerFactory.Instance,
            new ActivationDirectory(CreateCatalogInstruments()),
            new FakeActivationWorkingSet(),
            new FakeEnvironmentStatisticsProvider(),
            Options.Create(new LoadSheddingOptions()),
            serviceProvider ?? new ServiceCollection().BuildServiceProvider(),
            CreateSystemTargetShared(local));

    private static DeploymentLoadStatisticsDisseminationTopic CreateDeploymentLoadTopic(
        DeploymentLoadPublisher publisher,
        Serializer serializer,
        Action<DeploymentLoadPublisherOptions>? configure = null,
        TimeProvider? timeProvider = null)
    {
        var options = new DeploymentLoadPublisherOptions();
        configure?.Invoke(options);
        return new DeploymentLoadStatisticsDisseminationTopic(
            publisher,
            new TestOptionsMonitor<DeploymentLoadPublisherOptions>(options),
            serializer,
            timeProvider ?? TimeProvider.System);
    }

    private static SiloRuntimeStatistics CreateStatistics(DateTime dateTime, int activationCount = 0) =>
        new(
            activationCount,
            recentlyUsedActivationCount: 0,
            new FakeEnvironmentStatisticsProvider(),
            Options.Create(new LoadSheddingOptions()),
            dateTime);

    private static SiloAddress CreateSilo(int port) => SiloAddress.New(new IPEndPoint(IPAddress.Loopback, port), port);

    private static SiloAddress[] CreateSilos(int count) =>
        Enumerable.Range(11111, count).Select(CreateSilo).OrderBy(static silo => silo).ToArray();

    private static ImmutableDictionary<string, ImmutableArray<DisseminationTopicDigest>> CreateAntiEntropyRequestDigests(
        string topicName,
        params DisseminationTopicDigest[] digests) =>
        new Dictionary<string, ImmutableArray<DisseminationTopicDigest>>(StringComparer.Ordinal)
        {
            [topicName] = [.. digests],
        }.ToImmutableDictionary(StringComparer.Ordinal);

    private static DisseminationGossipBatch CreateGossipBatch(SiloAddress sender, params DisseminationValue[] values) => new()
    {
        Sender = sender,
        ValuesByTopic = CreateValueGroups(values),
    };

    private static IEnumerable<DisseminationValue> GetGossipValues(DisseminationGossipBatch batch) =>
        batch.ValuesByTopic.Values.SelectMany(static values => values);

    private static IEnumerable<DisseminationValue> GetAntiEntropyResponseValues(DisseminationAntiEntropyResponse response) =>
        response.ValuesByTopic.Values.SelectMany(static values => values);

    private static ImmutableDictionary<string, ImmutableArray<DisseminationValue>> CreateValueGroups(params DisseminationValue[] values) =>
        CreateValueGroups(FakeTopic.DefaultName, values);

    private static ImmutableDictionary<string, ImmutableArray<DisseminationValue>> CreateValueGroups(string topicName, params DisseminationValue[] values) =>
        new Dictionary<string, ImmutableArray<DisseminationValue>>(StringComparer.Ordinal)
        {
            [topicName] = [.. values],
        }.ToImmutableDictionary(StringComparer.Ordinal);

    private static T RoundTrip<T>(Serializer serializer, T value)
    {
        var result = serializer.Deserialize<T>(serializer.SerializeToArray(value));
        return result is null ? throw new InvalidOperationException("The serialized value unexpectedly round-tripped as null.") : result;
    }

    private static void UpdateMaximum(ref int maximum, int value)
    {
        var current = Volatile.Read(ref maximum);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref maximum, value, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }

    private static MembershipDisseminationTopic CreateMembershipTopic(
        SiloAddress local,
        FakeMembershipManager membershipManager,
        Serializer serializer,
        Action<ClusterMembershipOptions>? configure = null)
    {
        var options = new ClusterMembershipOptions();
        configure?.Invoke(options);
        return new(
            membershipManager,
            new TestOptionsMonitor<ClusterMembershipOptions>(options),
            serializer,
            TimeProvider.System,
            new FakeLocalSiloDetails(local));
    }

    private static MembershipTableSnapshot CreateMembershipSnapshot(long version, params MembershipEntry[] entries) =>
        new(new MembershipVersion(version), entries.ToImmutableDictionary(static entry => entry.SiloAddress));

    private static MembershipEntry CreateMembershipEntry(SiloAddress silo, SiloStatus status, DateTime startTime) => new()
    {
        SiloAddress = silo,
        Status = status,
        ProxyPort = silo.Endpoint.Port,
        HostName = "localhost",
        SiloName = silo.ToParsableString(),
        RoleName = "test",
        StartTime = startTime,
        IAmAliveTime = startTime,
    };

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.True(condition(), "Condition was not satisfied within the timeout.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }
    }

    private static IReadOnlyList<SiloAddress> GetOriginatorTreeTargets(
        SiloAddress root,
        IEnumerable<SiloAddress> peers,
        int fanout)
    {
        var participants = GetSortedParticipants(root, root, peers);
        var rootIndex = participants.FindIndex(silo => Equals(silo, root));
        var result = new List<SiloAddress>();
        AddTopLevelTargets(participants, fanout, root, result);
        AddFixedChildren(participants, rootIndex, fanout, root, except: null, result);
        return result;
    }

    private static IReadOnlyList<SiloAddress> GetForwardingTreeTargets(
        SiloAddress local,
        SiloAddress root,
        IEnumerable<SiloAddress> peers,
        int fanout,
        SiloAddress sender)
    {
        var participants = GetSortedParticipants(local, root, peers);
        var localIndex = participants.FindIndex(silo => Equals(silo, local));
        var result = new List<SiloAddress>();
        AddFixedChildren(participants, localIndex, fanout, root, sender, result);
        return result;
    }

    private static List<SiloAddress> GetSortedParticipants(
        SiloAddress local,
        SiloAddress root,
        IEnumerable<SiloAddress> peers) =>
        peers
            .Append(local)
            .Append(root)
            .Distinct()
            .OrderBy(static silo => silo)
            .ToList();

    private static void AddTopLevelTargets(
        IReadOnlyList<SiloAddress> participants,
        int fanout,
        SiloAddress root,
        List<SiloAddress> result)
    {
        var count = Math.Min(fanout, participants.Count);
        for (var i = 0; i < count; i++)
        {
            AddTarget(participants[i], root, except: null, result);
        }
    }

    private static void AddFixedChildren(
        IReadOnlyList<SiloAddress> participants,
        int index,
        int fanout,
        SiloAddress root,
        SiloAddress? except,
        List<SiloAddress> result)
    {
        if (index < 0)
        {
            return;
        }

        var firstChild = (long)fanout * (index + 1);
        for (var i = 0; i < fanout; i++)
        {
            var childIndex = firstChild + i;
            if (childIndex >= participants.Count)
            {
                break;
            }

            AddTarget(participants[(int)childIndex], root, except, result);
        }
    }

    private static void AddTarget(SiloAddress peer, SiloAddress root, SiloAddress? except, List<SiloAddress> result)
    {
        if (Equals(peer, root) || (except is { } excluded && Equals(peer, excluded)) || result.Contains(peer))
        {
            return;
        }

        result.Add(peer);
    }

    private static HashSet<SiloAddress> GetReachedParticipants(SiloAddress root, IReadOnlyList<SiloAddress> participants, int fanout)
    {
        var reached = new HashSet<SiloAddress> { root };
        var pending = new Queue<SiloAddress>(GetOriginatorTreeTargets(root, participants.Where(silo => !Equals(silo, root)), fanout));
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            if (!reached.Add(current))
            {
                continue;
            }

            foreach (var child in GetForwardingTreeTargets(current, root, participants.Where(silo => !Equals(silo, current)), fanout, sender: root))
            {
                pending.Enqueue(child);
            }
        }

        return reached;
    }

    private static IEnumerable<int> GetAntiEntropyCandidateIndexes(int localIndex, int participantCount, int fanout)
    {
        var topLevelEnd = Math.Min(fanout, participantCount);
        if (localIndex < topLevelEnd)
        {
            return Enumerable.Range(0, topLevelEnd);
        }

        var parentIndex = (localIndex / fanout) - 1;
        var (previousLevelStart, previousLevelEnd) = GetLevelRange(parentIndex, participantCount, fanout);
        var windowStart = previousLevelStart + (((parentIndex - previousLevelStart) / fanout) * fanout);
        var windowEnd = Math.Min(previousLevelEnd, windowStart + fanout);
        return Enumerable.Range(windowStart, windowEnd - windowStart);
    }

    private static IReadOnlyList<SiloAddress> GetExpectedAntiEntropyPeers(
        string topicName,
        IReadOnlyList<SiloAddress> participants,
        int localIndex,
        int fanout,
        int peerCount,
        long round) =>
        GetAntiEntropyCandidateIndexes(localIndex, participants.Count, fanout)
            .Where(index => index != localIndex)
            .OrderBy(index => GetRepairPeerScore(participants[index], topicName, round, localIndex))
            .ThenBy(index => participants[index])
            .Take(peerCount)
            .Select(index => participants[index])
            .ToArray();

    private static (int Start, int End) GetLevelRange(int index, int participantCount, int fanout)
    {
        var start = 0L;
        var width = (long)fanout;
        while (index >= start + width && start + width < participantCount)
        {
            start += width;
            width = Math.Min(width * fanout, participantCount - start);
        }

        return ((int)start, (int)Math.Min(participantCount, start + width));
    }

    private static ulong GetRepairPeerScore(SiloAddress peer, string topicName, long round, int localIndex)
    {
        var value = (ulong)(uint)peer.GetConsistentHashCode();
        value ^= Mix(GetStableStringHash(topicName));
        value ^= (ulong)round * 0x9E3779B97F4A7C15UL;
        value ^= (ulong)(uint)localIndex << 32;
        return Mix(value);
    }

    private static ulong GetStableStringHash(string value)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offsetBasis;
        foreach (var ch in value)
        {
            hash ^= (byte)ch;
            hash *= prime;
            hash ^= (byte)(ch >> 8);
            hash *= prime;
        }

        return hash;
    }

    private static ulong Mix(ulong value)
    {
        value ^= value >> 30;
        value *= 0xBF58476D1CE4E5B9UL;
        value ^= value >> 27;
        value *= 0x94D049BB133111EBUL;
        value ^= value >> 31;
        return value;
    }

    private static GrainManifest CreateManifest(params (string Grain, string Key, string Value)[] grains)
    {
        var grainBuilder = System.Collections.Immutable.ImmutableDictionary.CreateBuilder<GrainType, GrainProperties>();
        foreach (var grain in grains)
        {
            var properties = System.Collections.Immutable.ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            properties[grain.Key] = grain.Value;
            grainBuilder[GrainType.Create(grain.Grain)] = new GrainProperties(
                properties.ToImmutable());
        }

        return new GrainManifest(
            grainBuilder.ToImmutable(),
            System.Collections.Immutable.ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty);
    }

    private static Spec<MonotonicDisseminationState> CreateMonotonicDisseminationSpec()
    {
        var spec = new Spec<MonotonicDisseminationState>()
            .WithJsonPrinters();

        spec.Operation<long, ModelApplyResponse>("Receive", static (version, state) =>
        {
            if (version < state.Version)
            {
                return Expect.That<ModelApplyResponse>(
                    response => response.Result == ModelApplyResult.Obsolete && response.Version == state.Version,
                    "Older values are obsolete and do not change state")
                    .SameState();
            }

            if (version == state.Version && state.Version > 0)
            {
                return Expect.That<ModelApplyResponse>(
                    response => response.Result == ModelApplyResult.Duplicate && response.Version == state.Version,
                    "Equal versions are duplicates")
                    .SameState();
            }

            return Expect.That<ModelApplyResponse>(
                response => response.Result == ModelApplyResult.Applied && response.Version == version,
                "Newer versions are applied")
                .ThenState<MonotonicDisseminationState>(nextState => nextState.Version = version);
        });

        spec.Operation<long, ModelRepairResponse>("RepairPeer", static (peerVersion, state) =>
        {
            if (state.Version > peerVersion)
            {
                return Expect.That<ModelRepairResponse>(
                    response => response.HasValue && response.Version == state.Version,
                    "Repair returns the local newer value")
                    .SameState();
            }

            return Expect.That<ModelRepairResponse>(
                response => !response.HasValue && response.Version == 0,
                "Repair returns no value when the peer is current or newer")
                .SameState();
        });

        return spec;
    }

    private sealed class FakeTopic(SiloAddress localSilo, string name = FakeTopic.DefaultName) : IDisseminationTopic
    {
        public const string DefaultName = "fake-topic";
        public const string DefaultKey = "value";
        private readonly Dictionary<string, long> _versions = new(StringComparer.Ordinal);

        public Dictionary<string, int> ApplyCounts { get; } = new(StringComparer.Ordinal);

        public HashSet<string> ExpectedKeys { get; } = new(StringComparer.Ordinal);

        public List<DisseminationTopicDigest> FallbackDigests { get; } = new();

        public Func<DisseminationTopicDigest, DisseminationTopicDigest?, DisseminationValue?>? GetValueHandler { get; set; }

        public Func<DisseminationValue, CancellationToken, ValueTask<DisseminationApplyResult>>? ApplyValueHandler { get; set; }

        public string Name => name;

        public DisseminationMembershipScope MembershipScope { get; set; } = DisseminationMembershipScope.ActiveMembers;

        public DisseminationTopicOptions Options { get; } = new() { Enabled = true };

        public bool IsEnabled => true;

        public DisseminationValue CreateItem(SiloAddress root, string key, long sequence) => new()
        {
            Digest = new DisseminationTopicDigest(key, sequence),
            Root = root,
            ExpiresAt = TimeProvider.System.GetUtcNow().AddMinutes(1),
            Payload = BitConverter.GetBytes(sequence),
        };

        public void SetValue(string key, long version) => _versions[key] = version;

        public void Clear() => _versions.Clear();

        public long GetVersion(string key) => _versions.TryGetValue(key, out var version) ? version : 0;

        public IReadOnlyList<DisseminationTopicDigest> GetDigests()
        {
            var digests = _versions
                .Select(entry => new DisseminationTopicDigest(entry.Key, entry.Value))
                .ToList();
            foreach (var key in ExpectedKeys)
            {
                if (!_versions.ContainsKey(key))
                {
                    digests.Add(new DisseminationTopicDigest(key, long.MinValue));
                }
            }

            return [.. digests.OrderBy(static digest => digest.Key, StringComparer.Ordinal)];
        }

        public int CompareVersion(DisseminationTopicDigest left, DisseminationTopicDigest right) => left.Version.CompareTo(right.Version);

        public bool IsObsolete(DisseminationTopicDigest digest) =>
            _versions.TryGetValue(digest.Key, out var version) && version > digest.Version;

        public ValueTask<DisseminationValue?> GetValue(
            DisseminationTopicDigest digest,
            DisseminationTopicDigest? peerDigest,
            CancellationToken cancellationToken)
        {
            if (GetValueHandler is { } handler)
            {
                return ValueTask.FromResult(handler(digest, peerDigest));
            }

            if (!_versions.TryGetValue(digest.Key, out var version) || version < digest.Version)
            {
                return ValueTask.FromResult<DisseminationValue?>(null);
            }

            return ValueTask.FromResult<DisseminationValue?>(CreateItem(localSilo, digest.Key, version));
        }

        public ValueTask<DisseminationApplyResult> ApplyValue(DisseminationValue value, CancellationToken cancellationToken)
        {
            if (ApplyValueHandler is { } handler)
            {
                return handler(value, cancellationToken);
            }

            var version = BitConverter.ToInt64(value.Payload.Span);
            if (_versions.TryGetValue(value.Digest.Key, out var current))
            {
                if (current > version)
                {
                    return ValueTask.FromResult(DisseminationApplyResult.Obsolete);
                }

                if (current == version)
                {
                    return ValueTask.FromResult(DisseminationApplyResult.Duplicate);
                }
            }

            _versions[value.Digest.Key] = version;
            ApplyCounts[value.Digest.Key] = ApplyCounts.TryGetValue(value.Digest.Key, out var count) ? count + 1 : 1;
            return ValueTask.FromResult(DisseminationApplyResult.Applied);
        }

        public ValueTask OnFallbackRequired(SiloAddress? peer, DisseminationTopicDigest digest, CancellationToken cancellationToken)
        {
            FallbackDigests.Add(digest);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeTransport(SiloAddress localSilo, params SiloAddress[] peers) : IDisseminationTransport
    {
        private readonly List<SiloAddress> _peers = peers.ToList();

        public List<(SiloAddress Peer, DisseminationGossipBatch Batch)> GossipBatches { get; } = new();

        public List<(SiloAddress Peer, DisseminationAntiEntropyRequest Request)> AntiEntropyRequests { get; } = new();

        public List<SiloAddress> Peers => _peers;

        public Dictionary<SiloAddress, SiloStatus> PeerStatuses { get; } = new();

        public Dictionary<SiloAddress, DateTime> StartTimes { get; } = new();

        public Func<SiloAddress, DisseminationGossipBatch, CancellationToken, Task>? SendGossipHandler { get; set; }

        public Func<SiloAddress, DisseminationAntiEntropyRequest, ValueTask<DisseminationAntiEntropyResponse>> ExchangeAntiEntropyHandler { get; set; } =
            static (peer, _) => ValueTask.FromResult(new DisseminationAntiEntropyResponse { Sender = peer });

        public Func<CancellationToken, Task>? RefreshMembershipHandler { get; set; }

        public Func<DisseminationMembership>? GetMembershipHandler { get; set; }

        public SiloAddress LocalSilo => localSilo;

        public int RefreshMembershipCallCount { get; private set; }

        public DisseminationMembership GetMembership()
        {
            if (GetMembershipHandler is { } handler)
            {
                return handler();
            }

            var members = _peers.Append(localSilo).Distinct().Select(peer =>
            {
                var status = PeerStatuses.TryGetValue(peer, out var peerStatus) ? peerStatus : SiloStatus.Active;
                var startTime = StartTimes.TryGetValue(peer, out var value) ? value : DateTime.UnixEpoch;
                return (Peer: peer, Status: status, StartTime: startTime);
            }).ToArray();

            var allMembers = members
                .Where(static member => member.Status is SiloStatus.Joining or SiloStatus.Active or SiloStatus.ShuttingDown or SiloStatus.Stopping)
                .OrderBy(static member => GetStatusRank(member.Status))
                .ThenBy(static member => member.StartTime)
                .ThenBy(static member => member.Peer)
                .Select(static member => member.Peer)
                .ToImmutableArray();
            var activeMembers = members
                .Where(static member => member.Status == SiloStatus.Active)
                .OrderBy(static member => member.StartTime)
                .ThenBy(static member => member.Peer)
                .Select(static member => member.Peer)
                .ToImmutableArray();
            return new DisseminationMembership(allMembers, activeMembers);
        }

        public Task RefreshMembership(CancellationToken cancellationToken)
        {
            RefreshMembershipCallCount++;
            return RefreshMembershipHandler?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }

        public Task SendGossip(SiloAddress peer, DisseminationGossipBatch batch, CancellationToken cancellationToken)
        {
            if (SendGossipHandler is not null)
            {
                return SendGossipHandler(peer, batch, cancellationToken);
            }

            GossipBatches.Add((peer, batch));
            return Task.CompletedTask;
        }

        public List<CancellationToken> ExchangeAntiEntropyCancellationTokens { get; } = new();

        public ValueTask<DisseminationAntiEntropyResponse> ExchangeAntiEntropy(
            SiloAddress peer,
            DisseminationAntiEntropyRequest request,
            CancellationToken cancellationToken)
        {
            AntiEntropyRequests.Add((peer, request));
            ExchangeAntiEntropyCancellationTokens.Add(cancellationToken);
            return ExchangeAntiEntropyHandler(peer, request);
        }

        private static int GetStatusRank(SiloStatus status) => status switch
        {
            SiloStatus.Active => 0,
            SiloStatus.Joining => 1,
            SiloStatus.ShuttingDown => 2,
            SiloStatus.Stopping => 3,
            _ => 4,
        };
    }

    private sealed class MonotonicDisseminationHarness(SiloAddress localSilo)
    {
        private readonly FakeTopic _topic = new(localSilo);

        public void Reset() => _topic.Clear();

        public async Task<ModelApplyResponse> Receive(long version)
        {
            var value = _topic.CreateItem(localSilo, FakeTopic.DefaultKey, version);
            var result = await _topic.ApplyValue(value, CancellationToken.None);
            return new ModelApplyResponse(ToModelResult(result), _topic.GetVersion(FakeTopic.DefaultKey));
        }

        public async Task<ModelRepairResponse> RepairPeer(long peerVersion)
        {
            var localDigest = _topic.GetDigests().SingleOrDefault();
            if (localDigest.Version == 0)
            {
                return new ModelRepairResponse(false, 0);
            }

            var peerDigest = new DisseminationTopicDigest(FakeTopic.DefaultKey, peerVersion);
            if (_topic.CompareVersion(localDigest, peerDigest) <= 0)
            {
                return new ModelRepairResponse(false, 0);
            }

            var value = await _topic.GetValue(
                localDigest,
                peerDigest,
                CancellationToken.None);
            return value is null
                ? new ModelRepairResponse(false, 0)
                : new ModelRepairResponse(true, value.Digest.Version);
        }

        private static ModelApplyResult ToModelResult(DisseminationApplyResult result) => result switch
        {
            DisseminationApplyResult.Applied => ModelApplyResult.Applied,
            DisseminationApplyResult.Duplicate => ModelApplyResult.Duplicate,
            DisseminationApplyResult.Obsolete => ModelApplyResult.Obsolete,
            _ => ModelApplyResult.Rejected,
        };
    }

    private sealed class FakeMembershipManager(MembershipTableSnapshot currentSnapshot) : IMembershipManager
    {
        public MembershipTableSnapshot CurrentSnapshot { get; set; } = currentSnapshot;

        public IAsyncEnumerable<MembershipTableSnapshot> MembershipUpdates => EmptyUpdates();

        public SiloStatus LocalSiloStatus => SiloStatus.Active;

        public Task UpdateLocalStatus(SiloStatus status, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> TryKillSilo(SiloAddress silo, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<bool> TrySuspectSilo(SiloAddress silo, SiloAddress? indirectProbingSilo, CancellationToken cancellationToken) => Task.FromResult(false);

        public List<MembershipVersion?> RefreshCalls { get; } = new();

        public Func<MembershipTableSnapshot, Task<bool>>? ProcessGossipSnapshotHandler { get; set; }

        public Task Refresh(MembershipVersion? targetVersion, CancellationToken cancellationToken)
        {
            RefreshCalls.Add(targetVersion);
            return Task.CompletedTask;
        }

        public Task<bool> ProcessGossipSnapshot(MembershipTableSnapshot snapshot, CancellationToken cancellationToken)
        {
            if (ProcessGossipSnapshotHandler is { } handler)
            {
                return handler(snapshot);
            }

            CurrentSnapshot = snapshot;
            return Task.FromResult(true);
        }

        public Task UpdateIAmAlive(CancellationToken cancellationToken) => Task.CompletedTask;

        public bool CheckHealth(DateTime lastCheckTime, out string reason)
        {
            reason = string.Empty;
            return true;
        }

        public void Participate(ISiloLifecycle lifecycle)
        {
        }

        private static async IAsyncEnumerable<MembershipTableSnapshot> EmptyUpdates()
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class FakeLocalSiloDetails(SiloAddress localSilo) : ILocalSiloDetails
    {
        public string Name => "local";

        public string ClusterId => "test-cluster";

        public string DnsHostName => "localhost";

        public SiloAddress SiloAddress => localSilo;

        public SiloAddress GatewayAddress => localSilo;
    }

    private sealed class TestOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        private readonly List<Action<T, string?>> _listeners = [];

        public T CurrentValue { get; private set; } = currentValue;

        public T Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<T, string?> listener)
        {
            _listeners.Add(listener);
            return new ListenerRegistration(_listeners, listener);
        }

        public void Update(T value)
        {
            CurrentValue = value;
            foreach (var listener in _listeners.ToArray())
            {
                listener(value, Options.DefaultName);
            }
        }

        private sealed class ListenerRegistration(
            List<Action<T, string?>> listeners,
            Action<T, string?> listener) : IDisposable
        {
            public void Dispose() => listeners.Remove(listener);
        }
    }

    private sealed class MutableServiceProvider : IServiceProvider
    {
        private readonly Dictionary<Type, object> _services = [];

        public void Add<T>(T service) where T : class => _services[typeof(T)] = service;

        public object? GetService(Type serviceType) =>
            _services.TryGetValue(serviceType, out var service) ? service : null;
    }

    private sealed class FakeSiloStatusOracle : ISiloStatusOracle
    {
        private readonly ConcurrentDictionary<SiloAddress, SiloStatus> _statuses = new();
        private int _approximateStatusesRequests;

        public int ApproximateStatusesRequests => Volatile.Read(ref _approximateStatusesRequests);

        public Func<SiloAddress, SiloStatus>? GetStatusHandler { get; set; }

        public SiloStatus CurrentStatus => SiloStatus.Active;

        public string SiloName => "local";

        public SiloAddress SiloAddress { get; set; } = default!;

        public void SetStatus(SiloAddress silo, SiloStatus status) => _statuses[silo] = status;

        public SiloStatus GetStoredStatus(SiloAddress siloAddress) =>
            _statuses.TryGetValue(siloAddress, out var status) ? status : SiloStatus.None;

        public SiloAddress[] GetActiveSilos() =>
            _statuses.Where(static kvp => kvp.Value == SiloStatus.Active).Select(static kvp => kvp.Key).ToArray();

        public SiloStatus GetApproximateSiloStatus(SiloAddress siloAddress) =>
            GetStatusHandler?.Invoke(siloAddress) ?? GetStoredStatus(siloAddress);

        public Dictionary<SiloAddress, SiloStatus> GetApproximateSiloStatuses(bool onlyActive = false)
        {
            Interlocked.Increment(ref _approximateStatusesRequests);
            return _statuses
                .Where(kvp => !onlyActive || kvp.Value == SiloStatus.Active)
                .ToDictionary(static kvp => kvp.Key, static kvp => kvp.Value);
        }

        public bool TryGetSiloName(SiloAddress siloAddress, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? siloName)
        {
            siloName = siloAddress.ToParsableString();
            return true;
        }

        public bool IsFunctionalDirectory(SiloAddress siloAddress) => true;

        public bool IsDeadSilo(SiloAddress silo) => _statuses.TryGetValue(silo, out var status) && status == SiloStatus.Dead;

        public bool SubscribeToSiloStatusEvents(ISiloStatusListener observer) => true;

        public bool UnSubscribeFromSiloStatusEvents(ISiloStatusListener observer) => true;
    }

    private sealed class FakeEnvironmentStatisticsProvider : Orleans.Statistics.IEnvironmentStatisticsProvider
    {
        public Orleans.Statistics.EnvironmentStatistics GetEnvironmentStatistics() => default;
    }

    private sealed class DelegateStatisticsListener(
        Action<SiloAddress, SiloRuntimeStatistics>? onUpdate = null,
        Action<SiloAddress>? onRemove = null) : ISiloStatisticsChangeListener
    {
        public void SiloStatisticsChangeNotification(SiloAddress updatedSilo, SiloRuntimeStatistics newStats) =>
            onUpdate?.Invoke(updatedSilo, newStats);

        public void RemoveSilo(SiloAddress removedSilo) => onRemove?.Invoke(removedSilo);
    }

    private sealed class ActionObserver<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(T value) => onNext(value);
    }

    private sealed class FakeActivationWorkingSet : IActivationWorkingSet
    {
        public int Count => 0;

        public void OnActivated(IActivationWorkingSetMember member)
        {
        }

        public void OnActive(IActivationWorkingSetMember member)
        {
        }

        public void OnDeactivating(IActivationWorkingSetMember member)
        {
        }

        public void OnDeactivated(IActivationWorkingSetMember member)
        {
        }
    }

    private sealed class FakeDeploymentLoadPublisherTarget : IDeploymentLoadPublisher
    {
        public List<(SiloAddress Source, SiloRuntimeStatistics Statistics)> Updates { get; } = [];

        public Task UpdateRuntimeStatistics(SiloAddress siloAddress, SiloRuntimeStatistics siloStats)
        {
            Updates.Add((siloAddress, siloStats));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSiloControl(Func<Task<SiloRuntimeStatistics>>? getRuntimeStatistics = null) : ISiloControl
    {
        public Task<SiloRuntimeStatistics> GetRuntimeStatistics() =>
            getRuntimeStatistics?.Invoke() ?? throw new NotSupportedException();

        public Task Ping(string message) => throw new NotSupportedException();

        public Task ForceGarbageCollection() => throw new NotSupportedException();

        public Task ForceActivationCollection(TimeSpan ageLimit) => throw new NotSupportedException();

        public Task ForceRuntimeStatisticsCollection() => throw new NotSupportedException();

        public Task<List<Tuple<GrainId, string, int>>> GetGrainStatistics() => throw new NotSupportedException();

        public Task<List<DetailedGrainStatistic>> GetDetailedGrainStatistics(string[]? types = null) => throw new NotSupportedException();

        public Task<SimpleGrainStatistic[]> GetSimpleGrainStatistics() => throw new NotSupportedException();

        public Task<DetailedGrainReport> GetDetailedGrainReport(GrainId grainId) => throw new NotSupportedException();

        public Task<int> GetActivationCount() => throw new NotSupportedException();

        public Task MigrateRandomActivations(SiloAddress target, int count) => throw new NotSupportedException();

        public Task<object?> SendControlCommandToProvider<T>(string providerName, int command, object? arg) where T : IControllable =>
            throw new NotSupportedException();

        public Task<List<GrainId>> GetActiveGrains(GrainType grainType) => throw new NotSupportedException();

        public Task SetCompatibilityStrategy(CompatibilityStrategy strategy) => throw new NotSupportedException();

        public Task SetSelectorStrategy(VersionSelectorStrategy strategy) => throw new NotSupportedException();

        public Task SetCompatibilityStrategy(GrainInterfaceType interfaceType, CompatibilityStrategy strategy) => throw new NotSupportedException();

        public Task SetSelectorStrategy(GrainInterfaceType interfaceType, VersionSelectorStrategy strategy) => throw new NotSupportedException();
    }

    private sealed class FakeDisseminationSystemTarget(
        Func<DisseminationGossipBatch, CancellationToken, Task>? pushGossip = null,
        Func<DisseminationAntiEntropyRequest, CancellationToken, Task<DisseminationAntiEntropyResponse>>? exchangeAntiEntropy = null) : IDisseminationSystemTarget
    {
        public Task PushGossip(DisseminationGossipBatch batch, CancellationToken cancellationToken) =>
            pushGossip?.Invoke(batch, cancellationToken) ?? Task.CompletedTask;

        public Task<DisseminationAntiEntropyResponse> ExchangeAntiEntropy(DisseminationAntiEntropyRequest request, CancellationToken cancellationToken) =>
            exchangeAntiEntropy?.Invoke(request, cancellationToken) ?? Task.FromResult(new DisseminationAntiEntropyResponse { Sender = request.Sender });
    }

    /// <summary>
    /// Minimal <see cref="IInternalGrainFactory"/> fake that records <see cref="GetSystemTarget{TGrainInterface}(GrainType, SiloAddress)"/>
    /// requests and resolves them via a configurable delegate. All other members are unused by the dissemination
    /// subsystem and therefore throw if invoked.
    /// </summary>
    private sealed class RecordingGrainFactory : IInternalGrainFactory
    {
        public List<(Type Interface, SiloAddress Destination)> SystemTargetRequests { get; } = new();

        public Func<Type, SiloAddress, object>? Resolver { get; set; }

        public TGrainInterface GetSystemTarget<TGrainInterface>(GrainType grainType, SiloAddress destination) where TGrainInterface : ISystemTarget
        {
            SystemTargetRequests.Add((typeof(TGrainInterface), destination));
            if (Resolver is { } resolver)
            {
                return (TGrainInterface)resolver(typeof(TGrainInterface), destination);
            }

            throw new NotSupportedException($"No resolver configured for {typeof(TGrainInterface)}.");
        }

        public TGrainInterface GetSystemTarget<TGrainInterface>(GrainId grainId) where TGrainInterface : ISystemTarget =>
            throw new NotSupportedException();

        public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IAddressable obj) where TGrainObserverInterface : IAddressable =>
            throw new NotSupportedException();

        public TGrainInterface Cast<TGrainInterface>(IAddressable grain) => throw new NotSupportedException();

        public object Cast(IAddressable grain, Type interfaceType) => throw new NotSupportedException();

        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithGuidKey =>
            throw new NotSupportedException();

        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithIntegerKey =>
            throw new NotSupportedException();

        public TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithStringKey =>
            throw new NotSupportedException();

        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithGuidCompoundKey =>
            throw new NotSupportedException();

        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithIntegerCompoundKey =>
            throw new NotSupportedException();

        public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj) where TGrainObserverInterface : IGrainObserver =>
            throw new NotSupportedException();

        public void DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj) where TGrainObserverInterface : IGrainObserver =>
            throw new NotSupportedException();

        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey) => throw new NotSupportedException();

        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey) => throw new NotSupportedException();

        public IGrain GetGrain(Type grainInterfaceType, string grainPrimaryKey) => throw new NotSupportedException();

        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension) => throw new NotSupportedException();

        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey, string keyExtension) => throw new NotSupportedException();

        public TGrainInterface GetGrain<TGrainInterface>(GrainId grainId) where TGrainInterface : IAddressable => throw new NotSupportedException();

        public IAddressable GetGrain(GrainId grainId) => throw new NotSupportedException();

        public IAddressable GetGrain(GrainId grainId, GrainInterfaceType interfaceType) => throw new NotSupportedException();

        public IAddressable GetGrain(Type interfaceType, IdSpan grainKey, string grainClassNamePrefix) => throw new NotSupportedException();

        public IAddressable GetGrain(Type interfaceType, IdSpan grainKey) => throw new NotSupportedException();
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan value) => _utcNow += value;
    }
}

[State]
public partial class MonotonicDisseminationState
{
    public long Version { get; set; }
}

public enum ModelApplyResult
{
    Applied,
    Duplicate,
    Obsolete,
    Rejected,
}

public sealed record ModelApplyResponse(ModelApplyResult Result, long Version);

public sealed record ModelRepairResponse(bool HasValue, long Version);
