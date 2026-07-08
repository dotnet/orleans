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
    public async Task PublishSendsBroadcastToDeterministicTreeChildren()
    {
        var local = CreateSilo(11111);
        var peers = Enumerable.Range(11112, 6).Select(CreateSilo).ToArray();
        var transport = new FakeTransport(local, peers);
        var ns = new FakeNamespace(local);
        var protocol = CreateProtocol(transport, ns, options =>
        {
            options.MaxConcurrentSends = 1;
            options.Overlay.FanOutFactor = static _ => 2;
        });
        var item = ns.CreateValue(FakeNamespace.DefaultKey, sequence: 1);

        var result = await protocol.Publish(ns, item, CancellationToken.None);
        await protocol.FlushPendingBroadcast(CancellationToken.None);

        Assert.True(result);
        var expectedChildren = GetOriginatorTreeTargets(local, peers, fanout: 2);
        Assert.Equal(expectedChildren, transport.BroadcastBatches.Select(batch => batch.Peer));
        Assert.All(transport.BroadcastBatches, batch => Assert.Equal(item, GetBroadcastValues(batch.Batch).Single().Value));
    }

    [Fact]
    public async Task PublishQueuesTreeBroadcastWithoutCapabilityProbing()
    {
        var local = CreateSilo(11111);
        var peers = Enumerable.Range(11112, 6).Select(CreateSilo).ToArray();
        var transport = new FakeTransport(local, peers);
        var ns = new FakeNamespace(local);
        var protocol = CreateProtocol(transport, ns, options =>
        {
            options.MaxConcurrentSends = 1;
            options.Overlay.FanOutFactor = static _ => 2;
        });
        var item = ns.CreateValue(FakeNamespace.DefaultKey, sequence: 1);

        var result = await protocol.Publish(ns, item, CancellationToken.None);
        await protocol.FlushPendingBroadcast(CancellationToken.None);

        Assert.True(result);
        Assert.Equal(GetOriginatorTreeTargets(local, peers, fanout: 2), transport.BroadcastBatches.Select(batch => batch.Peer));
    }

    [Fact]
    public async Task BroadcastSendsHonorMaxConcurrentSends()
    {
        const int maxConcurrentSends = 2;
        var local = CreateSilo(11111);
        var peers = Enumerable.Range(11112, 6).Select(CreateSilo).ToArray();
        var transport = new FakeTransport(local, peers);
        var ns = new FakeNamespace(local);
        var gate = new object();
        var limitReached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSends = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = 0;
        var started = 0;
        var observedMax = 0;
        var sentPeers = new List<SiloAddress>();
        transport.SendBroadcastHandler = async (target, batch, cancellationToken) =>
        {
            lock (gate)
            {
                inFlight++;
                started++;
                observedMax = Math.Max(observedMax, inFlight);
                if (started == maxConcurrentSends)
                {
                    limitReached.TrySetResult(true);
                }
            }

            try
            {
                await releaseSends.Task.WaitAsync(cancellationToken);
                lock (gate)
                {
                    sentPeers.Add(target);
                }
            }
            finally
            {
                lock (gate)
                {
                    inFlight--;
                }
            }
        };

        var protocol = CreateProtocol(transport, ns, options =>
        {
            options.MaxConcurrentSends = maxConcurrentSends;
            options.Overlay.FanOutFactor = static _ => 10;
        });
        var item = ns.CreateValue(FakeNamespace.DefaultKey, sequence: 1);

        Assert.True(await protocol.Publish(ns, item, CancellationToken.None));
        var flushTask = protocol.FlushPendingBroadcast(CancellationToken.None);
        try
        {
            await limitReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            lock (gate)
            {
                Assert.Equal(maxConcurrentSends, started);
                Assert.Equal(maxConcurrentSends, inFlight);
                Assert.Equal(maxConcurrentSends, observedMax);
            }
        }
        finally
        {
            releaseSends.TrySetResult(true);
        }

        await flushTask.WaitAsync(TimeSpan.FromSeconds(5));
        lock (gate)
        {
            Assert.Equal(peers.OrderBy(static peer => peer), sentPeers.OrderBy(static peer => peer));
            Assert.True(observedMax <= maxConcurrentSends);
        }
    }

    [Fact]
    public async Task PublishRejectsInvalidValuesBeforeQueueing()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var ns = new FakeNamespace(local);
        var protocol = CreateProtocol(transport, ns);
        ns.SetValue("obsolete", version: 10);

        ns.Options.MaxPayloadBytes = sizeof(long);
        var oversized = new DisseminationValue(
            "oversized",
            fromVersion: 0,
            toVersion: 1,
            new byte[sizeof(long) + 1]);
        var obsolete = ns.CreateValue("obsolete", sequence: 5);

        Assert.False(await protocol.Publish(ns, oversized, CancellationToken.None));
        Assert.False(await protocol.Publish(ns, obsolete, CancellationToken.None));
        await protocol.FlushPendingBroadcast(CancellationToken.None);

        Assert.Empty(transport.BroadcastBatches);
    }

    [Fact]
    public async Task PublishReturnsFalseWhenRootIsMissingFromGroup()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        transport.PeerStatuses[local] = SiloStatus.Joining;
        var ns = new FakeNamespace(local);
        var protocol = CreateProtocol(transport, ns);
        var item = ns.CreateValue(FakeNamespace.DefaultKey, sequence: 1);

        var result = await protocol.Publish(ns, item, CancellationToken.None);
        await protocol.FlushPendingBroadcast(CancellationToken.None);

        Assert.False(result);
        Assert.Equal(1, transport.RefreshMembershipCallCount);
        Assert.Empty(transport.BroadcastBatches);
    }

    [Fact]
    public async Task PublishAttemptsJoiningParticipantAndReliesOnSendBackoff()
    {
        var local = CreateSilo(11111);
        var joining = CreateSilo(11112);
        var active = CreateSilo(11113);
        var transport = new FakeTransport(local, joining, active);
        transport.PeerStatuses[joining] = SiloStatus.Joining;
        transport.SendBroadcastHandler = (target, batch, cancellationToken) =>
        {
            if (Equals(target, joining))
            {
                throw new InvalidOperationException("joining peer is not yet reachable");
            }

            transport.BroadcastBatches.Add((target, batch));
            return Task.CompletedTask;
        };

        var ns = new FakeNamespace(local)
        {
            Group = DisseminationGroup.AllMembers,
        };
        var protocol = CreateProtocol(transport, ns, options =>
        {
            options.FailureBackoff = TimeSpan.FromSeconds(5);
            options.Overlay.FanOutFactor = static _ => 2;
        });
        var value = ns.CreateValue(FakeNamespace.DefaultKey, sequence: 1);

        var result = await protocol.Publish(ns, value, CancellationToken.None);
        await protocol.FlushPendingBroadcast(CancellationToken.None);

        Assert.True(result);
        Assert.Equal(new[] { active }, transport.BroadcastBatches.Select(batch => batch.Peer));
    }

    [Fact]
    public async Task SendFailureUsesFailureBackoff()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var timeProvider = new TestTimeProvider();
        var sendCount = 0;
        transport.SendBroadcastHandler = (target, batch, cancellationToken) =>
        {
            if (Interlocked.Increment(ref sendCount) == 1)
            {
                timeProvider.Advance(TimeSpan.FromSeconds(10));
                throw new InvalidOperationException("transient send failure");
            }

            transport.BroadcastBatches.Add((target, batch));
            return Task.CompletedTask;
        };

        var ns = new FakeNamespace(local);
        var protocol = CreateProtocol(transport, ns, options =>
        {
            options.FailureBackoff = TimeSpan.FromSeconds(5);
        }, timeProvider);
        var item = ns.CreateValue(FakeNamespace.DefaultKey, sequence: 1);

        var firstResult = await protocol.Publish(ns, item, CancellationToken.None);
        await protocol.FlushPendingBroadcast(CancellationToken.None);
        var secondResult = await protocol.Publish(ns, item, CancellationToken.None);
        await protocol.FlushPendingBroadcast(CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromSeconds(5));
        var thirdResult = await protocol.Publish(ns, item, CancellationToken.None);
        await protocol.FlushPendingBroadcast(CancellationToken.None);

        Assert.True(firstResult);
        Assert.True(secondResult);
        Assert.True(thirdResult);
        Assert.Equal(2, sendCount);
        Assert.Single(transport.BroadcastBatches);
    }

    [Fact]
    public async Task MembershipRefreshPrunesFailureBackoffForRemovedPeers()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var sendCount = 0;
        transport.SendBroadcastHandler = (target, batch, cancellationToken) =>
        {
            if (++sendCount == 1)
            {
                throw new InvalidOperationException("peer failed before removal");
            }

            transport.BroadcastBatches.Add((target, batch));
            return Task.CompletedTask;
        };

        var ns = new FakeNamespace(local);
        var protocol = CreateProtocol(transport, ns, options =>
        {
            options.FailureBackoff = TimeSpan.FromMinutes(1);
            options.Overlay.FanOutFactor = static _ => 1;
        }, new TestTimeProvider());

        Assert.True(await protocol.Publish(ns, ns.CreateValue("before-removal", sequence: 1), CancellationToken.None));
        await protocol.FlushPendingBroadcast(CancellationToken.None);
        Assert.Equal(1, sendCount);

        transport.Peers.Remove(peer);
        Assert.True(await protocol.Publish(ns, ns.CreateValue("during-removal", sequence: 2), CancellationToken.None));

        transport.Peers.Add(peer);
        Assert.True(await protocol.Publish(ns, ns.CreateValue("after-return", sequence: 3), CancellationToken.None));
        await protocol.FlushPendingBroadcast(CancellationToken.None);

        Assert.Equal(2, sendCount);
        Assert.Single(transport.BroadcastBatches);
    }

    [Fact]
    public async Task MembershipRefreshDropsPendingBroadcastForRemovedPeers()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var ns = new FakeNamespace(local);
        ns.Options.MaxCoalescingDelay = TimeSpan.FromMinutes(1);
        var protocol = CreateProtocol(transport, ns, options => options.Overlay.FanOutFactor = static _ => 1);

        Assert.True(await protocol.Publish(ns, ns.CreateValue("before-removal", sequence: 1), CancellationToken.None));

        transport.Peers.Remove(peer);
        Assert.True(await protocol.Publish(ns, ns.CreateValue("during-removal", sequence: 2), CancellationToken.None));
        await protocol.FlushPendingBroadcast(CancellationToken.None);

        Assert.Empty(transport.BroadcastBatches);
    }

    [Fact]
    public async Task ReceiveBroadcastForwardsOnlyToLocalTreeChildren()
    {
        var silos = Enumerable.Range(11111, 8).Select(CreateSilo).OrderBy(static silo => silo).ToArray();
        var root = silos[0];
        var local = silos[1];
        var peers = silos.Where(silo => !Equals(silo, local)).ToArray();
        var transport = new FakeTransport(local, peers);
        var ns = new FakeNamespace(local);
        var protocol = CreateProtocol(transport, ns, options =>
        {
            options.MaxConcurrentSends = 1;
            options.Overlay.FanOutFactor = static _ => 2;
        });
        var item = ns.CreateItem(root, FakeNamespace.DefaultKey, sequence: 1);

        await protocol.ReceiveBroadcast(CreateBroadcastBatch(root, item), CancellationToken.None);
        await protocol.FlushPendingBroadcast(CancellationToken.None);

        var expectedChildren = GetForwardingTreeTargets(local, root, peers, fanout: 2, sender: root);
        Assert.Equal(expectedChildren, transport.BroadcastBatches.Select(batch => batch.Peer));
    }

    [Fact]
    public async Task DuplicateBroadcastDoesNotForwardAgain()
    {
        var silos = Enumerable.Range(11111, 8).Select(CreateSilo).OrderBy(static silo => silo).ToArray();
        var root = silos[0];
        var local = silos[1];
        var peers = silos.Where(silo => !Equals(silo, local)).ToArray();
        var transport = new FakeTransport(local, peers);
        var ns = new FakeNamespace(local);
        var protocol = CreateProtocol(transport, ns, options =>
        {
            options.MaxConcurrentSends = 1;
            options.Overlay.FanOutFactor = static _ => 2;
        });
        var item = ns.CreateItem(root, FakeNamespace.DefaultKey, sequence: 1);
        var batch = CreateBroadcastBatch(root, item);

        await protocol.ReceiveBroadcast(batch, CancellationToken.None);
        await protocol.ReceiveBroadcast(batch, CancellationToken.None);
        await protocol.FlushPendingBroadcast(CancellationToken.None);

        var expectedChildren = GetForwardingTreeTargets(local, root, peers, fanout: 2, sender: root);
        Assert.Equal(expectedChildren.Count, transport.BroadcastBatches.Count);
        Assert.Equal(1, ns.ApplyCounts[item.Value.Key]);
    }

    [Fact]
    public async Task ReceiveBroadcastAppliesFullValueOverOlderLocalVersion()
    {
        var root = CreateSilo(11111);
        var local = CreateSilo(11112);
        var transport = new FakeTransport(local, root);
        var ns = new FakeNamespace(local);
        var protocol = CreateProtocol(transport, ns);
        ns.SetValue(FakeNamespace.DefaultKey, version: 1);
        var item = ns.CreateItem(root, FakeNamespace.DefaultKey, sequence: 3, fromVersion: 0);

        await protocol.ReceiveBroadcast(CreateBroadcastBatch(root, item), CancellationToken.None);

        Assert.Equal(3, ns.GetVersion(FakeNamespace.DefaultKey));
        Assert.Equal(1, ns.ApplyCounts[FakeNamespace.DefaultKey]);
    }

    [Fact]
    public async Task ReceiveBroadcastAppliesDeltaValueOnlyWhenFromVersionMatches()
    {
        var root = CreateSilo(11111);
        var local = CreateSilo(11112);
        var transport = new FakeTransport(local, root);
        var ns = new FakeNamespace(local);
        var protocol = CreateProtocol(transport, ns);
        ns.SetValue(FakeNamespace.DefaultKey, version: 2);
        var item = ns.CreateItem(root, FakeNamespace.DefaultKey, sequence: 3, fromVersion: 2);

        await protocol.ReceiveBroadcast(CreateBroadcastBatch(root, item), CancellationToken.None);

        Assert.Equal(3, ns.GetVersion(FakeNamespace.DefaultKey));
        Assert.Equal(1, ns.ApplyCounts[FakeNamespace.DefaultKey]);
    }

    [Fact]
    public async Task ReceiveBroadcastRejectsDeltaValueWhenFromVersionIsMissing()
    {
        var root = CreateSilo(11111);
        var local = CreateSilo(11112);
        var transport = new FakeTransport(local, root);
        var ns = new FakeNamespace(local);
        var protocol = CreateProtocol(transport, ns);
        ns.SetValue(FakeNamespace.DefaultKey, version: 1);
        var item = ns.CreateItem(root, FakeNamespace.DefaultKey, sequence: 3, fromVersion: 2);

        await protocol.ReceiveBroadcast(CreateBroadcastBatch(root, item), CancellationToken.None);

        Assert.Equal(1, ns.GetVersion(FakeNamespace.DefaultKey));
        Assert.False(ns.ApplyCounts.ContainsKey(FakeNamespace.DefaultKey));
    }

    [Fact]
    public async Task ReceiveBroadcastWithMissingRootRefreshesMembershipAndDoesNotForward()
    {
        var root = CreateSilo(11111);
        var local = CreateSilo(11112);
        var sender = CreateSilo(11113);
        var peer = CreateSilo(11114);
        var transport = new FakeTransport(local, sender, peer);
        var ns = new FakeNamespace(local);
        var protocol = CreateProtocol(transport, ns, options =>
        {
            options.MaxConcurrentSends = 1;
            options.Overlay.FanOutFactor = static _ => 2;
        });
        var item = ns.CreateItem(root, FakeNamespace.DefaultKey, sequence: 1);

        await protocol.ReceiveBroadcast(CreateBroadcastBatch(sender, item), CancellationToken.None);
        await protocol.FlushPendingBroadcast(CancellationToken.None);

        Assert.Equal(1, ns.GetVersion(FakeNamespace.DefaultKey));
        Assert.Equal(1, transport.RefreshMembershipCallCount);
        Assert.Empty(transport.BroadcastBatches);
    }

    [Fact]
    public async Task ReceiveBroadcastWithMissingRootForwardsAfterMembershipRefresh()
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
        var ns = new FakeNamespace(local);
        var protocol = CreateProtocol(transport, ns, options => options.Overlay.FanOutFactor = static _ => 2);
        var item = ns.CreateItem(root, FakeNamespace.DefaultKey, sequence: 1);

        await protocol.ReceiveBroadcast(CreateBroadcastBatch(sender, item), CancellationToken.None);
        await protocol.FlushPendingBroadcast(CancellationToken.None);

        Assert.Equal(1, ns.GetVersion(FakeNamespace.DefaultKey));
        Assert.Equal(1, transport.RefreshMembershipCallCount);
        Assert.Equal(new[] { peer }, transport.BroadcastBatches.Select(static batch => batch.Peer));
    }

    [Fact]
    public async Task TreeRoutingInvalidatesCachedTopologyWhenActivePeersChange()
    {
        var local = CreateSilo(11115);
        var initialPeers = Enumerable.Range(11112, 3).Select(CreateSilo).ToList();
        var transport = new FakeTransport(local, initialPeers.ToArray());
        var ns = new FakeNamespace(local);
        var protocol = CreateProtocol(transport, ns, options =>
        {
            options.MaxConcurrentSends = 1;
            options.Overlay.FanOutFactor = static _ => 2;
        });
        var item = ns.CreateValue(FakeNamespace.DefaultKey, sequence: 1);

        var initialResult = await protocol.Publish(ns, item, CancellationToken.None);
        await protocol.FlushPendingBroadcast(CancellationToken.None);
        var initialChildren = transport.BroadcastBatches.Select(batch => batch.Peer).ToArray();

        foreach (var peer in Enumerable.Range(11116, 8).Select(CreateSilo))
        {
            transport.Peers.Add(peer);
            var updatedChildren = GetOriginatorTreeTargets(local, transport.Peers, fanout: 2);
            if (!initialChildren.SequenceEqual(updatedChildren))
            {
                transport.BroadcastBatches.Clear();
                var updatedItem = ns.CreateValue(FakeNamespace.DefaultKey, sequence: 2);

                var updatedResult = await protocol.Publish(ns, updatedItem, CancellationToken.None);
                await protocol.FlushPendingBroadcast(CancellationToken.None);

                Assert.True(initialResult);
                Assert.True(updatedResult);
                Assert.Equal(updatedChildren, transport.BroadcastBatches.Select(batch => batch.Peer));
                return;
            }
        }

        throw new InvalidOperationException("The test did not find a peer set which changes the local tree children.");
    }

    [Fact]
    public async Task BroadcastBatchingCoalescesSamePeerValuesByLatestVersion()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var ns = new FakeNamespace(local);
        var protocol = CreateProtocol(transport, ns);

        var first = await protocol.Publish(ns, ns.CreateValue(FakeNamespace.DefaultKey, sequence: 1), CancellationToken.None);
        var second = await protocol.Publish(ns, ns.CreateValue(FakeNamespace.DefaultKey, sequence: 2), CancellationToken.None);
        await protocol.FlushPendingBroadcast(CancellationToken.None);

        Assert.True(first);
        Assert.True(second);
        var batch = Assert.Single(transport.BroadcastBatches);
        Assert.Equal(peer, batch.Peer);
        var value = Assert.Single(GetBroadcastValues(batch.Batch));
        Assert.Equal(2, value.Value.ToVersion);
    }

    [Fact]
    public async Task BroadcastBatchingWakesScheduledFlushWhenBatchLimitIsReached()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var sent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.SendBroadcastHandler = (target, batch, cancellationToken) =>
        {
            transport.BroadcastBatches.Add((target, batch));
            sent.TrySetResult(true);
            return Task.CompletedTask;
        };

        var ns = new FakeNamespace(local);
        ns.Options.MaxCoalescingDelay = TimeSpan.FromMinutes(1);
        var protocol = CreateProtocol(transport, ns, options => options.MaxBatchItems = 2);

        var first = await protocol.Publish(ns, ns.CreateValue("first", sequence: 1), CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        var second = await protocol.Publish(ns, ns.CreateValue("second", sequence: 1), CancellationToken.None);

        await sent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(first);
        Assert.True(second);
        var batch = Assert.Single(transport.BroadcastBatches);
        Assert.Equal(new DisseminationKey[] { "first", "second" }, GetBroadcastValues(batch.Batch).Select(static value => value.Value.Key));
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
    public void AntiEntropyPeerSelectionSamplesDistinctMembers()
    {
        Gen.Select(Gen.Int[2, 64], Gen.Int[0, 63], Gen.Int[1, 64], static (count, localSeed, peerCount) =>
        {
            var localIndex = localSeed % count;
            return (Count: count, LocalIndex: localIndex, PeerCount: peerCount);
        }).Sample(testCase =>
        {
            var silos = CreateSilos(testCase.Count);
            var local = silos[testCase.LocalIndex];
            var transport = new FakeTransport(local, silos.Where(silo => !Equals(silo, local)).ToArray());
            var ns = new FakeNamespace(local);
            ns.ExpectedKeys.Add(FakeNamespace.DefaultKey);
            var protocol = CreateProtocol(transport, ns, options =>
            {
                options.Overlay.AntiEntropyPeerCount = testCase.PeerCount;
            });

            protocol.RunAntiEntropyRound(CancellationToken.None).GetAwaiter().GetResult();
            var peers = transport.AntiEntropyRequests.Select(static request => request.Peer).ToArray();

            Assert.Equal(Math.Min(testCase.PeerCount, testCase.Count - 1), peers.Length);
            Assert.Equal(peers.Length, peers.Distinct().Count());
            Assert.DoesNotContain(local, peers);
            Assert.All(peers, peer => Assert.Contains(peer, silos));
        });
    }

    [Fact]
    public async Task AntiEntropyResponseIncludesNewerLocalValues()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var ns = new FakeNamespace(local);
        var protocol = CreateProtocol(transport, ns);
        ns.SetValue(FakeNamespace.DefaultKey, version: 5);

        var response = await protocol.ReceiveAntiEntropy(new DisseminationAntiEntropyRequest
        {
            Digests = CreateAntiEntropyRequestDigest(
                ns.Name,
                (FakeNamespace.DefaultKey, 3)),
        }, CancellationToken.None);

        var item = Assert.Single(GetAntiEntropyResponseValues(response));
        Assert.Equal(5, item.Value.ToVersion);
        Assert.False(response.Truncated);
    }

    [Fact]
    public async Task AntiEntropyResponseOnlyIncludesRequestedDigestKeys()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var ns = new FakeNamespace(local);
        var protocol = CreateProtocol(transport, ns);
        ns.SetValue("requested", version: 5);
        ns.SetValue("omitted", version: 5);

        var response = await protocol.ReceiveAntiEntropy(new DisseminationAntiEntropyRequest
        {
            Digests = CreateAntiEntropyRequestDigest(
                ns.Name,
                ("requested", 3)),
        }, CancellationToken.None);

        var item = Assert.Single(GetAntiEntropyResponseValues(response));
        Assert.Equal(new DisseminationKey("requested"), item.Value.Key);
    }

    [Fact]
    public async Task AntiEntropyDigestRequestsAreSuppressedUntilExpectedCadenceExpires()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var ns = new FakeNamespace(local);
        var timeProvider = new TestTimeProvider();
        ns.Options.ExpectedUpdateCadence = TimeSpan.FromSeconds(2);
        var protocol = CreateProtocol(transport, ns, timeProvider: timeProvider);

        await protocol.ReceiveBroadcast(
            CreateBroadcastBatch(peer, ns.CreateItem(peer, FakeNamespace.DefaultKey, sequence: 1)),
            CancellationToken.None);

        await protocol.RunAntiEntropyRound(CancellationToken.None);
        Assert.Empty(transport.AntiEntropyRequests);

        timeProvider.Advance(TimeSpan.FromSeconds(2) - TimeSpan.FromMilliseconds(1));
        await protocol.RunAntiEntropyRound(CancellationToken.None);
        Assert.Empty(transport.AntiEntropyRequests);

        timeProvider.Advance(TimeSpan.FromMilliseconds(1));
        await protocol.RunAntiEntropyRound(CancellationToken.None);
        var request = Assert.Single(transport.AntiEntropyRequests).Request;
        var digest = Assert.Single(request.Digests[ns.Name]);
        Assert.Equal(FakeNamespace.DefaultKey, digest.Key);
    }

    [Fact]
    public async Task AntiEntropyAppliesReturnedRepairItemsWithoutForwarding()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var ns = new FakeNamespace(local);
        var protocol = CreateProtocol(transport, ns);
        ns.ExpectedKeys.Add(FakeNamespace.DefaultKey);
        var repairItem = ns.CreateItem(peer, FakeNamespace.DefaultKey, sequence: 7);
        transport.ExchangeAntiEntropyHandler = (target, request) => ValueTask.FromResult(new DisseminationAntiEntropyResponse
        {
            Sender = target,
            Values = CreateValueGroups(repairItem),
        });

        await protocol.RunAntiEntropyRound(CancellationToken.None);

        Assert.Equal(7, ns.GetVersion(FakeNamespace.DefaultKey));
        Assert.Empty(transport.BroadcastBatches);
        Assert.Single(transport.AntiEntropyRequests);
        var digestByNamespace = Assert.Single(transport.AntiEntropyRequests[0].Request.Digests);
        Assert.Equal(ns.Name, digestByNamespace.Key);
        var digest = Assert.Single(digestByNamespace.Value);
        Assert.Equal(FakeNamespace.DefaultKey, digest.Key);
        Assert.Equal(0, digest.Version);
    }

    [Fact]
    public async Task AntiEntropyAppliesValidItemsAfterFailedRepairItem()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var ns = new FakeNamespace(local);
        var protocol = CreateProtocol(transport, ns);
        ns.SetValue(FakeNamespace.DefaultKey, version: 1);
        var badRepairItem = new DisseminationBroadcastValue
        {
            Value = new DisseminationValue(FakeNamespace.DefaultKey, fromVersion: 1, toVersion: 2, Array.Empty<byte>()),
            Originator = peer,
            ExpiresAt = TimeProvider.System.GetUtcNow().AddMinutes(1),
        };
        var goodRepairItem = ns.CreateItem(peer, FakeNamespace.DefaultKey, sequence: 3);
        transport.ExchangeAntiEntropyHandler = (target, request) => ValueTask.FromResult(new DisseminationAntiEntropyResponse
        {
            Sender = target,
            Values = CreateValueGroups(badRepairItem, goodRepairItem),
        });

        await protocol.RunAntiEntropyRound(CancellationToken.None);

        Assert.Equal(3, ns.GetVersion(FakeNamespace.DefaultKey));
    }

    [Fact]
    public async Task AntiEntropyAppliesValidItemsAfterFailedRepairRound()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var ns = new FakeNamespace(local);
        var protocol = CreateProtocol(transport, ns);
        ns.SetValue(FakeNamespace.DefaultKey, version: 1);

        var badRepairItem = new DisseminationBroadcastValue
        {
            Value = new DisseminationValue(FakeNamespace.DefaultKey, fromVersion: 1, toVersion: 2, Array.Empty<byte>()),
            Originator = peer,
            ExpiresAt = TimeProvider.System.GetUtcNow().AddMinutes(1),
        };
        var goodRepairItem = ns.CreateItem(peer, FakeNamespace.DefaultKey, sequence: 3);
        var exchangeCount = 0;
        transport.ExchangeAntiEntropyHandler = (target, request) =>
        {
            var count = Interlocked.Increment(ref exchangeCount);
            return ValueTask.FromResult(new DisseminationAntiEntropyResponse
            {
                Sender = target,
                Values = count switch
                {
                    1 => CreateValueGroups(badRepairItem),
                    2 => CreateValueGroups(goodRepairItem),
                    _ => FrozenDictionary<DisseminationNamespace, ImmutableArray<DisseminationBroadcastValue>>.Empty,
                },
            });
        };

        await protocol.RunAntiEntropyRound(CancellationToken.None);
        await protocol.RunAntiEntropyRound(CancellationToken.None);

        Assert.Equal(3, ns.GetVersion(FakeNamespace.DefaultKey));
        Assert.Equal(2, Volatile.Read(ref exchangeCount));
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
    public async Task MembershipNamespaceReturnsAndAppliesDiffWhenPeerVersionIsRetained()
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
        var sourceNamespace = CreateMembershipNamespace(sourceManager, serializer);
        var peerDigest = Assert.Single(sourceNamespace.GetDigest());
        sourceManager.CurrentSnapshot = updatedSnapshot;
        var localDigest = Assert.Single(sourceNamespace.GetDigest());

        Assert.True(sourceNamespace.TryCreateRepairValue(localDigest.Key, peerDigest.Value, out var value));
        Assert.Equal(peerDigest.Value, value.FromVersion);
        Assert.Equal(localDigest.Value, value.ToVersion);
        var update = serializer.Deserialize<MembershipTableSnapshotUpdate>(value.Payload);
        Assert.NotNull(update.Diff);
        Assert.Null(update.Snapshot);
        var receiverManager = new FakeMembershipManager(baseSnapshot);
        var receiverNamespace = CreateMembershipNamespace(receiverManager, serializer);
        var result = await receiverNamespace.ApplyValueAsync(value, CancellationToken.None);

        Assert.Equal(DisseminationApplyResult.Applied, result);
        Assert.Equal(updatedSnapshot.Version, receiverManager.CurrentSnapshot.Version);
        Assert.True(receiverManager.CurrentSnapshot.Entries.ContainsKey(peer));
    }

    [Fact]
    public void DisseminationKeyRoundTripsDefaultAndSiloAddressKeys()
    {
        var earlierPeer = CreateSilo(11111);
        var peer = CreateSilo(11112);
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var membershipValue = new DisseminationValue(DisseminationKey.Default, fromVersion: 0, toVersion: 7, Array.Empty<byte>());
        var deploymentLoadValue = new DisseminationValue(peer, fromVersion: 0, toVersion: 11, Array.Empty<byte>());

        var roundTripMembershipValue = serializer.Deserialize<DisseminationValue>(serializer.SerializeToArray(membershipValue));
        var roundTripDeploymentLoadValue = serializer.Deserialize<DisseminationValue>(serializer.SerializeToArray(deploymentLoadValue));

        Assert.Equal(DisseminationKey.Default, roundTripMembershipValue.Key);
        Assert.Equal(new DisseminationKey(peer), roundTripDeploymentLoadValue.Key);

        Span<char> expected = stackalloc char[128];
        Span<char> actual = stackalloc char[128];
        Assert.True(((ISpanFormattable)peer).TryFormat(expected, out var expectedLength, "H", null));
        Assert.True(roundTripDeploymentLoadValue.Key.TryFormat(actual, out var actualLength, "H", null));
        Assert.Equal(expected[..expectedLength].ToString(), actual[..actualLength].ToString());
        Assert.True(roundTripMembershipValue.Key.TryFormat(Span<char>.Empty, out var nullKeyLength, default, null));
        Assert.Equal(0, nullKeyLength);
        Assert.Equal(
            Math.Sign(earlierPeer.CompareTo(peer)),
            Math.Sign(Comparer<object>.Default.Compare(earlierPeer, peer)));
        Assert.Equal(
            Math.Sign(earlierPeer.CompareTo(peer)),
            Math.Sign(new DisseminationKey(earlierPeer).CompareTo(new DisseminationKey(peer))));
    }

    [Fact]
    public void DisseminationNamespaceWrapsNonEmptyStringsForDictionaryKeys()
    {
        DisseminationNamespace namespaceName = "load";
        var namespaces = new Dictionary<DisseminationNamespace, int>
        {
            [namespaceName] = 1,
        };

        Assert.Equal("load", (string)namespaceName);
        Assert.True(namespaces.TryGetValue(new DisseminationNamespace("load"), out var value));
        Assert.Equal(1, value);
        Assert.True(namespaceName.CompareTo(new DisseminationNamespace("membership")) < 0);
        Assert.ThrowsAny<ArgumentException>(() => new DisseminationNamespace(string.Empty));
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
    public void NamespaceOptionsUseExpectedUpdateCadenceDefaults()
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
        Assert.Equal(options.MaxBatchBytes, new DisseminationNamespaceOptions().MaxPayloadBytes);
    }

    [Fact]
    public void DisseminationMembershipReturnsCachedSnapshotForSameMembershipVersion()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var membershipManager = new FakeMembershipManager(CreateMembershipSnapshot(
            version: 1,
            CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch),
            CreateMembershipEntry(peer, SiloStatus.Joining, DateTime.UnixEpoch.AddSeconds(1))));
        var membership = new DisseminationMembership(
            membershipManager,
            new FakeLocalSiloDetails(local),
            Options.Create(new DisseminationOptions()));

        var first = membership.CurrentSnapshot;
        var second = membership.CurrentSnapshot;

        Assert.Same(first, second);
        Assert.Equal(new MembershipVersion(1), first.MembershipVersion);
        Assert.Equal(new[] { local, peer }, first.AllMembers);
        Assert.Equal(new[] { local }, first.ActiveMembers);
    }

    [Fact]
    public void DisseminationMembershipRecomputesSnapshotWhenMembershipVersionChanges()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var membershipManager = new FakeMembershipManager(CreateMembershipSnapshot(
            version: 1,
            CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch)));
        var membership = new DisseminationMembership(
            membershipManager,
            new FakeLocalSiloDetails(local),
            Options.Create(new DisseminationOptions()));
        var first = membership.CurrentSnapshot;

        membershipManager.CurrentSnapshot = CreateMembershipSnapshot(
            version: 2,
            CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch),
            CreateMembershipEntry(peer, SiloStatus.Active, DateTime.UnixEpoch.AddSeconds(1)));

        var second = membership.CurrentSnapshot;

        Assert.NotSame(first, second);
        Assert.Equal(new MembershipVersion(2), second.MembershipVersion);
        Assert.Equal(new[] { local, peer }, second.ActiveMembers);
    }

    [Fact]
    public async Task DisseminationMembershipRefreshDelegatesToMembershipManager()
    {
        var local = CreateSilo(11111);
        var membershipManager = new FakeMembershipManager(CreateMembershipSnapshot(
            version: 1,
            CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch)));
        var membership = new DisseminationMembership(
            membershipManager,
            new FakeLocalSiloDetails(local),
            Options.Create(new DisseminationOptions()));

        await membership.RefreshMembership(CancellationToken.None);

        Assert.Equal(1, membershipManager.RefreshCallCount);
        Assert.Null(Assert.Single(membershipManager.RefreshTargetVersions));
    }

    [Fact]
    public void DisseminationMembershipSnapshotRejectsDuplicateMembers()
    {
        var local = CreateSilo(11111);

        var exception = Assert.Throws<ArgumentException>(() => new DisseminationMembershipSnapshot(
            new MembershipVersion(1),
            local,
            [local, local],
            [local],
            new DisseminationOverlayOptions()));

        Assert.Equal("allMembers", exception.ParamName);
    }

    [Fact]
    public void DisseminationMembershipSnapshotRejectsActiveMembersOutsideAllMembers()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);

        var exception = Assert.Throws<ArgumentException>(() => new DisseminationMembershipSnapshot(
            new MembershipVersion(1),
            local,
            [local],
            [local, peer],
            new DisseminationOverlayOptions()));

        Assert.Equal("activeMembers", exception.ParamName);
    }

    [Fact]
    public void DisseminationMembershipSnapshotUsesStoredTreeOrderForOriginatorTargets()
    {
        var root = CreateSilo(11113);
        var first = CreateSilo(11115);
        var second = CreateSilo(11112);
        var snapshot = new DisseminationMembershipSnapshot(
            new MembershipVersion(1),
            root,
            [root, first, second],
            [root, first, second],
            CreateOverlayOptions(fanout: 2));

        var targets = snapshot.GetOriginatorTreeTargets(
            DisseminationGroup.ActiveMembers);

        Assert.Equal(new[] { first, second }, targets);
    }

    [Fact]
    public void DisseminationMembershipSnapshotUsesStoredTreeOrderForForwardingTargets()
    {
        var local = CreateSilo(11111);
        var root = CreateSilo(11112);
        var sender = CreateSilo(11113);
        var child = CreateSilo(11114);
        var snapshot = new DisseminationMembershipSnapshot(
            new MembershipVersion(1),
            local,
            [local, root, sender, child],
            [local, root, sender, child],
            CreateOverlayOptions(fanout: 2));

        var targets = snapshot.GetForwardingTreeTargets(
            DisseminationGroup.ActiveMembers);

        Assert.Equal(new[] { sender, child }, targets);
    }

    [Fact]
    public void DisseminationMembershipSnapshotDoesNotAddLocalSiloWhenMissing()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var snapshot = new DisseminationMembershipSnapshot(
            new MembershipVersion(1),
            local,
            [peer],
            [peer],
            CreateOverlayOptions(fanout: 1));

        var targets = snapshot.GetOriginatorTreeTargets(
            DisseminationGroup.ActiveMembers);
        Span<SiloAddress> antiEntropyPeers = new SiloAddress[1];
        snapshot.SelectAntiEntropyPeers(DisseminationGroup.ActiveMembers, ref antiEntropyPeers);

        Assert.False(snapshot.ContainsMember(local, DisseminationGroup.ActiveMembers));
        Assert.Single(snapshot.ActiveMembers);
        Assert.Empty(targets);
        Assert.Equal(0, antiEntropyPeers.Length);
    }

    [Fact]
    public void DisseminationMembershipSnapshotReturnsAllAntiEntropyCandidatesWhenRequestedCountIsLarge()
    {
        const int peerCount = 20;
        const int localIndex = 7;
        var silos = CreateSilos(12);
        var local = silos[localIndex];
        var expected = silos.Where(silo => !Equals(silo, local)).OrderBy(static silo => silo);
        var snapshot = new DisseminationMembershipSnapshot(
            new MembershipVersion(1),
            local,
            [.. silos],
            [.. silos],
            new DisseminationOverlayOptions());

        Span<SiloAddress> peers = new SiloAddress[peerCount];
        snapshot.SelectAntiEntropyPeers(DisseminationGroup.ActiveMembers, ref peers);
        var selectedPeers = peers.ToArray();

        Assert.Equal(silos.Length - 1, selectedPeers.Length);
        Assert.Equal(selectedPeers.Length, selectedPeers.Distinct().Count());
        Assert.DoesNotContain(local, selectedPeers);
        Assert.Equal(expected, selectedPeers.OrderBy(static silo => silo));
    }

    private static DisseminationProtocol CreateProtocol(
        FakeTransport transport,
        FakeNamespace ns,
        Action<DisseminationOptions>? configure = null,
        TimeProvider? timeProvider = null) =>
        CreateProtocol(transport, new IDisseminationNamespace[] { ns }, configure, timeProvider);

    private static DisseminationProtocol CreateProtocol(
        FakeTransport transport,
        IReadOnlyList<IDisseminationNamespace> namespaces,
        Action<DisseminationOptions>? configure = null,
        TimeProvider? timeProvider = null)
    {
        var options = new DisseminationOptions { Enabled = true };
        configure?.Invoke(options);
        return new DisseminationProtocol(
            transport,
            new DisseminationMembership(
                transport.MembershipManager,
                new FakeLocalSiloDetails(transport.LocalSilo),
                Options.Create(options)),
            new TestOptionsMonitor<DisseminationOptions>(options),
            namespaces,
            timeProvider ?? TimeProvider.System,
            NullLogger<DisseminationProtocol>.Instance);
    }

    private static DisseminationOverlayOptions CreateOverlayOptions(int fanout) => new()
    {
        FanOutFactor = _ => fanout,
    };

    private static SiloAddress CreateSilo(int port) => SiloAddress.New(new IPEndPoint(IPAddress.Loopback, port), port);

    private static SiloAddress[] CreateSilos(int count) =>
        Enumerable.Range(11111, count).Select(CreateSilo).OrderBy(static silo => silo).ToArray();

    private static FrozenDictionary<DisseminationNamespace, ImmutableArray<DigestEntry>> CreateAntiEntropyRequestDigest(
        DisseminationNamespace namespaceName,
        params (DisseminationKey Key, long Version)[] versions) =>
        new Dictionary<DisseminationNamespace, ImmutableArray<DigestEntry>>
        {
            [namespaceName] = versions
                .Select(static entry => new DigestEntry(entry.Key, entry.Version))
                .ToImmutableArray(),
        }.ToFrozenDictionary();

    private static DisseminationBroadcastBatch CreateBroadcastBatch(SiloAddress sender, params DisseminationBroadcastValue[] values) => new()
    {
        Sender = sender,
        Values = CreateValueGroups(values),
    };

    private static IEnumerable<DisseminationBroadcastValue> GetBroadcastValues(DisseminationBroadcastBatch batch) =>
        batch.Values.Values.SelectMany(static values => values);

    private static IEnumerable<DisseminationBroadcastValue> GetAntiEntropyResponseValues(DisseminationAntiEntropyResponse response) =>
        response.Values.Values.SelectMany(static values => values);

    private static FrozenDictionary<DisseminationNamespace, ImmutableArray<DisseminationBroadcastValue>> CreateValueGroups(params DisseminationBroadcastValue[] values) =>
        CreateValueGroups(FakeNamespace.DefaultName, values);

    private static FrozenDictionary<DisseminationNamespace, ImmutableArray<DisseminationBroadcastValue>> CreateValueGroups(DisseminationNamespace namespaceName, params DisseminationBroadcastValue[] values) =>
        new Dictionary<DisseminationNamespace, ImmutableArray<DisseminationBroadcastValue>>
        {
            [namespaceName] = [.. values],
        }.ToFrozenDictionary();

    private static MembershipDisseminationNamespace CreateMembershipNamespace(
        FakeMembershipManager membershipManager,
        Serializer serializer) =>
        new(
            membershipManager,
            new TestOptionsMonitor<ClusterMembershipOptions>(new ClusterMembershipOptions()),
            serializer);

    private static DisseminationBroadcastValue CreateDisseminationValue(SiloAddress originator, DisseminationValue value) => new()
    {
        Value = value,
        Originator = originator,
        ExpiresAt = TimeProvider.System.GetUtcNow().AddMinutes(1),
    };

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

    private sealed class FakeNamespace : IDisseminationNamespace
    {
        public static readonly DisseminationNamespace DefaultName = new("fake-namespace");
        public static readonly DisseminationKey DefaultKey = new("value");
        private readonly Dictionary<DisseminationKey, long> _versions = new();
        private readonly DisseminationNamespace _name;

        public FakeNamespace(SiloAddress localSilo, DisseminationNamespace? name = null)
        {
            _ = localSilo;
            _name = name ?? DefaultName;
        }

        public Dictionary<DisseminationKey, int> ApplyCounts { get; } = new();

        public HashSet<DisseminationKey> ExpectedKeys { get; } = new();

        public DisseminationNamespace Name => _name;

        public DisseminationGroup Group { get; set; } = DisseminationGroup.ActiveMembers;

        public DisseminationNamespaceOptions Options { get; } = new() { Enabled = true };

        public DisseminationValue CreateValue(DisseminationKey key, long sequence, long fromVersion = 0) => new(
            key,
            fromVersion,
            sequence,
            BitConverter.GetBytes(sequence));

        public DisseminationBroadcastValue CreateItem(SiloAddress originator, DisseminationKey key, long sequence, long fromVersion = 0) =>
            CreateDisseminationValue(originator, CreateValue(key, sequence, fromVersion));

        public void SetValue(DisseminationKey key, long version) => _versions[key] = version;

        public void Clear() => _versions.Clear();

        public long GetVersion(DisseminationKey key) => _versions.TryGetValue(key, out var version) ? version : 0;

        public IReadOnlyDictionary<DisseminationKey, long> GetDigest()
        {
            var digest = new Dictionary<DisseminationKey, long>(_versions);
            foreach (var key in ExpectedKeys)
            {
                digest.TryAdd(key, 0);
            }

            return digest;
        }

        public bool TryCreateRepairValue(
            DisseminationKey key,
            long peerVersion,
            out DisseminationValue value)
        {
            if (!_versions.TryGetValue(key, out var version)
                || version <= peerVersion)
            {
                value = default;
                return false;
            }

            value = CreateValue(key, version);
            return true;
        }

        public ValueTask<DisseminationApplyResult> ApplyValueAsync(
            DisseminationValue value,
            CancellationToken cancellationToken)
        {
            var version = BitConverter.ToInt64(value.Payload.Span);
            if (version != value.ToVersion)
            {
                return ValueTask.FromResult(DisseminationApplyResult.Rejected);
            }

            if (_versions.TryGetValue(value.Key, out var current))
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

            _versions[value.Key] = version;
            ApplyCounts[value.Key] = ApplyCounts.TryGetValue(value.Key, out var count) ? count + 1 : 1;
            return ValueTask.FromResult(DisseminationApplyResult.Applied);
        }

    }

    private sealed class FakeTransport : IDisseminationTransport
    {
        private readonly SiloAddress _localSilo;
        private readonly List<SiloAddress> _peers;

        public FakeTransport(SiloAddress localSilo, params SiloAddress[] peers)
        {
            _localSilo = localSilo;
            _peers = peers.ToList();
            MembershipManager = new FakeMembershipManager(GetMembershipSnapshot, RefreshMembership);
        }

        public List<(SiloAddress Peer, DisseminationBroadcastBatch Batch)> BroadcastBatches { get; } = new();

        public List<(SiloAddress Peer, DisseminationAntiEntropyRequest Request)> AntiEntropyRequests { get; } = new();

        public FakeMembershipManager MembershipManager { get; }

        public List<SiloAddress> Peers => _peers;

        public Dictionary<SiloAddress, SiloStatus> PeerStatuses { get; } = new();

        public Dictionary<SiloAddress, DateTime> StartTimes { get; } = new();

        public Func<SiloAddress, DisseminationBroadcastBatch, CancellationToken, Task>? SendBroadcastHandler { get; set; }

        public Func<SiloAddress, DisseminationAntiEntropyRequest, ValueTask<DisseminationAntiEntropyResponse>> ExchangeAntiEntropyHandler { get; set; } =
            static (peer, _) => ValueTask.FromResult(new DisseminationAntiEntropyResponse { Sender = peer });

        public Func<CancellationToken, Task>? RefreshMembershipHandler { get; set; }

        public SiloAddress LocalSilo => _localSilo;

        public int RefreshMembershipCallCount { get; private set; }

        private MembershipTableSnapshot GetMembershipSnapshot()
        {
            var entries = _peers.Append(_localSilo).Distinct().Select(peer =>
            {
                var status = PeerStatuses.TryGetValue(peer, out var peerStatus) ? peerStatus : SiloStatus.Active;
                var startTime = StartTimes.TryGetValue(peer, out var value) ? value : DateTime.UnixEpoch;
                return CreateMembershipEntry(peer, status, startTime);
            }).ToArray();

            return new MembershipTableSnapshot(
                new MembershipVersion(ComputeMembershipVersion(entries)),
                entries.ToImmutableDictionary(static entry => entry.SiloAddress));
        }

        private Task RefreshMembership(CancellationToken cancellationToken)
        {
            RefreshMembershipCallCount++;
            return RefreshMembershipHandler?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }

        public Task SendBroadcast(SiloAddress peer, DisseminationBroadcastBatch batch, CancellationToken cancellationToken)
        {
            if (SendBroadcastHandler is not null)
            {
                return SendBroadcastHandler(peer, batch, cancellationToken);
            }

            lock (BroadcastBatches)
            {
                BroadcastBatches.Add((peer, batch));
            }

            return Task.CompletedTask;
        }

        public ValueTask<DisseminationAntiEntropyResponse> ExchangeAntiEntropy(
            SiloAddress peer,
            DisseminationAntiEntropyRequest request,
            CancellationToken cancellationToken)
        {
            lock (AntiEntropyRequests)
            {
                AntiEntropyRequests.Add((peer, request));
            }

            return ExchangeAntiEntropyHandler(peer, request);
        }

        private static long ComputeMembershipVersion(IEnumerable<MembershipEntry> entries)
        {
            var result = 17L;
            foreach (var entry in entries.OrderBy(static entry => entry.SiloAddress))
            {
                result = unchecked((result * 31) + entry.SiloAddress.GetConsistentHashCode());
                result = unchecked((result * 31) + (int)entry.Status);
                result = unchecked((result * 31) + entry.StartTime.Ticks);
            }

            return result == MembershipVersion.MinValue.Value ? result + 1 : result;
        }
    }

    private sealed class FakeLocalSiloDetails(SiloAddress siloAddress) : ILocalSiloDetails
    {
        public string Name => "test";

        public string ClusterId => "test";

        public string DnsHostName => "localhost";

        public SiloAddress SiloAddress => siloAddress;

        public SiloAddress GatewayAddress => siloAddress;
    }

#if NET10_0_OR_GREATER
    private sealed class MonotonicDisseminationHarness(SiloAddress localSilo)
    {
        private readonly FakeNamespace _topic = new(localSilo);

        public void Reset() => _topic.Clear();

        public async Task<ModelApplyResponse> Receive(long version)
        {
            var value = _topic.CreateValue(FakeNamespace.DefaultKey, version);
            var result = await _topic.ApplyValueAsync(value, CancellationToken.None);
            return new ModelApplyResponse(ToModelResult(result), _topic.GetVersion(FakeNamespace.DefaultKey));
        }

        public async Task<ModelRepairResponse> RepairPeer(long peerVersion)
        {
            var localDigest = _topic.GetDigest().SingleOrDefault();
            if (localDigest.Value == 0)
            {
                return new ModelRepairResponse(false, 0);
            }

            if (localDigest.Value <= peerVersion)
            {
                return new ModelRepairResponse(false, 0);
            }

            return _topic.TryCreateRepairValue(
                FakeNamespace.DefaultKey,
                peerVersion,
                out var value)
                ? new ModelRepairResponse(true, value.ToVersion)
                : new ModelRepairResponse(false, 0);
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

    private sealed class FakeMembershipManager : IMembershipManager
    {
        private readonly Func<MembershipTableSnapshot>? _getCurrentSnapshot;
        private readonly Func<CancellationToken, Task>? _refresh;
        private MembershipTableSnapshot _currentSnapshot;

        public FakeMembershipManager(MembershipTableSnapshot currentSnapshot)
        {
            _currentSnapshot = currentSnapshot;
        }

        public FakeMembershipManager(
            Func<MembershipTableSnapshot> getCurrentSnapshot,
            Func<CancellationToken, Task> refresh)
        {
            _getCurrentSnapshot = getCurrentSnapshot;
            _refresh = refresh;
            _currentSnapshot = getCurrentSnapshot();
        }

        public MembershipTableSnapshot CurrentSnapshot
        {
            get => _getCurrentSnapshot?.Invoke() ?? _currentSnapshot;
            set => _currentSnapshot = value;
        }

        public int RefreshCallCount { get; private set; }

        public List<MembershipVersion?> RefreshTargetVersions { get; } = new();

        public IAsyncEnumerable<MembershipTableSnapshot> MembershipUpdates => EmptyUpdates();

        public SiloStatus LocalSiloStatus => SiloStatus.Active;

        public Task UpdateLocalStatus(SiloStatus status, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> TryKillSilo(SiloAddress silo, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<bool> TrySuspectSilo(SiloAddress silo, SiloAddress? indirectProbingSilo, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task Refresh(MembershipVersion? targetVersion, CancellationToken cancellationToken)
        {
            RefreshCallCount++;
            RefreshTargetVersions.Add(targetVersion);
            return _refresh?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }

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
