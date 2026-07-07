#nullable enable

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using CsCheck;
#if NET10_0_OR_GREATER
using Microsoft.Accordant;
#endif
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Runtime.Dissemination;
using Orleans.Runtime.MembershipService;
using Orleans.Serialization;
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
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        var second = await protocol.Publish(topic.Name, topic.CreateItem(local, "second", sequence: 1), [peer], CancellationToken.None);

        await sent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(first);
        Assert.True(second);
        var batch = Assert.Single(transport.GossipBatches);
        Assert.Equal(new[] { "first", "second" }, GetGossipValues(batch.Batch).Select(static value => value.Digest.Key));
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
                    _ => FrozenDictionary<string, ImmutableArray<DisseminationValue>>.Empty,
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

#if NET10_0_OR_GREATER
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
#endif

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
        Action<DisseminationOptions>? configure = null)
    {
        var options = new DisseminationOptions { Enabled = true };
        configure?.Invoke(options);
        return new DisseminationService(
            transport,
            new TestOptionsMonitor<DisseminationOptions>(options),
            new[] { topic },
            TimeProvider.System,
            NullLogger<DisseminationProtocol>.Instance);
    }

    private static SiloAddress CreateSilo(int port) => SiloAddress.New(new IPEndPoint(IPAddress.Loopback, port), port);

    private static SiloAddress[] CreateSilos(int count) =>
        Enumerable.Range(11111, count).Select(CreateSilo).OrderBy(static silo => silo).ToArray();

    private static FrozenDictionary<string, ImmutableArray<DisseminationTopicDigest>> CreateAntiEntropyRequestDigests(
        string topicName,
        params DisseminationTopicDigest[] digests) =>
        new Dictionary<string, ImmutableArray<DisseminationTopicDigest>>(StringComparer.Ordinal)
        {
            [topicName] = [.. digests],
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private static DisseminationGossipBatch CreateGossipBatch(SiloAddress sender, params DisseminationValue[] values) => new()
    {
        Sender = sender,
        ValuesByTopic = CreateValueGroups(values),
    };

    private static IEnumerable<DisseminationValue> GetGossipValues(DisseminationGossipBatch batch) =>
        batch.ValuesByTopic.Values.SelectMany(static values => values);

    private static IEnumerable<DisseminationValue> GetAntiEntropyResponseValues(DisseminationAntiEntropyResponse response) =>
        response.ValuesByTopic.Values.SelectMany(static values => values);

    private static FrozenDictionary<string, ImmutableArray<DisseminationValue>> CreateValueGroups(params DisseminationValue[] values) =>
        CreateValueGroups(FakeTopic.DefaultName, values);

    private static FrozenDictionary<string, ImmutableArray<DisseminationValue>> CreateValueGroups(string topicName, params DisseminationValue[] values) =>
        new Dictionary<string, ImmutableArray<DisseminationValue>>(StringComparer.Ordinal)
        {
            [topicName] = [.. values],
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private static MembershipDisseminationTopic CreateMembershipTopic(
        SiloAddress local,
        FakeMembershipManager membershipManager,
        Serializer serializer) =>
        new(
            membershipManager,
            new TestOptionsMonitor<ClusterMembershipOptions>(new ClusterMembershipOptions()),
            serializer,
            TimeProvider.System,
            new FakeLocalSiloDetails(local));

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

#if NET10_0_OR_GREATER
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
#endif

    private sealed class FakeTopic(SiloAddress localSilo, string name = FakeTopic.DefaultName) : IDisseminationTopic
    {
        public const string DefaultName = "fake-topic";
        public const string DefaultKey = "value";
        private readonly Dictionary<string, long> _versions = new(StringComparer.Ordinal);

        public Dictionary<string, int> ApplyCounts { get; } = new(StringComparer.Ordinal);

        public HashSet<string> ExpectedKeys { get; } = new(StringComparer.Ordinal);

        public List<DisseminationTopicDigest> FallbackDigests { get; } = new();

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
            if (!_versions.TryGetValue(digest.Key, out var version) || version < digest.Version)
            {
                return ValueTask.FromResult<DisseminationValue?>(null);
            }

            return ValueTask.FromResult<DisseminationValue?>(CreateItem(localSilo, digest.Key, version));
        }

        public ValueTask<DisseminationApplyResult> ApplyValue(DisseminationValue value, CancellationToken cancellationToken)
        {
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

        public SiloAddress LocalSilo => localSilo;

        public int RefreshMembershipCallCount { get; private set; }

        public DisseminationMembership GetMembership()
        {
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

        public ValueTask<DisseminationAntiEntropyResponse> ExchangeAntiEntropy(
            SiloAddress peer,
            DisseminationAntiEntropyRequest request,
            CancellationToken cancellationToken)
        {
            AntiEntropyRequests.Add((peer, request));
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

#if NET10_0_OR_GREATER
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
#endif

    private sealed class FakeMembershipManager(MembershipTableSnapshot currentSnapshot) : IMembershipManager
    {
        public MembershipTableSnapshot CurrentSnapshot { get; set; } = currentSnapshot;

        public IAsyncEnumerable<MembershipTableSnapshot> MembershipUpdates => EmptyUpdates();

        public SiloStatus LocalSiloStatus => SiloStatus.Active;

        public Task UpdateLocalStatus(SiloStatus status, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> TryKillSilo(SiloAddress silo, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<bool> TrySuspectSilo(SiloAddress silo, SiloAddress? indirectProbingSilo, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task Refresh(MembershipVersion? targetVersion, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ProcessGossipSnapshot(MembershipTableSnapshot snapshot, CancellationToken cancellationToken)
        {
            CurrentSnapshot = snapshot;
            return Task.CompletedTask;
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
        public T CurrentValue => currentValue;

        public T Get(string? name) => currentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan value) => _utcNow += value;
    }
}

#if NET10_0_OR_GREATER
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
#endif
