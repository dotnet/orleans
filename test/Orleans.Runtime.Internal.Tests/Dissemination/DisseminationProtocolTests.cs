#nullable enable

using System;
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
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
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
    public void PushBroadcastUsesRequestResponseSemantics()
    {
        var method = typeof(IDisseminationSystemTarget).GetMethod(nameof(IDisseminationSystemTarget.PushBroadcast));

        Assert.NotNull(method);
        Assert.False(method.IsDefined(typeof(Orleans.Concurrency.OneWayAttribute), inherit: false));
        Assert.Equal(typeof(Task<DisseminationBroadcastResponse>), method.ReturnType);
    }

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

        var result = await PublishValue(protocol, ns, item, CancellationToken.None);
        await protocol.FlushPendingBroadcast(CancellationToken.None);

        Assert.True(result);
        var expectedChildren = GetOriginatorTreeTargets(local, peers, fanout: 2);
        Assert.Equal(expectedChildren.OrderBy(static peer => peer), transport.BroadcastBatches.Select(batch => batch.Peer).OrderBy(static peer => peer));
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

        var result = await PublishValue(protocol, ns, item, CancellationToken.None);
        await protocol.FlushPendingBroadcast(CancellationToken.None);

        Assert.True(result);
        Assert.Equal(
            GetOriginatorTreeTargets(local, peers, fanout: 2).OrderBy(static peer => peer),
            transport.BroadcastBatches.Select(batch => batch.Peer).OrderBy(static peer => peer));
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

        Assert.True(await PublishValue(protocol, ns, item, CancellationToken.None));
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

        Assert.False(await PublishValue(protocol, ns, oversized, CancellationToken.None));
        Assert.False(await protocol.Publish(ns, "obsolete", version: 11, CancellationToken.None));
        await protocol.FlushPendingBroadcast(CancellationToken.None);

        Assert.Empty(transport.BroadcastBatches);
    }

    [Fact]
    public async Task PublishReturnsFalseWhenRootIsMissingFromMembership()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        transport.PeerStatuses[local] = SiloStatus.Dead;
        var ns = new FakeNamespace(local);
        var protocol = CreateProtocol(transport, ns);
        var item = ns.CreateValue(FakeNamespace.DefaultKey, sequence: 1);

        var result = await PublishValue(protocol, ns, item, CancellationToken.None);
        await protocol.FlushPendingBroadcast(CancellationToken.None);

        Assert.False(result);
        Assert.Equal(1, transport.RefreshMembershipCallCount);
        Assert.Empty(transport.BroadcastBatches);
    }

    [Fact]
    public async Task PublishContinuesAfterPeerSendFailure()
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

        var ns = new FakeNamespace(local);
        var protocol = CreateProtocol(transport, ns, options =>
        {
            options.MaxConcurrentSends = 1;
            options.Overlay.FanOutFactor = static _ => 2;
        });
        var value = ns.CreateValue(FakeNamespace.DefaultKey, sequence: 1);

        var result = await PublishValue(protocol, ns, value, CancellationToken.None);
        await protocol.FlushPendingBroadcast(CancellationToken.None);

        Assert.True(result);
        Assert.Equal(new[] { active }, transport.BroadcastBatches.Select(batch => batch.Peer));
    }

    [Fact]
    public async Task NewPublicationWakesFailedPeerWithoutWaitingForBackoff()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var timeProvider = new FakeTimeProvider();
        var sendCount = 0;
        transport.SendBroadcastHandler = (target, batch, cancellationToken) =>
        {
            if (Interlocked.Increment(ref sendCount) == 1)
            {
                throw new InvalidOperationException("transient send failure");
            }

            transport.BroadcastBatches.Add((target, batch));
            return Task.CompletedTask;
        };

        var ns = new FakeNamespace(local);
        var protocol = CreateProtocol(transport, ns, timeProvider: timeProvider);

        var firstResult = await PublishValue(protocol, ns, ns.CreateValue(FakeNamespace.DefaultKey, sequence: 1), CancellationToken.None);
        await protocol.FlushPendingBroadcast(CancellationToken.None);
        var secondResult = await PublishValue(protocol, ns, ns.CreateValue(FakeNamespace.DefaultKey, sequence: 2), CancellationToken.None);
        await protocol.FlushPendingBroadcast(CancellationToken.None);

        Assert.True(firstResult);
        Assert.True(secondResult);
        Assert.Equal(2, sendCount);
        var batch = Assert.Single(transport.BroadcastBatches);
        Assert.Equal(2, GetBroadcastValues(batch.Batch).Single().Value.ToVersion);
    }

    [Fact]
    public async Task SendFailureRetriesAutomaticallyWithBoundedBackoff()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var timeProvider = new FakeTimeProvider();
        var firstAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendCount = 0;
        transport.SendBroadcastHandler = (target, batch, cancellationToken) =>
        {
            if (Interlocked.Increment(ref sendCount) == 1)
            {
                firstAttempt.TrySetResult();
                throw new InvalidOperationException("transient send failure");
            }

            transport.BroadcastBatches.Add((target, batch));
            secondAttempt.TrySetResult();
            return Task.CompletedTask;
        };

        var ns = new FakeNamespace(local);
        ns.Options.MaxCoalescingDelay = TimeSpan.FromSeconds(1);
        var protocol = CreateProtocol(
            transport,
            ns,
            options => options.Overlay.AntiEntropyInterval = TimeSpan.FromSeconds(4),
            timeProvider);

        Assert.True(await PublishValue(
            protocol,
            ns,
            ns.CreateValue(FakeNamespace.DefaultKey, sequence: 1),
            CancellationToken.None));
        using var schedule = new BroadcastScheduleObserver();
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await firstAttempt.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await schedule.WaitAsync(
            e => e.Peer.Equals(peer) && e.Reason == DisseminationBroadcastScheduleReason.Retry,
            TimeSpan.FromSeconds(5));

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await secondAttempt.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, sendCount);
        Assert.Single(transport.BroadcastBatches);
        await protocol.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task NewNotificationResetsRetryBackoff()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var timeProvider = new FakeTimeProvider();
        var firstAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendCount = 0;
        transport.SendBroadcastHandler = (target, batch, cancellationToken) =>
        {
            switch (Interlocked.Increment(ref sendCount))
            {
                case 1:
                    firstAttempt.TrySetResult();
                    throw new InvalidOperationException("first transient send failure");
                case 2:
                    secondAttempt.TrySetResult();
                    throw new InvalidOperationException("second transient send failure");
                default:
                    transport.BroadcastBatches.Add((target, batch));
                    thirdAttempt.TrySetResult();
                    return Task.CompletedTask;
            }
        };

        var ns = new FakeNamespace(local);
        ns.Options.MaxCoalescingDelay = TimeSpan.FromSeconds(1);
        var protocol = CreateProtocol(
            transport,
            ns,
            options => options.Overlay.AntiEntropyInterval = TimeSpan.FromSeconds(4),
            timeProvider);

        Assert.True(await PublishValue(
            protocol,
            ns,
            ns.CreateValue(FakeNamespace.DefaultKey, sequence: 1),
            CancellationToken.None));
        using var schedule = new BroadcastScheduleObserver();
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await firstAttempt.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await schedule.WaitAsync(
            e => e.Peer.Equals(peer) && e.Reason == DisseminationBroadcastScheduleReason.Retry && e.Attempt == 1,
            TimeSpan.FromSeconds(5));
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await secondAttempt.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await schedule.WaitAsync(
            e => e.Peer.Equals(peer) && e.Reason == DisseminationBroadcastScheduleReason.Retry && e.Attempt == 2,
            TimeSpan.FromSeconds(5));

        Assert.True(await PublishValue(
            protocol,
            ns,
            ns.CreateValue(FakeNamespace.DefaultKey, sequence: 2),
            CancellationToken.None));
        timeProvider.Advance(TimeSpan.FromMilliseconds(999));
        Assert.False(thirdAttempt.Task.IsCompleted);
        timeProvider.Advance(TimeSpan.FromMilliseconds(1));
        await thirdAttempt.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(3, sendCount);
        Assert.Equal(2, Assert.Single(GetBroadcastValues(Assert.Single(transport.BroadcastBatches).Batch)).Value.ToVersion);
        await protocol.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HighPriorityNamespaceFlushesWithoutWaitingForCoalescingWindow()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var timeProvider = new FakeTimeProvider();
        var sent = new TaskCompletionSource<DisseminationBroadcastBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.SendBroadcastHandler = (target, batch, cancellationToken) =>
        {
            transport.BroadcastBatches.Add((target, batch));
            sent.TrySetResult(batch);
            return Task.CompletedTask;
        };

        var ns = new FakeNamespace(local);
        ns.Options.Priority = DisseminationPriority.High;
        // A long window would delay a normal namespace; a high-priority namespace must ignore it.
        ns.Options.MaxCoalescingDelay = TimeSpan.FromMinutes(1);
        var protocol = CreateProtocol(transport, ns, timeProvider: timeProvider);
        using var schedule = new BroadcastScheduleObserver();

        Assert.True(await PublishValue(
            protocol,
            ns,
            ns.CreateValue(FakeNamespace.DefaultKey, sequence: 1),
            CancellationToken.None));

        var scheduled = await schedule.WaitAsync(
            e => e.Peer.Equals(peer) && e.Reason == DisseminationBroadcastScheduleReason.Priority,
            TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.Zero, scheduled.DueTime);

        // Advancing far less than the coalescing window still delivers the update immediately.
        timeProvider.Advance(TimeSpan.FromMilliseconds(1));
        var batch = await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, Assert.Single(GetBroadcastValues(batch)).Value.ToVersion);
        await protocol.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HighPriorityNotificationPullsPendingCoalescedFlushForward()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var timeProvider = new FakeTimeProvider();
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.SendBroadcastHandler = (target, batch, cancellationToken) =>
        {
            transport.BroadcastBatches.Add((target, batch));
            sent.TrySetResult();
            return Task.CompletedTask;
        };

        var normalNamespace = new FakeNamespace(local, new DisseminationNamespace("normal"));
        normalNamespace.Options.MaxCoalescingDelay = TimeSpan.FromMinutes(1);
        var highNamespace = new FakeNamespace(local, new DisseminationNamespace("high"));
        highNamespace.Options.Priority = DisseminationPriority.High;
        var protocol = CreateProtocol(
            transport,
            new IDisseminationNamespace[] { normalNamespace, highNamespace },
            timeProvider: timeProvider);
        using var schedule = new BroadcastScheduleObserver();

        // A normal update on the peer pump arms a distant coalescing timer.
        Assert.True(await PublishValue(
            protocol,
            normalNamespace,
            normalNamespace.CreateValue(FakeNamespace.DefaultKey, sequence: 1),
            CancellationToken.None));
        var coalesced = await schedule.WaitAsync(
            e => e.Peer.Equals(peer) && e.Reason == DisseminationBroadcastScheduleReason.Coalesce,
            TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromMinutes(1), coalesced.DueTime);
        Assert.False(sent.Task.IsCompleted);

        // A high-priority update on the same pump must pull that flush forward to now.
        Assert.True(await PublishValue(
            protocol,
            highNamespace,
            highNamespace.CreateValue(FakeNamespace.DefaultKey, sequence: 1),
            CancellationToken.None));
        var pulled = await schedule.WaitAsync(
            e => e.Peer.Equals(peer) && e.Reason == DisseminationBroadcastScheduleReason.Priority,
            TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.Zero, pulled.DueTime);

        timeProvider.Advance(TimeSpan.FromMilliseconds(1));
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await protocol.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task NormalPriorityNamespaceStillWaitsForCoalescingWindow()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var timeProvider = new FakeTimeProvider();
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.SendBroadcastHandler = (target, batch, cancellationToken) =>
        {
            transport.BroadcastBatches.Add((target, batch));
            sent.TrySetResult();
            return Task.CompletedTask;
        };

        var ns = new FakeNamespace(local);
        ns.Options.MaxCoalescingDelay = TimeSpan.FromSeconds(1);
        Assert.Equal(DisseminationPriority.Normal, ns.Options.Priority);
        var protocol = CreateProtocol(transport, ns, timeProvider: timeProvider);
        using var schedule = new BroadcastScheduleObserver();

        Assert.True(await PublishValue(
            protocol,
            ns,
            ns.CreateValue(FakeNamespace.DefaultKey, sequence: 1),
            CancellationToken.None));
        var scheduled = await schedule.WaitAsync(
            e => e.Peer.Equals(peer) && e.Reason == DisseminationBroadcastScheduleReason.Coalesce,
            TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(1), scheduled.DueTime);

        // A sub-window advance must not release the coalesced batch.
        timeProvider.Advance(TimeSpan.FromMilliseconds(999));
        Assert.False(sent.Task.IsCompleted);

        // Crossing the window flushes it.
        timeProvider.Advance(TimeSpan.FromMilliseconds(1));
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await protocol.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HighPriorityNamespaceValuesAreSentAheadOfNormalPriority()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var timeProvider = new FakeTimeProvider();
        transport.SendBroadcastResponseHandler = (target, batch, cancellationToken) =>
        {
            transport.BroadcastBatches.Add((target, batch));
            var acknowledgments = new Dictionary<DisseminationNamespace, List<DigestEntry>>();
            foreach (var (namespaceName, values) in batch.Values)
            {
                acknowledgments[namespaceName] =
                    [.. values.Select(static value => new DigestEntry(value.Value.Key, value.Value.ToVersion))];
            }

            return Task.FromResult(new DisseminationBroadcastResponse { Acknowledgments = acknowledgments });
        };

        var normalNamespace = new FakeNamespace(local, new DisseminationNamespace("normal"));
        normalNamespace.Options.MaxCoalescingDelay = TimeSpan.FromMinutes(1);
        var highNamespace = new FakeNamespace(local, new DisseminationNamespace("high"));
        highNamespace.Options.Priority = DisseminationPriority.High;
        DisseminationOptions? optionsRef = null;
        var protocol = CreateProtocol(
            transport,
            new IDisseminationNamespace[] { normalNamespace, highNamespace },
            options =>
            {
                optionsRef = options;
                options.MaxBatchItems = 10;
            },
            timeProvider);

        Assert.True(await PublishValue(
            protocol,
            normalNamespace,
            normalNamespace.CreateValue(FakeNamespace.DefaultKey, sequence: 1),
            CancellationToken.None));
        Assert.True(await PublishValue(
            protocol,
            highNamespace,
            highNamespace.CreateValue(FakeNamespace.DefaultKey, sequence: 1),
            CancellationToken.None));

        // One value per batch, so the recorded send sequence reflects the drain order.
        optionsRef!.MaxBatchItems = 1;
        await protocol.FlushPendingBroadcast(CancellationToken.None);

        Assert.Equal(
            new[] { highNamespace.Name, normalNamespace.Name },
            transport.BroadcastBatches.Select(batch => Assert.Single(batch.Batch.Values.Keys)));
        await protocol.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PeerVersionAdvancesOnlyFromExplicitAcknowledgment()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var timeProvider = new FakeTimeProvider();
        var secondAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendCount = 0;
        transport.SendBroadcastResponseHandler = (target, batch, cancellationToken) =>
        {
            transport.BroadcastBatches.Add((target, batch));
            var acknowledgedVersion = Interlocked.Increment(ref sendCount) == 1 ? 0 : 1;
            if (acknowledgedVersion == 1)
            {
                secondAttempt.TrySetResult();
            }

            return Task.FromResult(new DisseminationBroadcastResponse
            {
                Acknowledgments = new()
                {
                    [FakeNamespace.DefaultName] =
                    [
                        new DigestEntry(FakeNamespace.DefaultKey, acknowledgedVersion),
                    ],
                },
            });
        };

        var ns = new FakeNamespace(local);
        var protocol = CreateProtocol(transport, ns, timeProvider: timeProvider);
        using var schedule = new BroadcastScheduleObserver();

        Assert.True(await PublishValue(
            protocol,
            ns,
            ns.CreateValue(FakeNamespace.DefaultKey, sequence: 1),
            CancellationToken.None));
        await protocol.FlushPendingBroadcast(CancellationToken.None);
        var retry = await schedule.WaitAsync(
            e => e.Peer.Equals(peer) && e.Reason == DisseminationBroadcastScheduleReason.Retry,
            TimeSpan.FromSeconds(5));

        Assert.Equal(1, sendCount);

        timeProvider.Advance(retry.DueTime);
        await secondAttempt.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, sendCount);
        Assert.Equal(
            new[] { 1L, 1L },
            transport.BroadcastBatches.Select(batch => Assert.Single(GetBroadcastValues(batch.Batch)).Value.ToVersion));
    }

    [Fact]
    public async Task UnsupportedNamespaceResponseStopsAutomaticRetries()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var timeProvider = new FakeTimeProvider();
        var sendCount = 0;
        transport.SendBroadcastResponseHandler = (target, batch, cancellationToken) =>
        {
            Interlocked.Increment(ref sendCount);
            return Task.FromResult(new DisseminationBroadcastResponse
            {
                UnsupportedNamespaces = [FakeNamespace.DefaultName],
            });
        };

        var ns = new FakeNamespace(local);
        ns.Options.MaxCoalescingDelay = TimeSpan.FromSeconds(1);
        var protocol = CreateProtocol(transport, ns, timeProvider: timeProvider);

        Assert.True(await PublishValue(
            protocol,
            ns,
            ns.CreateValue(FakeNamespace.DefaultKey, sequence: 1),
            CancellationToken.None));
        await protocol.FlushPendingBroadcast(CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        await Task.Delay(TimeSpan.FromMilliseconds(50));

        Assert.Equal(1, sendCount);
        await protocol.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task UnsupportedNamespaceResponseCompletesQueuedFlushWaiter()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var timeProvider = new FakeTimeProvider();
        var firstSendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.SendBroadcastResponseHandler = async (target, batch, cancellationToken) =>
        {
            firstSendStarted.TrySetResult();
            await releaseFirstSend.Task.WaitAsync(cancellationToken);
            return new DisseminationBroadcastResponse
            {
                UnsupportedNamespaces = [FakeNamespace.DefaultName],
            };
        };

        var ns = new FakeNamespace(local);
        ns.Options.MaxCoalescingDelay = TimeSpan.FromSeconds(1);
        var protocol = CreateProtocol(transport, ns, timeProvider: timeProvider);

        Assert.True(await PublishValue(
            protocol,
            ns,
            ns.CreateValue(FakeNamespace.DefaultKey, sequence: 1),
            CancellationToken.None));
        timeProvider.Advance(ns.Options.MaxCoalescingDelay);
        await firstSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await PublishValue(
            protocol,
            ns,
            ns.CreateValue(FakeNamespace.DefaultKey, sequence: 2),
            CancellationToken.None));
        var flushTask = protocol.FlushPendingBroadcast(CancellationToken.None);
        Assert.False(flushTask.IsCompleted);

        releaseFirstSend.TrySetResult();
        await flushTask.WaitAsync(TimeSpan.FromSeconds(5));
        await protocol.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RemovedPendingKeyIsDroppedWithoutRetry()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var timeProvider = new FakeTimeProvider();
        var sendCount = 0;
        transport.SendBroadcastHandler = (target, batch, cancellationToken) =>
        {
            Interlocked.Increment(ref sendCount);
            return Task.CompletedTask;
        };

        var ns = new FakeNamespace(local);
        ns.Options.MaxCoalescingDelay = TimeSpan.FromSeconds(1);
        var protocol = CreateProtocol(transport, ns, timeProvider: timeProvider);

        Assert.True(await PublishValue(
            protocol,
            ns,
            ns.CreateValue("removed", sequence: 1),
            CancellationToken.None));
        ns.RemoveValue("removed");
        await protocol.FlushPendingBroadcast(CancellationToken.None);
        var repairRequestCount = ns.RepairRequestCount;
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        await Task.Delay(TimeSpan.FromMilliseconds(50));

        Assert.Equal(0, sendCount);
        Assert.Equal(repairRequestCount, ns.RepairRequestCount);
        await protocol.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task BroadcastBatchingStopsPeerFlushAfterSendFailure()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var timeProvider = new AutoAdvancingTimeProvider(TimeSpan.FromSeconds(2));
        var sendCount = 0;
        transport.SendBroadcastHandler = (target, batch, cancellationToken) =>
        {
            if (Interlocked.Increment(ref sendCount) == 1)
            {
                throw new InvalidOperationException("transient send failure");
            }

            transport.BroadcastBatches.Add((target, batch));
            return Task.CompletedTask;
        };

        var ns = new FakeNamespace(local);
        ns.Options.MaxCoalescingDelay = TimeSpan.FromMinutes(1);
        DisseminationOptions? optionsRef = null;
        var protocol = CreateProtocol(transport, ns, options =>
        {
            optionsRef = options;
            options.MaxBatchItems = 10;
        }, timeProvider);

        Assert.True(await PublishValue(protocol, ns, ns.CreateValue("first", sequence: 1), CancellationToken.None));
        Assert.True(await PublishValue(protocol, ns, ns.CreateValue("second", sequence: 1), CancellationToken.None));
        Assert.True(await PublishValue(protocol, ns, ns.CreateValue("third", sequence: 1), CancellationToken.None));
        optionsRef!.MaxBatchItems = 1;
        await protocol.FlushPendingBroadcast(CancellationToken.None);

        Assert.Equal(1, sendCount);
        Assert.Empty(transport.BroadcastBatches);
    }

    [Fact]
    public async Task OrderedBroadcastBatchingRetriesFailedChainBeforeNewerValues()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var attempts = new List<long>();
        var failed = false;
        transport.SendBroadcastHandler = (target, batch, cancellationToken) =>
        {
            var value = Assert.Single(GetBroadcastValues(batch));
            attempts.Add(value.Value.ToVersion);
            if (value.Value.ToVersion == 2 && !failed)
            {
                failed = true;
                throw new InvalidOperationException("transient send failure");
            }

            transport.BroadcastBatches.Add((target, batch));
            return Task.CompletedTask;
        };

        var ns = new FakeNamespace(local) { ReturnRepairChain = true };
        ns.Options.MaxCoalescingDelay = TimeSpan.FromMinutes(1);
        DisseminationOptions? optionsRef = null;
        var protocol = CreateProtocol(transport, ns, options =>
        {
            optionsRef = options;
            options.MaxBatchItems = 10;
        });

        Assert.True(await PublishValue(protocol, ns, ns.CreateValue(FakeNamespace.DefaultKey, sequence: 1), CancellationToken.None));
        Assert.True(await PublishValue(protocol, ns, ns.CreateValue(FakeNamespace.DefaultKey, sequence: 2, fromVersion: 1), CancellationToken.None));
        Assert.True(await PublishValue(protocol, ns, ns.CreateValue(FakeNamespace.DefaultKey, sequence: 3, fromVersion: 2), CancellationToken.None));
        optionsRef!.MaxBatchItems = 1;
        await protocol.FlushPendingBroadcast(CancellationToken.None);

        Assert.Equal(new long[] { 1, 2 }, attempts);

        Assert.True(await PublishValue(protocol, ns, ns.CreateValue(FakeNamespace.DefaultKey, sequence: 4, fromVersion: 3), CancellationToken.None));
        await protocol.FlushPendingBroadcast(CancellationToken.None);

        Assert.Equal(new long[] { 1, 2, 2, 3, 4 }, attempts);
    }

    [Fact]
    public async Task BroadcastPeerFailureDoesNotBlockOtherPeerPumps()
    {
        var local = CreateSilo(11111);
        var failedPeer = CreateSilo(11112);
        var healthyPeer = CreateSilo(11113);
        var transport = new FakeTransport(local, failedPeer, healthyPeer);
        var timeProvider = new FakeTimeProvider();
        var failedPeerAttempts = 0;
        transport.SendBroadcastHandler = (target, batch, cancellationToken) =>
        {
            if (Equals(target, failedPeer) && Interlocked.Increment(ref failedPeerAttempts) == 1)
            {
                throw new InvalidOperationException("transient peer failure");
            }

            lock (transport.BroadcastBatches)
            {
                transport.BroadcastBatches.Add((target, batch));
            }

            return Task.CompletedTask;
        };

        var ns = new FakeNamespace(local);
        var protocol = CreateProtocol(transport, ns, options =>
        {
            options.Overlay.FanOutFactor = static _ => 10;
        }, timeProvider);

        Assert.True(await PublishValue(protocol, ns, ns.CreateValue("first", sequence: 1), CancellationToken.None));
        await protocol.FlushPendingBroadcast(CancellationToken.None);
        Assert.Equal(1, failedPeerAttempts);
        Assert.Equal(new[] { healthyPeer }, GetSentBroadcastPeers(transport));

        ClearBroadcastBatches(transport);
        Assert.True(await PublishValue(protocol, ns, ns.CreateValue("second", sequence: 2), CancellationToken.None));
        await protocol.FlushPendingBroadcast(CancellationToken.None);
        Assert.Equal(2, failedPeerAttempts);
        Assert.Equal(new[] { failedPeer, healthyPeer }.OrderBy(static peer => peer), GetSentBroadcastPeers(transport).OrderBy(static peer => peer));
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

        Assert.True(await PublishValue(protocol, ns, ns.CreateValue("before-removal", sequence: 1), CancellationToken.None));

        transport.Peers.Remove(peer);
        Assert.True(await PublishValue(protocol, ns, ns.CreateValue("during-removal", sequence: 2), CancellationToken.None));
        await protocol.FlushPendingBroadcast(CancellationToken.None);

        Assert.Empty(transport.BroadcastBatches);
    }

    [Fact]
    public async Task StopDrainsPendingBroadcastPumps()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var ns = new FakeNamespace(local);
        ns.Options.MaxCoalescingDelay = TimeSpan.FromMinutes(1);
        var protocol = CreateProtocol(transport, ns, options => options.Overlay.FanOutFactor = static _ => 1);

        Assert.True(await PublishValue(protocol, ns, ns.CreateValue("before-stop", sequence: 1), CancellationToken.None));
        await protocol.StopAsync(CancellationToken.None);

        var batch = Assert.Single(transport.BroadcastBatches);
        Assert.Equal(peer, batch.Peer);
        Assert.Equal(new DisseminationKey("before-stop"), Assert.Single(GetBroadcastValues(batch.Batch)).Value.Key);
    }

    [Fact]
    public async Task FlushPendingBroadcastWaitsForInFlightFlush()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var timeProvider = new FakeTimeProvider();
        var sendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.SendBroadcastHandler = async (target, batch, cancellationToken) =>
        {
            sendStarted.TrySetResult();
            await releaseSend.Task.WaitAsync(cancellationToken);
            transport.BroadcastBatches.Add((target, batch));
        };

        var ns = new FakeNamespace(local);
        ns.Options.MaxCoalescingDelay = TimeSpan.FromSeconds(1);
        var protocol = CreateProtocol(transport, ns, options => options.Overlay.FanOutFactor = static _ => 1, timeProvider);

        Assert.True(await PublishValue(protocol, ns, ns.CreateValue("in-flight", sequence: 1), CancellationToken.None));
        timeProvider.Advance(ns.Options.MaxCoalescingDelay);
        await sendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var flushTask = protocol.FlushPendingBroadcast(CancellationToken.None);
        try
        {
            Assert.False(flushTask.IsCompleted);
        }
        finally
        {
            releaseSend.TrySetResult();
        }

        await flushTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(transport.BroadcastBatches);
        await protocol.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task NotificationDuringInFlightSendRepairsFromAcknowledgedVersion()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var timeProvider = new FakeTimeProvider();
        var firstSendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendCount = 0;
        transport.SendBroadcastResponseHandler = async (target, batch, cancellationToken) =>
        {
            transport.BroadcastBatches.Add((target, batch));
            if (Interlocked.Increment(ref sendCount) == 1)
            {
                firstSendStarted.TrySetResult();
                await releaseFirstSend.Task.WaitAsync(cancellationToken);
            }

            return FakeTransport.CreateAcknowledgment(batch);
        };

        var ns = new FakeNamespace(local) { ReturnRepairChain = true };
        ns.Options.MaxCoalescingDelay = TimeSpan.FromSeconds(1);
        var protocol = CreateProtocol(transport, ns, timeProvider: timeProvider);

        Assert.True(await PublishValue(
            protocol,
            ns,
            ns.CreateValue(FakeNamespace.DefaultKey, sequence: 1),
            CancellationToken.None));
        timeProvider.Advance(ns.Options.MaxCoalescingDelay);
        await firstSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(await PublishValue(
            protocol,
            ns,
            ns.CreateValue(FakeNamespace.DefaultKey, sequence: 2, fromVersion: 1),
            CancellationToken.None));
        releaseFirstSend.TrySetResult();
        await protocol.FlushPendingBroadcast(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(
            new[] { (From: 0L, To: 1L), (From: 1L, To: 2L) },
            transport.BroadcastBatches.Select(batch =>
            {
                var value = Assert.Single(GetBroadcastValues(batch.Batch));
                return (value.Value.FromVersion, value.Value.ToVersion);
            }));
        await protocol.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task MembershipRefreshCompletesFlushWaitersForRemovedPeers()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var timeProvider = new FakeTimeProvider();
        var sendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.SendBroadcastHandler = async (target, batch, cancellationToken) =>
        {
            sendStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        };

        var ns = new FakeNamespace(local);
        ns.Options.MaxCoalescingDelay = TimeSpan.FromSeconds(1);
        var protocol = CreateProtocol(transport, ns, options => options.Overlay.FanOutFactor = static _ => 1, timeProvider);

        Assert.True(await PublishValue(protocol, ns, ns.CreateValue("in-flight", sequence: 1), CancellationToken.None));
        timeProvider.Advance(ns.Options.MaxCoalescingDelay);
        await sendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await PublishValue(protocol, ns, ns.CreateValue("pending", sequence: 1), CancellationToken.None));
        var flushTask = protocol.FlushPendingBroadcast(CancellationToken.None);
        Assert.False(flushTask.IsCompleted);

        transport.Peers.Remove(peer);
        Assert.True(await PublishValue(protocol, ns, ns.CreateValue("after-removal", sequence: 1), CancellationToken.None));

        await flushTask.WaitAsync(TimeSpan.FromSeconds(5));
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
        Assert.Equal(expectedChildren.OrderBy(static peer => peer), transport.BroadcastBatches.Select(batch => batch.Peer).OrderBy(static peer => peer));
    }

    [Fact]
    public async Task ReceiveBroadcastAppliesAllValuesBeforeForwarding()
    {
        var root = CreateSilo(11111);
        var local = CreateSilo(11112);
        var sender = CreateSilo(11113);
        var peer = CreateSilo(11114);
        var transport = new FakeTransport(local, sender, peer);
        DisseminationKey firstKey = new("first");
        DisseminationKey secondKey = new("second");
        var ns = new FakeNamespace(local);
        var forwardingObserved = false;
        transport.SendBroadcastHandler = (target, batch, cancellationToken) =>
        {
            forwardingObserved = true;
            Assert.Equal(1, ns.GetVersion(firstKey));
            Assert.Equal(2, ns.GetVersion(secondKey));
            transport.BroadcastBatches.Add((target, batch));
            return Task.CompletedTask;
        };

        var protocol = CreateProtocol(transport, ns, options => options.Overlay.FanOutFactor = static _ => 2);
        var first = ns.CreateItem(root, firstKey, sequence: 1);
        var second = ns.CreateItem(root, secondKey, sequence: 2);

        await protocol.ReceiveBroadcast(CreateBroadcastBatch(sender, first, second), CancellationToken.None);
        await protocol.FlushPendingBroadcast(CancellationToken.None);

        Assert.True(forwardingObserved);
        Assert.Equal(1, ns.GetVersion(firstKey));
        Assert.Equal(2, ns.GetVersion(secondKey));
    }

    [Fact]
    public async Task ReceiveBroadcastContinuesAfterFailedValue()
    {
        var root = CreateSilo(11111);
        var local = CreateSilo(11112);
        var child = CreateSilo(11113);
        var transport = new FakeTransport(local, root, child);
        var ns = new FakeNamespace(local);
        var protocol = CreateProtocol(transport, ns, options => options.Overlay.FanOutFactor = static _ => 1);
        DisseminationKey firstKey = new("first");
        DisseminationKey failedKey = new("failed");
        DisseminationKey secondKey = new("second");
        var first = ns.CreateItem(root, firstKey, sequence: 1);
        var failed = CreateDisseminationValue(
            root,
            new DisseminationValue(failedKey, fromVersion: 0, toVersion: 1, Array.Empty<byte>()));
        var second = ns.CreateItem(root, secondKey, sequence: 1);

        await protocol.ReceiveBroadcast(CreateBroadcastBatch(root, first, failed, second), CancellationToken.None);
        await protocol.FlushPendingBroadcast(CancellationToken.None);

        Assert.Equal(1, ns.GetVersion(firstKey));
        Assert.Equal(0, ns.GetVersion(failedKey));
        Assert.Equal(1, ns.GetVersion(secondKey));
        var forwarded = Assert.Single(transport.BroadcastBatches);
        Assert.Equal(child, forwarded.Peer);
        Assert.Equal(
            new[] { firstKey, secondKey }.OrderBy(static key => key),
            GetBroadcastValues(forwarded.Batch).Select(static item => item.Value.Key).OrderBy(static key => key));
    }

    [Fact]
    public async Task ReceiveBroadcastDoesNotRefreshMembershipForRemovedOriginators()
    {
        var local = CreateSilo(11111);
        var sender = CreateSilo(11112);
        var firstOriginator = CreateSilo(11120);
        var secondOriginator = CreateSilo(11121);
        var transport = new FakeTransport(local, sender);
        var ns = new FakeNamespace(local);
        var protocol = CreateProtocol(transport, ns);
        var first = ns.CreateItem(firstOriginator, "first", sequence: 1);
        var second = ns.CreateItem(secondOriginator, "second", sequence: 1);

        await protocol.ReceiveBroadcast(CreateBroadcastBatch(sender, first, second), CancellationToken.None);

        Assert.Equal(0, transport.RefreshMembershipCallCount);
        Assert.Equal(1, ns.GetVersion("first"));
        Assert.Equal(1, ns.GetVersion("second"));
        Assert.Empty(transport.BroadcastBatches);
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
    public async Task DuplicateBroadcastSchedulesForwardingWhenNoPriorQueueStateExists()
    {
        var silos = Enumerable.Range(11111, 8).Select(CreateSilo).OrderBy(static silo => silo).ToArray();
        var sender = silos[0];
        var local = silos[1];
        var peers = silos.Where(silo => !Equals(silo, local)).ToArray();
        var transport = new FakeTransport(local, peers);
        var ns = new FakeNamespace(local);
        ns.SetValue(FakeNamespace.DefaultKey, version: 1);
        var protocol = CreateProtocol(transport, ns, options => options.Overlay.FanOutFactor = static _ => 2);
        var item = ns.CreateItem(sender, FakeNamespace.DefaultKey, sequence: 1);

        await protocol.ReceiveBroadcast(
            CreateBroadcastBatch(sender, item),
            CancellationToken.None);
        await protocol.FlushPendingBroadcast(CancellationToken.None);

        var expectedChildren = GetForwardingTreeTargets(local, sender, peers, fanout: 2, sender: sender);
        Assert.Equal(
            expectedChildren.OrderBy(static peer => peer),
            transport.BroadcastBatches.Select(static batch => batch.Peer).OrderBy(static peer => peer));
        Assert.False(ns.ApplyCounts.ContainsKey(FakeNamespace.DefaultKey));
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
    public async Task ReceiveBroadcastAcknowledgesCurrentVersionAfterRejectedRepair()
    {
        var sender = CreateSilo(11111);
        var local = CreateSilo(11112);
        var transport = new FakeTransport(local, sender);
        var ns = new FakeNamespace(local);
        var protocol = CreateProtocol(transport, ns);
        ns.SetValue(FakeNamespace.DefaultKey, version: 1);
        var item = ns.CreateItem(
            sender,
            FakeNamespace.DefaultKey,
            sequence: 3,
            fromVersion: 2);

        var response = await protocol.ReceiveBroadcast(
            CreateBroadcastBatch(sender, item),
            CancellationToken.None);

        var acknowledgment = Assert.Single(response.Acknowledgments[ns.Name]);
        Assert.Equal(FakeNamespace.DefaultKey, acknowledgment.Key);
        Assert.Equal(1, acknowledgment.Version);
    }

    [Fact]
    public async Task ReceiveBroadcastDoesNotRefreshMembershipForMissingRoot()
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
        Assert.Equal(0, transport.RefreshMembershipCallCount);
        Assert.Equal(new[] { peer }, transport.BroadcastBatches.Select(static batch => batch.Peer));
    }

    [Fact]
    public async Task ReceiveBroadcastRoutesWithoutRefreshingForMissingRoot()
    {
        var local = CreateSilo(11111);
        var sender = CreateSilo(11112);
        var peer = CreateSilo(11113);
        var root = CreateSilo(11120);
        var transport = new FakeTransport(local, sender, peer);
        var ns = new FakeNamespace(local);
        var protocol = CreateProtocol(transport, ns, options => options.Overlay.FanOutFactor = static _ => 2);
        var item = ns.CreateItem(root, FakeNamespace.DefaultKey, sequence: 1);

        await protocol.ReceiveBroadcast(CreateBroadcastBatch(sender, item), CancellationToken.None);
        await protocol.FlushPendingBroadcast(CancellationToken.None);

        Assert.Equal(1, ns.GetVersion(FakeNamespace.DefaultKey));
        Assert.Equal(0, transport.RefreshMembershipCallCount);
        Assert.Equal(
            GetForwardingTreeTargets(local, sender, new[] { sender, peer }, fanout: 2, sender: sender),
            transport.BroadcastBatches.Select(static batch => batch.Peer));
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

        var initialResult = await PublishValue(protocol, ns, item, CancellationToken.None);
        await protocol.FlushPendingBroadcast(CancellationToken.None);
        var initialChildren = GetOriginatorTreeTargets(local, transport.Peers, fanout: 2);

        foreach (var peer in Enumerable.Range(11116, 8).Select(CreateSilo))
        {
            transport.Peers.Add(peer);
            var updatedChildren = GetOriginatorTreeTargets(local, transport.Peers, fanout: 2);
            if (!initialChildren.SequenceEqual(updatedChildren))
            {
                transport.BroadcastBatches.Clear();
                var updatedItem = ns.CreateValue(FakeNamespace.DefaultKey, sequence: 2);

                var updatedResult = await PublishValue(protocol, ns, updatedItem, CancellationToken.None);
                await protocol.FlushPendingBroadcast(CancellationToken.None);

                Assert.True(initialResult);
                Assert.True(updatedResult);
                Assert.Equal(updatedChildren.OrderBy(static peer => peer), transport.BroadcastBatches.Select(batch => batch.Peer).OrderBy(static peer => peer));
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

        var first = await PublishValue(protocol, ns, ns.CreateValue(FakeNamespace.DefaultKey, sequence: 1), CancellationToken.None);
        var second = await PublishValue(protocol, ns, ns.CreateValue(FakeNamespace.DefaultKey, sequence: 2), CancellationToken.None);
        await protocol.FlushPendingBroadcast(CancellationToken.None);

        Assert.True(first);
        Assert.True(second);
        var batch = Assert.Single(transport.BroadcastBatches);
        Assert.Equal(peer, batch.Peer);
        var value = Assert.Single(GetBroadcastValues(batch.Batch));
        Assert.Equal(2, value.Value.ToVersion);
    }

    [Fact]
    public async Task BroadcastRepairsFromAcknowledgedPeerVersion()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var ns = new FakeNamespace(local) { ReturnRepairChain = true };
        var protocol = CreateProtocol(transport, ns);

        Assert.True(await PublishValue(
            protocol,
            ns,
            ns.CreateValue(FakeNamespace.DefaultKey, sequence: 1),
            CancellationToken.None));
        await protocol.FlushPendingBroadcast(CancellationToken.None);
        Assert.True(await PublishValue(
            protocol,
            ns,
            ns.CreateValue(FakeNamespace.DefaultKey, sequence: 2, fromVersion: 1),
            CancellationToken.None));
        await protocol.FlushPendingBroadcast(CancellationToken.None);

        Assert.Equal(
            new[] { (From: 0L, To: 1L), (From: 1L, To: 2L) },
            transport.BroadcastBatches.Select(batch =>
            {
                var value = Assert.Single(GetBroadcastValues(batch.Batch));
                return (value.Value.FromVersion, value.Value.ToVersion);
            }));
    }

    [Fact]
    public async Task BroadcastBatchingPreservesOrderedSameKeyValues()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var ns = new FakeNamespace(local) { ReturnRepairChain = true };
        var protocol = CreateProtocol(transport, ns);

        Assert.True(await PublishValue(protocol, ns, ns.CreateValue(FakeNamespace.DefaultKey, sequence: 1), CancellationToken.None));
        Assert.True(await PublishValue(protocol, ns, ns.CreateValue(FakeNamespace.DefaultKey, sequence: 2, fromVersion: 1), CancellationToken.None));
        Assert.True(await PublishValue(protocol, ns, ns.CreateValue(FakeNamespace.DefaultKey, sequence: 3, fromVersion: 2), CancellationToken.None));
        await protocol.FlushPendingBroadcast(CancellationToken.None);

        var batch = Assert.Single(transport.BroadcastBatches);
        Assert.Equal(
            new[] { (From: 0L, To: 1L), (From: 1L, To: 2L), (From: 2L, To: 3L) },
            GetBroadcastValues(batch.Batch).Select(static value => (value.Value.FromVersion, value.Value.ToVersion)));
    }

    [Fact]
    public async Task BroadcastBatchingLetsFullValueSupersedeOrderedValues()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var ns = new FakeNamespace(local) { ReturnRepairChain = true };
        var protocol = CreateProtocol(transport, ns);

        Assert.True(await PublishValue(protocol, ns, ns.CreateValue(FakeNamespace.DefaultKey, sequence: 2, fromVersion: 1), CancellationToken.None));
        Assert.True(await PublishValue(protocol, ns, ns.CreateValue(FakeNamespace.DefaultKey, sequence: 3, fromVersion: 2), CancellationToken.None));
        Assert.True(await PublishValue(protocol, ns, ns.CreateValue(FakeNamespace.DefaultKey, sequence: 4), CancellationToken.None));
        await protocol.FlushPendingBroadcast(CancellationToken.None);

        var value = Assert.Single(GetBroadcastValues(Assert.Single(transport.BroadcastBatches).Batch));
        Assert.Equal(0, value.Value.FromVersion);
        Assert.Equal(4, value.Value.ToVersion);
    }

    [Fact]
    public async Task BroadcastPeerSenderCachesSystemTarget()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var ns = new FakeNamespace(local);
        var protocol = CreateProtocol(transport, ns, options => options.Overlay.FanOutFactor = static _ => 1);

        Assert.True(await PublishValue(protocol, ns, ns.CreateValue("first", sequence: 1), CancellationToken.None));
        await protocol.FlushPendingBroadcast(CancellationToken.None);
        Assert.True(await PublishValue(protocol, ns, ns.CreateValue("second", sequence: 2), CancellationToken.None));
        await protocol.FlushPendingBroadcast(CancellationToken.None);

        Assert.Equal(2, transport.BroadcastBatches.Count);
        Assert.Equal(1, transport.GetTargetResolutionCount(peer));
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

        var first = await PublishValue(protocol, ns, ns.CreateValue("first", sequence: 1), CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        var second = await PublishValue(protocol, ns, ns.CreateValue("second", sequence: 1), CancellationToken.None);

        await sent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(first);
        Assert.True(second);
        var batch = Assert.Single(transport.BroadcastBatches);
        Assert.Equal(new DisseminationKey[] { "first", "second" }, GetBroadcastValues(batch.Batch).Select(static value => value.Value.Key));
    }

    [Fact]
    public async Task BroadcastBatchingUsesShortestConfiguredCoalescingDelay()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var timeProvider = new FakeTimeProvider();
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.SendBroadcastHandler = (target, batch, cancellationToken) =>
        {
            transport.BroadcastBatches.Add((target, batch));
            sent.TrySetResult();
            return Task.CompletedTask;
        };

        var slowNamespace = new FakeNamespace(local, "slow");
        slowNamespace.Options.MaxCoalescingDelay = TimeSpan.FromMinutes(1);
        var fastNamespace = new FakeNamespace(local, "fast");
        fastNamespace.Options.MaxCoalescingDelay = TimeSpan.FromSeconds(1);
        var protocol = CreateProtocol(
            transport,
            new IDisseminationNamespace[] { slowNamespace, fastNamespace },
            options => options.Overlay.FanOutFactor = static _ => 1,
            timeProvider);

        Assert.True(await PublishValue(protocol, slowNamespace, slowNamespace.CreateValue("slow", sequence: 1), CancellationToken.None));
        Assert.True(await PublishValue(protocol, fastNamespace, fastNamespace.CreateValue("fast", sequence: 1), CancellationToken.None));

        timeProvider.Advance(TimeSpan.FromMilliseconds(999));
        Assert.False(sent.Task.IsCompleted);
        timeProvider.Advance(TimeSpan.FromMilliseconds(1));
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var batch = Assert.Single(transport.BroadcastBatches);
        Assert.Equal(2, batch.Batch.Values.Count);
        await protocol.StopAsync(CancellationToken.None);
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
            var options = new DisseminationOverlayOptions
            {
                FanOutFactor = _ => testCase.Fanout,
            };
            var members = silos.ToImmutableArray();
            var snapshots = silos.ToDictionary(
                static silo => silo,
                silo => new DisseminationMembershipSnapshot(
                    new MembershipVersion(1),
                    silo,
                    members,
                    options));
            var directTargets = snapshots[root].OriginatorTreeTargets;

            Assert.DoesNotContain(root, directTargets);
            Assert.Equal(directTargets.Length, directTargets.Distinct().Count());
            Assert.True(directTargets.Length <= Math.Min((testCase.Fanout * 2), Math.Max(0, testCase.Count - 1)));

            var reached = GetReachedParticipants(root, snapshots);
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
            Sender = peer,
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
            Sender = peer,
            Digests = CreateAntiEntropyRequestDigest(
                ns.Name,
                ("requested", 3)),
        }, CancellationToken.None);

        var item = Assert.Single(GetAntiEntropyResponseValues(response));
        Assert.Equal(new DisseminationKey("requested"), item.Value.Key);
    }

    [Fact]
    public async Task OversizedAntiEntropyKeyDoesNotStarveLaterRepairs()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var ns = new FakeNamespace(local);
        ns.Options.MaxPayloadBytes = sizeof(long);
        ns.PublishValue(new DisseminationValue(
            "oversized",
            fromVersion: 0,
            toVersion: 1,
            new byte[sizeof(long) + 1]));
        ns.SetValue("valid", version: 2);
        var protocol = CreateProtocol(transport, ns);

        var response = await protocol.ReceiveAntiEntropy(new DisseminationAntiEntropyRequest
        {
            Sender = peer,
            Digests = CreateAntiEntropyRequestDigest(
                ns.Name,
                ("oversized", 0),
                ("valid", 0)),
        }, CancellationToken.None);

        var value = Assert.Single(GetAntiEntropyResponseValues(response));
        Assert.Equal(new DisseminationKey("valid"), value.Value.Key);
        Assert.False(response.Truncated);
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
    public async Task DuplicateBroadcastDoesNotPostponeAntiEntropyProbe()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var ns = new FakeNamespace(local);
        var timeProvider = new TestTimeProvider();
        ns.Options.ExpectedUpdateCadence = TimeSpan.FromSeconds(2);
        var protocol = CreateProtocol(transport, ns, timeProvider: timeProvider);
        var batch = CreateBroadcastBatch(peer, ns.CreateItem(peer, FakeNamespace.DefaultKey, sequence: 1));

        await protocol.ReceiveBroadcast(batch, CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromSeconds(2) - TimeSpan.FromMilliseconds(1));
        await protocol.ReceiveBroadcast(batch, CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromMilliseconds(1));

        await protocol.RunAntiEntropyRound(CancellationToken.None);

        var request = Assert.Single(transport.AntiEntropyRequests).Request;
        Assert.Single(request.Digests[ns.Name]);
    }

    [Fact]
    public async Task AntiEntropySendsOneDigestRequestPerPeer()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        DisseminationKey firstKey = new("first");
        DisseminationKey secondKey = new("second");
        var firstNamespace = new FakeNamespace(local, new DisseminationNamespace("first-namespace"));
        firstNamespace.ExpectedKeys.Add(firstKey);
        var secondNamespace = new FakeNamespace(local, new DisseminationNamespace("second-namespace"));
        secondNamespace.ExpectedKeys.Add(secondKey);
        var protocol = CreateProtocol(transport, new IDisseminationNamespace[] { firstNamespace, secondNamespace }, options =>
        {
            options.Overlay.AntiEntropyPeerCount = 1;
        });

        await protocol.RunAntiEntropyRound(CancellationToken.None);

        var request = Assert.Single(transport.AntiEntropyRequests).Request;
        Assert.Equal(
            new[] { firstNamespace.Name, secondNamespace.Name }.OrderBy(static name => name),
            request.Digests.Keys.OrderBy(static name => name));
        var firstDigest = Assert.Single(request.Digests[firstNamespace.Name]);
        Assert.Equal(firstKey, firstDigest.Key);
        Assert.Equal(0, firstDigest.Version);
        var secondDigest = Assert.Single(request.Digests[secondNamespace.Name]);
        Assert.Equal(secondKey, secondDigest.Key);
        Assert.Equal(0, secondDigest.Version);
    }

    [Fact]
    public async Task AntiEntropyExchangeFailureDoesNotBackOffPeer()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var ns = new FakeNamespace(local);
        ns.ExpectedKeys.Add(FakeNamespace.DefaultKey);
        var exchangeCount = 0;
        transport.ExchangeAntiEntropyHandler = (target, request, _) =>
        {
            if (Interlocked.Increment(ref exchangeCount) == 1)
            {
                throw new InvalidOperationException("transient anti-entropy failure");
            }

            return ValueTask.FromResult(new DisseminationAntiEntropyResponse { Sender = target });
        };

        var protocol = CreateProtocol(transport, ns, options =>
        {
            options.Overlay.AntiEntropyPeerCount = 1;
        });

        await protocol.RunAntiEntropyRound(CancellationToken.None);
        await protocol.RunAntiEntropyRound(CancellationToken.None);

        Assert.Equal(2, Volatile.Read(ref exchangeCount));
        Assert.Equal(2, transport.AntiEntropyRequests.Count);
    }

    [Fact]
    public async Task AntiEntropyExchangePropagatesCancellation()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var ns = new FakeNamespace(local);
        ns.ExpectedKeys.Add(FakeNamespace.DefaultKey);
        var exchangeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        transport.ExchangeAntiEntropyHandler = async (target, request, cancellationToken) =>
        {
            exchangeStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new DisseminationAntiEntropyResponse { Sender = target };
        };

        var protocol = CreateProtocol(transport, ns, options => options.Overlay.AntiEntropyPeerCount = 1);
        var exchangeTask = protocol.RunAntiEntropyRound(cancellation.Token);
        await exchangeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await exchangeTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task AntiEntropyResponseHonorsCancellation()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var ns = new FakeNamespace(local);
        var protocol = CreateProtocol(transport, ns);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await protocol.ReceiveAntiEntropy(
                new DisseminationAntiEntropyRequest { Sender = peer },
                cancellation.Token));
    }

    [Fact]
    public async Task AntiEntropyExchangesAreNotLimitedByMaxConcurrentSends()
    {
        var local = CreateSilo(11111);
        var peers = Enumerable.Range(11112, 3).Select(CreateSilo).ToArray();
        var transport = new FakeTransport(local, peers);
        var ns = new FakeNamespace(local);
        ns.ExpectedKeys.Add(FakeNamespace.DefaultKey);
        var gate = new object();
        var allStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseExchanges = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = 0;
        var started = 0;
        var observedMax = 0;
        transport.ExchangeAntiEntropyHandler = async (target, request, _) =>
        {
            lock (gate)
            {
                inFlight++;
                started++;
                observedMax = Math.Max(observedMax, inFlight);
                if (started == peers.Length)
                {
                    allStarted.TrySetResult(true);
                }
            }

            try
            {
                await releaseExchanges.Task;
                return new DisseminationAntiEntropyResponse { Sender = target };
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
            options.MaxConcurrentSends = 1;
            options.Overlay.AntiEntropyPeerCount = peers.Length;
        });

        var exchangeTask = protocol.RunAntiEntropyRound(CancellationToken.None);
        try
        {
            await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            lock (gate)
            {
                Assert.Equal(peers.Length, observedMax);
            }
        }
        finally
        {
            releaseExchanges.TrySetResult(true);
        }

        await exchangeTask;

        Assert.Equal(peers.Length, transport.AntiEntropyRequests.Count);
    }

    [Fact]
    public async Task AntiEntropyAppliesCompletedResponsesWhenSlowPeerExceedsRoundLifetime()
    {
        var local = CreateSilo(11111);
        var fastPeer = CreateSilo(11112);
        var slowPeer = CreateSilo(11113);
        var transport = new FakeTransport(local, fastPeer, slowPeer);
        var timeProvider = new FakeTimeProvider();
        var ns = new FakeNamespace(local);
        ns.Options.StaleItemTtl = TimeSpan.FromSeconds(1);
        ns.ExpectedKeys.Add(FakeNamespace.DefaultKey);
        ns.ApplyObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var fastResponseReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowExchangeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowCancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repair = ns.CreateItem(fastPeer, FakeNamespace.DefaultKey, sequence: 1);
        transport.ExchangeAntiEntropyHandler = async (target, _, cancellationToken) =>
        {
            if (target.Equals(slowPeer))
            {
                slowExchangeStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                finally
                {
                    slowCancellationObserved.TrySetResult();
                }
            }

            fastResponseReturned.TrySetResult();
            return new DisseminationAntiEntropyResponse
            {
                Sender = target,
                Values = CreateValueGroups(repair),
            };
        };
        var protocol = CreateProtocol(
            transport,
            ns,
            options => options.Overlay.AntiEntropyPeerCount = 2,
            timeProvider);

        var round = protocol.RunAntiEntropyRound(CancellationToken.None);
        await Task.WhenAll(fastResponseReturned.Task, slowExchangeStarted.Task)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, ns.GetVersion(FakeNamespace.DefaultKey));

        timeProvider.Advance(ns.Options.StaleItemTtl);
        await slowCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await round.WaitAsync(TimeSpan.FromSeconds(5));
        await ns.ApplyObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, ns.GetVersion(FakeNamespace.DefaultKey));
    }

    [Fact]
    public async Task AntiEntropyRanksStaggeredResponsesWhichCompleteWithinRoundLifetime()
    {
        var local = CreateSilo(11111);
        var fastPeer = CreateSilo(11112);
        var slowerPeer = CreateSilo(11113);
        var transport = new FakeTransport(local, fastPeer, slowerPeer);
        var ns = new FakeNamespace(local);
        ns.SetValue(FakeNamespace.DefaultKey, version: 1);
        var fastResponseReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowExchangeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSlowResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.ExchangeAntiEntropyHandler = async (target, request, cancellationToken) =>
        {
            var requestedVersion = Assert.Single(request.Digests[ns.Name]).Version;
            var responseVersion = target.Equals(fastPeer) ? 2 : 3;
            if (target.Equals(slowerPeer))
            {
                slowExchangeStarted.TrySetResult();
                await releaseSlowResponse.Task.WaitAsync(cancellationToken);
            }
            else
            {
                fastResponseReturned.TrySetResult();
            }

            return new DisseminationAntiEntropyResponse
            {
                Sender = target,
                Values = CreateValueGroups(ns.CreateItem(
                    target,
                    FakeNamespace.DefaultKey,
                    sequence: responseVersion,
                    fromVersion: requestedVersion)),
            };
        };
        var protocol = CreateProtocol(
            transport,
            ns,
            options => options.Overlay.AntiEntropyPeerCount = 2);

        var round = protocol.RunAntiEntropyRound(CancellationToken.None);
        await Task.WhenAll(fastResponseReturned.Task, slowExchangeStarted.Task)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, ns.GetVersion(FakeNamespace.DefaultKey));

        releaseSlowResponse.TrySetResult();
        await round.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(3, ns.GetVersion(FakeNamespace.DefaultKey));
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
        transport.ExchangeAntiEntropyHandler = (target, request, _) => ValueTask.FromResult(new DisseminationAntiEntropyResponse
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
    public async Task AntiEntropyAppliesRepairChainInOrder()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var ns = new FakeNamespace(local);
        ns.SetValue(FakeNamespace.DefaultKey, version: 1);
        var protocol = CreateProtocol(transport, ns);
        var second = ns.CreateItem(
            peer,
            FakeNamespace.DefaultKey,
            sequence: 2,
            fromVersion: 1);
        var third = ns.CreateItem(
            peer,
            FakeNamespace.DefaultKey,
            sequence: 3,
            fromVersion: 2);
        transport.ExchangeAntiEntropyHandler = (target, request, _) => ValueTask.FromResult(new DisseminationAntiEntropyResponse
        {
            Sender = target,
            Values = CreateValueGroups(second, third),
        });

        await protocol.RunAntiEntropyRound(CancellationToken.None);

        Assert.Equal(3, ns.GetVersion(FakeNamespace.DefaultKey));
        Assert.Equal(2, ns.ApplyCounts[FakeNamespace.DefaultKey]);
        Assert.Empty(transport.BroadcastBatches);
    }

    [Fact]
    public async Task AntiEntropyAppliesNewestRepairFirstForEachStream()
    {
        var local = CreateSilo(11111);
        var peers = new[] { CreateSilo(11112), CreateSilo(11113) };
        var transport = new FakeTransport(local, peers);
        var ns = new FakeNamespace(local);
        ns.SetValue(FakeNamespace.DefaultKey, version: 1);
        var exchangeCount = 0;
        transport.ExchangeAntiEntropyHandler = (target, request, _) =>
        {
            var requestedVersion = Assert.Single(request.Digests[ns.Name]).Version;
            var responseVersion = Interlocked.Increment(ref exchangeCount) == 1 ? 2 : 3;
            var repair = ns.CreateItem(
                target,
                FakeNamespace.DefaultKey,
                sequence: responseVersion,
                fromVersion: requestedVersion);
            return ValueTask.FromResult(new DisseminationAntiEntropyResponse
            {
                Sender = target,
                Values = CreateValueGroups(repair),
            });
        };

        var protocol = CreateProtocol(
            transport,
            ns,
            options => options.Overlay.AntiEntropyPeerCount = peers.Length);

        await protocol.RunAntiEntropyRound(CancellationToken.None);

        Assert.Equal(3, ns.GetVersion(FakeNamespace.DefaultKey));
        Assert.Equal(peers.Length, exchangeCount);
    }

    [Fact]
    public async Task AntiEntropyResponseOmitsNamespacesWithoutRepairs()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var ns = new FakeNamespace(local);
        ns.SetValue(FakeNamespace.DefaultKey, version: 5);
        var protocol = CreateProtocol(transport, ns);

        var response = await protocol.ReceiveAntiEntropy(new DisseminationAntiEntropyRequest
        {
            Sender = peer,
            Digests = CreateAntiEntropyRequestDigest(
                ns.Name,
                (FakeNamespace.DefaultKey, 5)),
        }, CancellationToken.None);

        Assert.Empty(response.Values);
    }

    [Fact]
    public async Task AntiEntropyTruncationRotatesPastContinuouslyAdvancingKey()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var ns = new FakeNamespace(local);
        DisseminationKey hotKey = "hot";
        DisseminationKey waitingKey = "waiting";
        ns.SetValue(hotKey, version: 1);
        ns.SetValue(waitingKey, version: 1);
        var protocol = CreateProtocol(
            transport,
            ns,
            options => options.MaxBatchItems = 1);
        var request = new DisseminationAntiEntropyRequest
        {
            Sender = peer,
            Digests = CreateAntiEntropyRequestDigest(
                ns.Name,
                (hotKey, 0),
                (waitingKey, 0)),
        };

        var first = await protocol.ReceiveAntiEntropy(request, CancellationToken.None);
        ns.SetValue(hotKey, version: 2);
        var second = await protocol.ReceiveAntiEntropy(request, CancellationToken.None);

        Assert.True(first.Truncated);
        Assert.True(second.Truncated);
        Assert.Equal(hotKey, Assert.Single(GetAntiEntropyResponseValues(first)).Value.Key);
        Assert.Equal(waitingKey, Assert.Single(GetAntiEntropyResponseValues(second)).Value.Key);
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
            Value = new DisseminationValue("bad", fromVersion: 0, toVersion: 1, Array.Empty<byte>()),
            TimeToLive = TimeSpan.FromMinutes(1),
        };
        var goodRepairItem = ns.CreateItem(peer, FakeNamespace.DefaultKey, sequence: 3);
        transport.ExchangeAntiEntropyHandler = (target, request, _) => ValueTask.FromResult(new DisseminationAntiEntropyResponse
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
            TimeToLive = TimeSpan.FromMinutes(1),
        };
        var goodRepairItem = ns.CreateItem(peer, FakeNamespace.DefaultKey, sequence: 3);
        var exchangeCount = 0;
        transport.ExchangeAntiEntropyHandler = (target, request, _) =>
        {
            var count = Interlocked.Increment(ref exchangeCount);
            return ValueTask.FromResult(new DisseminationAntiEntropyResponse
            {
                Sender = target,
                Values = count switch
                {
                    1 => CreateValueGroups(badRepairItem),
                    2 => CreateValueGroups(goodRepairItem),
                    _ => [],
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
        var peerDigest = Assert.Single(sourceNamespace.Digests);
        sourceManager.CurrentSnapshot = updatedSnapshot;
        var localDigest = Assert.Single(sourceNamespace.Digests);

        var repair = sourceNamespace.CreateRepair(new DisseminationRepairRequest(
            localDigest.Key,
            peerDigest.Version,
            toVersion: null,
            maxItemCount: 1,
            maxBatchBytes: 1024 * 1024,
            maxPayloadBytes: 1024 * 1024));
        Assert.Equal(DisseminationRepairStatus.Produced, repair.Status);
        var value = Assert.Single(repair.Values);
        Assert.Equal(peerDigest.Version, value.FromVersion);
        Assert.Equal(localDigest.Version, value.ToVersion);
        var update = Assert.IsType<MembershipTableSnapshotUpdate>(
            serializer.Deserialize<MembershipTableSnapshotUpdate>(value.Payload));
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
    public async Task MembershipNamespaceAppliesSameVersionDiffWhenLivenessAdvances()
    {
        var local = CreateSilo(11111);
        var receiverSnapshot = CreateMembershipSnapshot(
            version: 2,
            CreateMembershipEntry(
                local,
                SiloStatus.Active,
                DateTime.UnixEpoch,
                iAmAliveTime: DateTime.UnixEpoch.AddSeconds(2)));
        var updatedEntry = CreateMembershipEntry(
            local,
            SiloStatus.Active,
            DateTime.UnixEpoch,
            iAmAliveTime: DateTime.UnixEpoch.AddSeconds(3));
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var update = new MembershipTableSnapshotUpdate
        {
            Diff = new MembershipTableSnapshotDiff(
                new MembershipVersion(1),
                new MembershipVersion(2),
                [updatedEntry],
                []),
        };
        var value = new DisseminationValue(
            DisseminationKey.Default,
            fromVersion: 1,
            toVersion: 2,
            serializer.SerializeToArray(update));
        var receiverManager = new FakeMembershipManager(receiverSnapshot);
        var receiverNamespace = CreateMembershipNamespace(receiverManager, serializer);

        var result = await receiverNamespace.ApplyValueAsync(value, CancellationToken.None);

        Assert.Equal(DisseminationApplyResult.Applied, result);
        Assert.Equal(
            DateTime.UnixEpoch.AddSeconds(3),
            receiverManager.CurrentSnapshot.Entries[local].IAmAliveTime);
    }

    [Fact]
    public void MembershipNamespaceInvalidatesCachedPayloadForSameVersionUpdate()
    {
        var local = CreateSilo(11111);
        var firstSnapshot = CreateMembershipSnapshot(
            version: 1,
            CreateMembershipEntry(
                local,
                SiloStatus.Active,
                DateTime.UnixEpoch,
                iAmAliveTime: DateTime.UnixEpoch.AddSeconds(1)));
        var updatedSnapshot = CreateMembershipSnapshot(
            version: 1,
            CreateMembershipEntry(
                local,
                SiloStatus.Active,
                DateTime.UnixEpoch,
                iAmAliveTime: DateTime.UnixEpoch.AddSeconds(2)));
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var manager = new FakeMembershipManager(firstSnapshot);
        var disseminationNamespace = CreateMembershipNamespace(manager, serializer);
        var request = new DisseminationRepairRequest(
            DisseminationKey.Default,
            fromVersion: null,
            toVersion: null,
            maxItemCount: 1,
            maxBatchBytes: 1024 * 1024,
            maxPayloadBytes: 1024 * 1024);

        var firstRepair = disseminationNamespace.CreateRepair(request);
        manager.CurrentSnapshot = updatedSnapshot;
        var updatedRepair = disseminationNamespace.CreateRepair(request);

        var firstValue = Assert.Single(firstRepair.Values);
        var updatedValue = Assert.Single(updatedRepair.Values);
        var firstUpdate = Assert.IsType<MembershipTableSnapshotUpdate>(
            serializer.Deserialize<MembershipTableSnapshotUpdate>(firstValue.Payload));
        var updatedUpdate = Assert.IsType<MembershipTableSnapshotUpdate>(
            serializer.Deserialize<MembershipTableSnapshotUpdate>(updatedValue.Payload));
        Assert.Equal(
            DateTime.UnixEpoch.AddSeconds(1),
            firstUpdate.Snapshot!.Entries[local].IAmAliveTime);
        Assert.Equal(
            DateTime.UnixEpoch.AddSeconds(2),
            updatedUpdate.Snapshot!.Entries[local].IAmAliveTime);
    }

    [Fact]
    public async Task MembershipNamespacePublishesSameVersionSuccessorAsFullSnapshot()
    {
        var local = CreateSilo(11111);
        var firstSnapshot = CreateMembershipSnapshot(
            version: 1,
            CreateMembershipEntry(
                local,
                SiloStatus.Active,
                DateTime.UnixEpoch,
                iAmAliveTime: DateTime.UnixEpoch.AddSeconds(1)));
        var updatedSnapshot = CreateMembershipSnapshot(
            version: 1,
            CreateMembershipEntry(
                local,
                SiloStatus.Active,
                DateTime.UnixEpoch,
                iAmAliveTime: DateTime.UnixEpoch.AddSeconds(2)));
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var sourceManager = new FakeMembershipManager(firstSnapshot);
        var sourceNamespace = CreateMembershipNamespace(sourceManager, serializer);
        var disseminationService = new FakeDisseminationService();

        Assert.True(await sourceNamespace.PublishAsync(
            disseminationService,
            firstSnapshot,
            CancellationToken.None));
        sourceManager.CurrentSnapshot = updatedSnapshot;
        Assert.True(await sourceNamespace.PublishAsync(
            disseminationService,
            updatedSnapshot,
            CancellationToken.None));

        var update = Assert.Single(disseminationService.Values.Skip(1));
        Assert.Equal((0L, 1L), (update.FromVersion, update.ToVersion));
        var receiverManager = new FakeMembershipManager(firstSnapshot);
        var receiverNamespace = CreateMembershipNamespace(receiverManager, serializer);
        Assert.Equal(
            DisseminationApplyResult.Applied,
            await receiverNamespace.ApplyValueAsync(update, CancellationToken.None));
        Assert.Equal(
            DateTime.UnixEpoch.AddSeconds(2),
            receiverManager.CurrentSnapshot.Entries[local].IAmAliveTime);
    }

    [Fact]
    public async Task MembershipAntiEntropyRepairsSameVersionSuccessor()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var firstSnapshot = CreateMembershipSnapshot(
            version: 1,
            CreateMembershipEntry(
                local,
                SiloStatus.Active,
                DateTime.UnixEpoch,
                iAmAliveTime: DateTime.UnixEpoch.AddSeconds(1)));
        var updatedSnapshot = CreateMembershipSnapshot(
            version: 1,
            CreateMembershipEntry(
                local,
                SiloStatus.Active,
                DateTime.UnixEpoch,
                iAmAliveTime: DateTime.UnixEpoch.AddSeconds(2)));
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var sourceNamespace = CreateMembershipNamespace(
            new FakeMembershipManager(updatedSnapshot),
            serializer);
        var receiverManager = new FakeMembershipManager(firstSnapshot);
        var receiverNamespace = CreateMembershipNamespace(receiverManager, serializer);
        var peerDigest = Assert.Single(receiverNamespace.Digests);
        var transport = new FakeTransport(local, peer);
        var protocol = CreateProtocol(
            transport,
            new IDisseminationNamespace[] { sourceNamespace });

        var response = await protocol.ReceiveAntiEntropy(new DisseminationAntiEntropyRequest
        {
            Sender = peer,
            Digests = new()
            {
                [sourceNamespace.Name] = [peerDigest],
            },
        }, CancellationToken.None);

        var value = Assert.Single(GetAntiEntropyResponseValues(response)).Value;
        Assert.Equal((0L, 1L), (value.FromVersion, value.ToVersion));
        Assert.Equal(
            DisseminationApplyResult.Applied,
            await receiverNamespace.ApplyValueAsync(value, CancellationToken.None));
        Assert.Equal(
            DateTime.UnixEpoch.AddSeconds(2),
            receiverManager.CurrentSnapshot.Entries[local].IAmAliveTime);
    }

    [Fact]
    public async Task MembershipNamespacePublishesOrderedDiffChain()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var firstSnapshot = CreateMembershipSnapshot(
            version: 1,
            CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch),
            CreateMembershipEntry(peer, SiloStatus.Active, DateTime.UnixEpoch.AddSeconds(1)));
        var secondSnapshot = CreateMembershipSnapshot(
            version: 2,
            CreateMembershipEntry(
                local,
                SiloStatus.Active,
                DateTime.UnixEpoch,
                iAmAliveTime: DateTime.UnixEpoch.AddSeconds(2)),
            CreateMembershipEntry(peer, SiloStatus.Active, DateTime.UnixEpoch.AddSeconds(1)));
        var thirdSnapshot = CreateMembershipSnapshot(
            version: 3,
            CreateMembershipEntry(
                local,
                SiloStatus.Active,
                DateTime.UnixEpoch,
                iAmAliveTime: DateTime.UnixEpoch.AddSeconds(2)),
            CreateMembershipEntry(
                peer,
                SiloStatus.Active,
                DateTime.UnixEpoch.AddSeconds(1),
                iAmAliveTime: DateTime.UnixEpoch.AddSeconds(3)));
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var sourceManager = new FakeMembershipManager(firstSnapshot);
        var sourceNamespace = CreateMembershipNamespace(sourceManager, serializer);
        var disseminationService = new FakeDisseminationService();

        Assert.True(await sourceNamespace.PublishAsync(disseminationService, firstSnapshot, CancellationToken.None));
        sourceManager.CurrentSnapshot = secondSnapshot;
        Assert.True(await sourceNamespace.PublishAsync(disseminationService, secondSnapshot, CancellationToken.None));
        sourceManager.CurrentSnapshot = thirdSnapshot;
        Assert.True(await sourceNamespace.PublishAsync(disseminationService, thirdSnapshot, CancellationToken.None));
        var (first, second, third) = (
            disseminationService.Values[0],
            disseminationService.Values[1],
            disseminationService.Values[2]);

        Assert.Equal((0L, 1L), (first.FromVersion, first.ToVersion));
        Assert.Equal((1L, 2L), (second.FromVersion, second.ToVersion));
        Assert.Equal((2L, 3L), (third.FromVersion, third.ToVersion));
        Assert.NotNull(Assert.IsType<MembershipTableSnapshotUpdate>(
            serializer.Deserialize<MembershipTableSnapshotUpdate>(first.Payload)).Snapshot);
        Assert.NotNull(Assert.IsType<MembershipTableSnapshotUpdate>(
            serializer.Deserialize<MembershipTableSnapshotUpdate>(second.Payload)).Diff);
        Assert.NotNull(Assert.IsType<MembershipTableSnapshotUpdate>(
            serializer.Deserialize<MembershipTableSnapshotUpdate>(third.Payload)).Diff);

        var receiverManager = new FakeMembershipManager(CreateMembershipSnapshot(MembershipVersion.MinValue.Value));
        var receiverNamespace = CreateMembershipNamespace(receiverManager, serializer);
        Assert.Equal(DisseminationApplyResult.Applied, await receiverNamespace.ApplyValueAsync(first, CancellationToken.None));
        Assert.Equal(DisseminationApplyResult.Applied, await receiverNamespace.ApplyValueAsync(second, CancellationToken.None));
        Assert.Equal(DisseminationApplyResult.Applied, await receiverNamespace.ApplyValueAsync(third, CancellationToken.None));
        Assert.Equal(thirdSnapshot.Version, receiverManager.CurrentSnapshot.Version);
        Assert.Equal(SiloStatus.Active, receiverManager.CurrentSnapshot.Entries[peer].Status);
    }

    [Fact]
    public async Task MembershipNamespacePublishesFullSnapshotAfterFailedBaseline()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var firstSnapshot = CreateMembershipSnapshot(
            version: 1,
            CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch),
            CreateMembershipEntry(peer, SiloStatus.Active, DateTime.UnixEpoch.AddSeconds(1)));
        var secondSnapshot = CreateMembershipSnapshot(
            version: 2,
            CreateMembershipEntry(
                local,
                SiloStatus.Active,
                DateTime.UnixEpoch,
                iAmAliveTime: DateTime.UnixEpoch.AddSeconds(2)),
            CreateMembershipEntry(peer, SiloStatus.Active, DateTime.UnixEpoch.AddSeconds(1)));
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var sourceManager = new FakeMembershipManager(firstSnapshot);
        var sourceNamespace = CreateMembershipNamespace(sourceManager, serializer);
        var disseminationService = new FakeDisseminationService();
        disseminationService.Results.Enqueue(false);
        disseminationService.Results.Enqueue(true);

        Assert.False(await sourceNamespace.PublishAsync(disseminationService, firstSnapshot, CancellationToken.None));
        sourceManager.CurrentSnapshot = secondSnapshot;
        Assert.True(await sourceNamespace.PublishAsync(disseminationService, secondSnapshot, CancellationToken.None));

        Assert.Equal(0, disseminationService.Values[1].FromVersion);
        Assert.NotNull(Assert.IsType<MembershipTableSnapshotUpdate>(
            serializer.Deserialize<MembershipTableSnapshotUpdate>(disseminationService.Values[1].Payload)).Snapshot);
    }

    [Fact]
    public async Task MembershipNamespacePublishesDiffWhenTopologyChanges()
    {
        var local = CreateSilo(11111);
        var firstPeer = CreateSilo(11112);
        var secondPeer = CreateSilo(11113);
        var firstSnapshot = CreateMembershipSnapshot(
            version: 1,
            CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch),
            CreateMembershipEntry(firstPeer, SiloStatus.Active, DateTime.UnixEpoch.AddSeconds(1)));
        var secondSnapshot = CreateMembershipSnapshot(
            version: 2,
            CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch),
            CreateMembershipEntry(firstPeer, SiloStatus.Active, DateTime.UnixEpoch.AddSeconds(1)),
            CreateMembershipEntry(secondPeer, SiloStatus.Active, DateTime.UnixEpoch.AddSeconds(2)));
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var sourceManager = new FakeMembershipManager(firstSnapshot);
        var sourceNamespace = CreateMembershipNamespace(sourceManager, serializer);
        var disseminationService = new FakeDisseminationService();

        Assert.True(await sourceNamespace.PublishAsync(disseminationService, firstSnapshot, CancellationToken.None));
        sourceManager.CurrentSnapshot = secondSnapshot;
        Assert.True(await sourceNamespace.PublishAsync(disseminationService, secondSnapshot, CancellationToken.None));

        Assert.Equal(1, disseminationService.Values[1].FromVersion);
        Assert.NotNull(Assert.IsType<MembershipTableSnapshotUpdate>(
            serializer.Deserialize<MembershipTableSnapshotUpdate>(disseminationService.Values[1].Payload)).Diff);
    }

    [Fact]
    public async Task MembershipNamespacePublishesDirectDiffAcrossVersionGap()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var firstSnapshot = CreateMembershipSnapshot(
            version: 1,
            CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch),
            CreateMembershipEntry(peer, SiloStatus.Active, DateTime.UnixEpoch.AddSeconds(1)));
        var thirdSnapshot = CreateMembershipSnapshot(
            version: 3,
            CreateMembershipEntry(
                local,
                SiloStatus.Active,
                DateTime.UnixEpoch,
                iAmAliveTime: DateTime.UnixEpoch.AddSeconds(2)),
            CreateMembershipEntry(peer, SiloStatus.Active, DateTime.UnixEpoch.AddSeconds(1)));
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var sourceManager = new FakeMembershipManager(firstSnapshot);
        var sourceNamespace = CreateMembershipNamespace(sourceManager, serializer);
        var disseminationService = new FakeDisseminationService();

        Assert.True(await sourceNamespace.PublishAsync(disseminationService, firstSnapshot, CancellationToken.None));
        sourceManager.CurrentSnapshot = thirdSnapshot;
        Assert.True(await sourceNamespace.PublishAsync(disseminationService, thirdSnapshot, CancellationToken.None));

        Assert.Equal(1, disseminationService.Values[1].FromVersion);
        Assert.NotNull(Assert.IsType<MembershipTableSnapshotUpdate>(
            serializer.Deserialize<MembershipTableSnapshotUpdate>(disseminationService.Values[1].Payload)).Diff);
    }

    [Fact]
    public async Task MembershipNamespaceAllowsConcurrentPublicationNotifications()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var firstSnapshot = CreateMembershipSnapshot(
            version: 1,
            CreateMembershipEntry(local, SiloStatus.Active, DateTime.UnixEpoch),
            CreateMembershipEntry(peer, SiloStatus.Active, DateTime.UnixEpoch.AddSeconds(1)));
        var secondSnapshot = CreateMembershipSnapshot(
            version: 2,
            CreateMembershipEntry(
                local,
                SiloStatus.Active,
                DateTime.UnixEpoch,
                iAmAliveTime: DateTime.UnixEpoch.AddSeconds(2)),
            CreateMembershipEntry(peer, SiloStatus.Active, DateTime.UnixEpoch.AddSeconds(1)));
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var sourceManager = new FakeMembershipManager(firstSnapshot);
        var sourceNamespace = CreateMembershipNamespace(sourceManager, serializer);
        var disseminationService = new FakeDisseminationService();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        disseminationService.PublishHandler = async (value, cancellationToken) =>
        {
            if (Interlocked.Increment(ref callCount) == 1)
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
            }

            return true;
        };

        var firstPublish = sourceNamespace.PublishAsync(disseminationService, firstSnapshot, CancellationToken.None).AsTask();
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        sourceManager.CurrentSnapshot = secondSnapshot;
        var secondPublish = sourceNamespace.PublishAsync(disseminationService, secondSnapshot, CancellationToken.None).AsTask();
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            Assert.Equal(2, disseminationService.Values.Count);
            Assert.True(secondPublish.IsCompletedSuccessfully);
        }
        finally
        {
            releaseFirst.TrySetResult();
        }

        Assert.True(await firstPublish);
        Assert.True(await secondPublish);
        Assert.Equal(new long[] { 1, 2 }, disseminationService.Values.Select(static value => value.ToVersion).Order());
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
    public void ManifestHashIncludesEntryBoundaries()
    {
        var manifest1 = CreateManifestWithProperties(
            ("a", [("b", "c")]),
            ("d", [("e", "f"), ("g", "h")]));
        var manifest2 = CreateManifestWithProperties(
            ("a", [("b", "c"), ("d", "e")]),
            ("f", [("g", "h")]));

        Assert.NotEqual(ManifestHashCalculator.ComputeHash(manifest1), ManifestHashCalculator.ComputeHash(manifest2));
    }

    [Fact]
    public void ManifestHashUsesRawTypeIdentifierBytes()
    {
        var properties = new GrainProperties(
            System.Collections.Immutable.ImmutableDictionary<string, string>.Empty
                .WithComparers(StringComparer.Ordinal));
        var first = new GrainManifest(
            System.Collections.Immutable.ImmutableDictionary<GrainType, GrainProperties>.Empty
                .Add(new GrainType([0x80]), properties),
            System.Collections.Immutable.ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty);
        var second = new GrainManifest(
            System.Collections.Immutable.ImmutableDictionary<GrainType, GrainProperties>.Empty
                .Add(new GrainType([0x81]), properties),
            System.Collections.Immutable.ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty);

        Assert.NotEqual(ManifestHashCalculator.ComputeHash(first), ManifestHashCalculator.ComputeHash(second));
    }

    [Fact]
    public void ManifestHashDistinguishesNullAndEmptyPropertyValues()
    {
        var nullProperties = System.Collections.Immutable.ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        nullProperties["value"] = null!;
        var emptyProperties = System.Collections.Immutable.ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        emptyProperties["value"] = string.Empty;
        var nullManifest = new GrainManifest(
            System.Collections.Immutable.ImmutableDictionary<GrainType, GrainProperties>.Empty
                .Add(GrainType.Create("grain"), new GrainProperties(nullProperties.ToImmutable())),
            System.Collections.Immutable.ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty);
        var emptyManifest = new GrainManifest(
            System.Collections.Immutable.ImmutableDictionary<GrainType, GrainProperties>.Empty
                .Add(GrainType.Create("grain"), new GrainProperties(emptyProperties.ToImmutable())),
            System.Collections.Immutable.ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty);

        Assert.NotEqual(
            ManifestHashCalculator.ComputeHash(nullManifest),
            ManifestHashCalculator.ComputeHash(emptyManifest));
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
    public void NamespaceOptionsValidatorAllowsStaleTtlShorterThanCoalescingDelay()
    {
        var options = new DeploymentLoadPublisherOptions();
        options.Dissemination.MaxCoalescingDelay = TimeSpan.FromSeconds(1);
        options.Dissemination.StaleItemTtl = TimeSpan.FromMilliseconds(1);

        var result = new DeploymentLoadPublisherOptionsValidator().Validate(Options.DefaultName, options);

        Assert.Equal(ValidateOptionsResult.Success, result);
    }

    [Fact]
    public void NamespaceOptionsValidatorRejectsNonPositiveStaleTtl()
    {
        var options = new DeploymentLoadPublisherOptions();
        options.Dissemination.StaleItemTtl = TimeSpan.Zero;

        var result = new DeploymentLoadPublisherOptionsValidator().Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4294967295)]
    public void OptionsValidatorRejectsUnsupportedAntiEntropyIntervals(long milliseconds)
    {
        var options = new DisseminationOptions();
        options.Overlay.AntiEntropyInterval = TimeSpan.FromMilliseconds(milliseconds);
        var result = new DisseminationOptionsValidator().Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4294967295)]
    public void NamespaceOptionsValidatorRejectsUnsupportedCoalescingDelays(long milliseconds)
    {
        var options = new DeploymentLoadPublisherOptions();
        options.Dissemination.MaxCoalescingDelay = TimeSpan.FromMilliseconds(milliseconds);
        options.Dissemination.StaleItemTtl = options.Dissemination.MaxCoalescingDelay + TimeSpan.FromSeconds(1);
        var result = new DeploymentLoadPublisherOptionsValidator().Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
    }

    [Fact]
    public async Task ReceiveBroadcastUsesRelativeLifetimeAcrossClockSkew()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var ns = new FakeNamespace(local);
        var timeProvider = new FakeTimeProvider(DateTimeOffset.MaxValue - TimeSpan.FromSeconds(1));
        var protocol = CreateProtocol(transport, ns, timeProvider: timeProvider);
        var item = ns.CreateItem(peer, FakeNamespace.DefaultKey, sequence: 1);
        item = new DisseminationBroadcastValue
        {
            Value = item.Value,
            TimeToLive = TimeSpan.FromMilliseconds(1),
        };

        await protocol.ReceiveBroadcast(CreateBroadcastBatch(peer, item), CancellationToken.None);

        Assert.Equal(1, ns.GetVersion(FakeNamespace.DefaultKey));
    }

    [Fact]
    public async Task ReceiveBroadcastDropsNonPositiveRelativeLifetime()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var ns = new FakeNamespace(local);
        var protocol = CreateProtocol(transport, ns);
        var item = ns.CreateItem(peer, FakeNamespace.DefaultKey, sequence: 1);
        item = new DisseminationBroadcastValue
        {
            Value = item.Value,
            TimeToLive = TimeSpan.Zero,
        };

        await protocol.ReceiveBroadcast(CreateBroadcastBatch(peer, item), CancellationToken.None);

        Assert.Equal(0, ns.GetVersion(FakeNamespace.DefaultKey));
    }

    [Fact]
    public async Task BroadcastTransportCancelsWhenRelativeLifetimeExpires()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var timeProvider = new FakeTimeProvider();
        var sendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.SendBroadcastResponseHandler = async (_, _, cancellationToken) =>
        {
            sendStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                cancellationObserved.TrySetResult();
            }

            return new DisseminationBroadcastResponse();
        };

        var ns = new FakeNamespace(local);
        ns.Options.MaxCoalescingDelay = TimeSpan.FromSeconds(1);
        ns.Options.StaleItemTtl = TimeSpan.FromSeconds(1);
        var protocol = CreateProtocol(transport, ns, timeProvider: timeProvider);

        Assert.True(await PublishValue(
            protocol,
            ns,
            ns.CreateValue(FakeNamespace.DefaultKey, sequence: 1),
            CancellationToken.None));
        timeProvider.Advance(ns.Options.MaxCoalescingDelay);
        await sendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        timeProvider.Advance(ns.Options.StaleItemTtl);
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

        ns.Options.Enabled = false;
        await protocol.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
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
        Assert.Equal(TimeSpan.FromSeconds(5), new DeploymentLoadPublisherOptions().Dissemination.ExpectedUpdateCadence);
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
        Assert.Equal(new[] { local, peer }, first.Members);
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
        Assert.Equal(new[] { local, peer }, second.Members);
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
            new DisseminationOverlayOptions()));

        Assert.Equal("members", exception.ParamName);
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
            CreateOverlayOptions(fanout: 2));

        var targets = snapshot.OriginatorTreeTargets;

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
            CreateOverlayOptions(fanout: 2));

        var targets = snapshot.ForwardingTreeTargets;

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
            CreateOverlayOptions(fanout: 1));

        var targets = snapshot.OriginatorTreeTargets;
        var antiEntropyPeers = snapshot.SelectAntiEntropyPeers(1);

        Assert.False(snapshot.ContainsMember(local));
        Assert.Single(snapshot.Members);
        Assert.Empty(targets);
        Assert.Empty(antiEntropyPeers);
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
            new DisseminationOverlayOptions());

        var selectedPeers = snapshot.SelectAntiEntropyPeers(peerCount);

        Assert.Equal(silos.Length - 1, selectedPeers.Length);
        Assert.Equal(selectedPeers.Length, selectedPeers.Distinct().Count());
        Assert.DoesNotContain(local, selectedPeers);
        Assert.Equal(expected, selectedPeers.OrderBy(static silo => silo));
    }

    [Fact]
    public async Task DisabledNamespaceBetweenPublishAndReschedule_UsesFiniteBoundedFallbackWithoutInfiniteTimerDueTime()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var timeProvider = new RecordingFakeTimeProvider();
        var sendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFailedSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendCount = 0;
        transport.SendBroadcastHandler = async (_, _, _) =>
        {
            Interlocked.Increment(ref sendCount);
            sendStarted.TrySetResult();
            await releaseFailedSend.Task;
            throw new InvalidOperationException("The initial send fails after the namespace is disabled.");
        };

        var ns = new FakeNamespace(local);
        ns.Options.MaxCoalescingDelay = TimeSpan.FromSeconds(1);
        var protocol = CreateProtocol(
            transport,
            ns,
            options => options.Overlay.AntiEntropyInterval = TimeSpan.FromSeconds(4),
            timeProvider);
        using var schedule = new BroadcastScheduleObserver();
        var retryScheduled = schedule.WaitAsync(
            e => e.Peer.Equals(peer)
                && e.Reason == DisseminationBroadcastScheduleReason.Retry
                && e.Attempt == 1,
            TimeSpan.FromSeconds(5));

        Assert.True(await PublishValue(
            protocol,
            ns,
            ns.CreateValue(FakeNamespace.DefaultKey, sequence: 1),
            CancellationToken.None));
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await sendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        ns.Options.Enabled = false;
        releaseFailedSend.TrySetResult();

        var retry = await retryScheduled;
        Assert.Equal(TimeSpan.FromMilliseconds(100), retry.DueTime);
        Assert.DoesNotContain(TimeSpan.MaxValue, timeProvider.TimerDueTimes);

        timeProvider.Advance(retry.DueTime);
        await protocol.FlushPendingBroadcast(CancellationToken.None);
        await protocol.StopAsync(CancellationToken.None);

        Assert.Equal(1, sendCount);
        Assert.Empty(transport.BroadcastBatches);
    }

    [Fact]
    public async Task UnexpectedPeerPumpIterationFailure_IsRetriedAndCompletesFlushAndStopDrainWaiters()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var timeProvider = new RecordingFakeTimeProvider();
        var logger = new RecordingLogger<DisseminationBroadcastQueue>();
        var firstSendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retrySendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRetrySend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendCount = 0;
        transport.SendBroadcastResponseHandler = async (_, batch, _) =>
        {
            if (Interlocked.Increment(ref sendCount) == 1)
            {
                firstSendStarted.TrySetResult();
                await releaseFirstSend.Task;
                return new DisseminationBroadcastResponse
                {
                    Acknowledgments = new()
                    {
                        [FakeNamespace.DefaultName] =
                        [
                            new DigestEntry(FakeNamespace.DefaultKey, version: 0),
                        ],
                    },
                };
            }

            retrySendStarted.TrySetResult();
            await releaseRetrySend.Task;
            transport.BroadcastBatches.Add((peer, batch));
            return FakeTransport.CreateAcknowledgment(batch);
        };

        var ns = new FakeNamespace(local);
        ns.Options.MaxCoalescingDelay = TimeSpan.FromSeconds(1);
        var protocol = CreateProtocolWithBroadcastLogger(
            transport,
            ns,
            options => options.Overlay.AntiEntropyInterval = TimeSpan.FromSeconds(4),
            timeProvider,
            logger);
        using var schedule = new BroadcastScheduleObserver();
        var recoveredRetryScheduled = schedule.WaitAsync(
            e => e.Peer.Equals(peer)
                && e.Reason == DisseminationBroadcastScheduleReason.Retry
                && e.Attempt == 2,
            TimeSpan.FromSeconds(5));

        Assert.True(await PublishValue(
            protocol,
            ns,
            ns.CreateValue(FakeNamespace.DefaultKey, sequence: 1),
            CancellationToken.None));
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await firstSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var flushTask = protocol.FlushPendingBroadcast(CancellationToken.None);
        Assert.False(flushTask.IsCompleted);
        timeProvider.ThrowOnNextTimerChange();
        releaseFirstSend.TrySetResult();

        await flushTask.WaitAsync(TimeSpan.FromSeconds(5));
        var retry = await recoveredRetryScheduled;
        Assert.Equal(TimeSpan.FromSeconds(2), retry.DueTime);
        Assert.Equal(1, logger.WarningCount);
        Assert.DoesNotContain(TimeSpan.MaxValue, timeProvider.TimerDueTimes);

        timeProvider.Advance(retry.DueTime);
        await retrySendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopTask = protocol.StopAsync(CancellationToken.None);
        Assert.False(stopTask.IsCompleted);
        releaseRetrySend.TrySetResult();

        await stopTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, sendCount);
        var batch = Assert.Single(transport.BroadcastBatches);
        Assert.Equal(1, Assert.Single(GetBroadcastValues(batch.Batch)).Value.ToVersion);
    }

    [Fact]
    public async Task PermanentPeerPumpRecoveryFailure_FaultsFlushAndStopDrainWaiters()
    {
        var local = CreateSilo(11111);
        var peer = CreateSilo(11112);
        var transport = new FakeTransport(local, peer);
        var timeProvider = new RecordingFakeTimeProvider();
        var firstSendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.SendBroadcastResponseHandler = async (_, _, _) =>
        {
            firstSendStarted.TrySetResult();
            await releaseFirstSend.Task;
            return new DisseminationBroadcastResponse
            {
                Acknowledgments = new()
                {
                    [FakeNamespace.DefaultName] =
                    [
                        new DigestEntry(FakeNamespace.DefaultKey, version: 0),
                    ],
                },
            };
        };

        var ns = new FakeNamespace(local);
        ns.Options.MaxCoalescingDelay = TimeSpan.FromSeconds(1);
        var protocol = CreateProtocol(
            transport,
            ns,
            options => options.Overlay.AntiEntropyInterval = TimeSpan.FromSeconds(4),
            timeProvider);

        Assert.True(await PublishValue(
            protocol,
            ns,
            ns.CreateValue(FakeNamespace.DefaultKey, sequence: 1),
            CancellationToken.None));
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await firstSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var flushTask = protocol.FlushPendingBroadcast(CancellationToken.None);
        timeProvider.ThrowOnNextTimerChanges(2);
        releaseFirstSend.TrySetResult();

        var flushException = await Assert.ThrowsAsync<AggregateException>(() => flushTask);
        Assert.Contains("could not recover", flushException.Message, StringComparison.Ordinal);

        var stopException = await Assert.ThrowsAsync<AggregateException>(
            () => protocol.StopAsync(CancellationToken.None));
        Assert.Same(flushException, stopException);
    }

    private static DisseminationProtocol CreateProtocol(
        FakeTransport transport,
        FakeNamespace ns,
        Action<DisseminationOptions>? configure = null,
        TimeProvider? timeProvider = null) =>
        CreateProtocol(transport, new IDisseminationNamespace[] { ns }, configure, timeProvider);

    private static ValueTask<bool> PublishValue(
        DisseminationProtocol protocol,
        FakeNamespace disseminationNamespace,
        DisseminationValue value,
        CancellationToken cancellationToken)
    {
        disseminationNamespace.PublishValue(value);
        return protocol.Publish(
            disseminationNamespace,
            value.Key,
            value.ToVersion,
            cancellationToken);
    }

    private static DisseminationProtocol CreateProtocol(
        FakeTransport transport,
        IReadOnlyList<IDisseminationNamespace> namespaces,
        Action<DisseminationOptions>? configure = null,
        TimeProvider? timeProvider = null)
    {
        var options = new DisseminationOptions { Enabled = true };
        configure?.Invoke(options);
        var localSiloDetails = new FakeLocalSiloDetails(transport.LocalSilo);
        return new DisseminationProtocol(
            localSiloDetails,
            transport.GrainFactory,
            new DisseminationMembership(
                transport.MembershipManager,
                localSiloDetails,
                Options.Create(options)),
            new TestOptionsMonitor<DisseminationOptions>(options),
            namespaces,
            timeProvider ?? TimeProvider.System,
            NullLogger<DisseminationProtocol>.Instance,
            NullLogger<DisseminationBroadcastQueue>.Instance);
    }

    private static DisseminationProtocol CreateProtocolWithBroadcastLogger(
        FakeTransport transport,
        FakeNamespace ns,
        Action<DisseminationOptions> configure,
        TimeProvider timeProvider,
        Microsoft.Extensions.Logging.ILogger<DisseminationBroadcastQueue> broadcastLogger)
    {
        var options = new DisseminationOptions { Enabled = true };
        configure(options);
        var localSiloDetails = new FakeLocalSiloDetails(transport.LocalSilo);
        return new DisseminationProtocol(
            localSiloDetails,
            transport.GrainFactory,
            new DisseminationMembership(
                transport.MembershipManager,
                localSiloDetails,
                Options.Create(options)),
            new TestOptionsMonitor<DisseminationOptions>(options),
            [ns],
            timeProvider,
            NullLogger<DisseminationProtocol>.Instance,
            broadcastLogger);
    }

    private static DisseminationOverlayOptions CreateOverlayOptions(int fanout) => new()
    {
        FanOutFactor = _ => fanout,
    };

    private static SiloAddress CreateSilo(int port) => SiloAddress.New(new IPEndPoint(IPAddress.Loopback, port), port);

    private static SiloAddress[] CreateSilos(int count) =>
        Enumerable.Range(11111, count).Select(CreateSilo).OrderBy(static silo => silo).ToArray();

    private static Dictionary<DisseminationNamespace, List<DigestEntry>> CreateAntiEntropyRequestDigest(
        DisseminationNamespace namespaceName,
        params (DisseminationKey Key, long Version)[] versions) =>
        new()
        {
            [namespaceName] = versions
                .Select(static entry => new DigestEntry(entry.Key, entry.Version))
                .ToList(),
        };

    private static DisseminationBroadcastBatch CreateBroadcastBatch(SiloAddress sender, params DisseminationBroadcastValue[] values) => new()
    {
        Sender = sender,
        Values = CreateBroadcastValueGroups(values),
    };

    private static IEnumerable<DisseminationBroadcastValue> GetBroadcastValues(DisseminationBroadcastBatch batch) =>
        batch.Values.Values.SelectMany(static values => values);

    private static SiloAddress[] GetSentBroadcastPeers(FakeTransport transport)
    {
        lock (transport.BroadcastBatches)
        {
            return [.. transport.BroadcastBatches.Select(static batch => batch.Peer)];
        }
    }

    private static void ClearBroadcastBatches(FakeTransport transport)
    {
        lock (transport.BroadcastBatches)
        {
            transport.BroadcastBatches.Clear();
        }
    }

    private static IEnumerable<DisseminationBroadcastValue> GetAntiEntropyResponseValues(DisseminationAntiEntropyResponse response) =>
        response.Values.Values.SelectMany(static values => values);

    private static Dictionary<DisseminationNamespace, List<DisseminationBroadcastValue>> CreateBroadcastValueGroups(params DisseminationBroadcastValue[] values) =>
        new()
        {
            [FakeNamespace.DefaultName] = [.. values],
        };

    private static Dictionary<DisseminationNamespace, List<DisseminationBroadcastValue>> CreateValueGroups(params DisseminationBroadcastValue[] values) =>
        CreateValueGroups(FakeNamespace.DefaultName, values);

    private static Dictionary<DisseminationNamespace, List<DisseminationBroadcastValue>> CreateValueGroups(DisseminationNamespace namespaceName, params DisseminationBroadcastValue[] values) =>
        new()
        {
            [namespaceName] = [.. values],
        };

    private static MembershipDisseminationNamespace CreateMembershipNamespace(
        FakeMembershipManager membershipManager,
        Serializer serializer)
    {
        var options = new ClusterMembershipOptions();
        options.Dissemination.Enabled = true;
        return new(
            membershipManager,
            new TestOptionsMonitor<ClusterMembershipOptions>(options),
            serializer);
    }

    private static DisseminationBroadcastValue CreateDisseminationValue(SiloAddress originator, DisseminationValue value)
    {
        _ = originator;
        return new()
        {
            Value = value,
            TimeToLive = TimeSpan.FromMinutes(1),
        };
    }

    private static MembershipTableSnapshot CreateMembershipSnapshot(long version, params MembershipEntry[] entries) =>
        new(new MembershipVersion(version), entries.ToImmutableDictionary(static entry => entry.SiloAddress));

    private static MembershipEntry CreateMembershipEntry(
        SiloAddress silo,
        SiloStatus status,
        DateTime startTime,
        DateTime? iAmAliveTime = null) => new()
    {
        SiloAddress = silo,
        Status = status,
        ProxyPort = silo.Endpoint.Port,
        HostName = "localhost",
        SiloName = silo.ToParsableString(),
        RoleName = "test",
        StartTime = startTime,
        IAmAliveTime = iAmAliveTime ?? startTime,
    };

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

    private static HashSet<SiloAddress> GetReachedParticipants(
        SiloAddress root,
        IReadOnlyDictionary<SiloAddress, DisseminationMembershipSnapshot> snapshots)
    {
        var reached = new HashSet<SiloAddress> { root };
        var pending = new Queue<SiloAddress>(snapshots[root].OriginatorTreeTargets);
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            if (!reached.Add(current))
            {
                continue;
            }

            foreach (var child in snapshots[current].ForwardingTreeTargets)
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

    private static GrainManifest CreateManifestWithProperties(params (string Grain, (string Key, string Value)[] Properties)[] grains)
    {
        var grainBuilder = System.Collections.Immutable.ImmutableDictionary.CreateBuilder<GrainType, GrainProperties>();
        foreach (var grain in grains)
        {
            var properties = System.Collections.Immutable.ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            foreach (var property in grain.Properties)
            {
                properties[property.Key] = property.Value;
            }

            grainBuilder[GrainType.Create(grain.Grain)] = new GrainProperties(properties.ToImmutable());
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
        private readonly object _lock = new();
        private readonly Dictionary<DisseminationKey, long> _versions = new();
        private readonly Dictionary<DisseminationKey, SortedDictionary<long, DisseminationValue>> _publishedValues = new();
        private readonly DisseminationNamespace _name;
        private int _repairRequestCount;

        public FakeNamespace(SiloAddress localSilo, DisseminationNamespace? name = null)
        {
            _ = localSilo;
            _name = name ?? DefaultName;
        }

        public Dictionary<DisseminationKey, int> ApplyCounts { get; } = new();

        public HashSet<DisseminationKey> ExpectedKeys { get; } = new();

        public DisseminationNamespace Name => _name;

        public DisseminationNamespaceOptions Options { get; } = new() { Enabled = true };

        public bool ReturnRepairChain { get; set; }

        public int RepairRequestCount => Volatile.Read(ref _repairRequestCount);

        public TaskCompletionSource? ApplyObserved { get; set; }

        public DisseminationValue CreateValue(DisseminationKey key, long sequence, long fromVersion = 0) => new(
            key,
            fromVersion,
            sequence,
            BitConverter.GetBytes(sequence));

        public DisseminationBroadcastValue CreateItem(SiloAddress originator, DisseminationKey key, long sequence, long fromVersion = 0) =>
            CreateDisseminationValue(originator, CreateValue(key, sequence, fromVersion));

        public void PublishValue(DisseminationValue value)
        {
            lock (_lock)
            {
                if (!_publishedValues.TryGetValue(value.Key, out var values))
                {
                    values = [];
                    _publishedValues.Add(value.Key, values);
                }

                values[value.ToVersion] = value;
                if (!_versions.TryGetValue(value.Key, out var version) || value.ToVersion > version)
                {
                    _versions[value.Key] = value.ToVersion;
                }
            }
        }

        public void SetValue(DisseminationKey key, long version) => PublishValue(CreateValue(key, version));

        public void RemoveValue(DisseminationKey key)
        {
            lock (_lock)
            {
                _versions.Remove(key);
                _publishedValues.Remove(key);
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _versions.Clear();
                _publishedValues.Clear();
            }
        }

        public long GetVersion(DisseminationKey key)
        {
            lock (_lock)
            {
                return _versions.TryGetValue(key, out var version) ? version : 0;
            }
        }

        public IEnumerable<DigestEntry> Digests
        {
            get
            {
                KeyValuePair<DisseminationKey, long>[] versions;
                lock (_lock)
                {
                    versions = [.. _versions];
                }

                var present = new HashSet<DisseminationKey>(versions.Length);
                foreach (var (key, version) in versions)
                {
                    present.Add(key);
                    yield return new DigestEntry(key, version);
                }

                foreach (var key in ExpectedKeys)
                {
                    if (!present.Contains(key))
                    {
                        yield return new DigestEntry(key, 0);
                    }
                }
            }
        }

        public DisseminationRepairResult CreateRepair(in DisseminationRepairRequest request)
        {
            Interlocked.Increment(ref _repairRequestCount);
            long version;
            List<DisseminationValue> candidates;
            lock (_lock)
            {
                if (!_versions.TryGetValue(request.Key, out var currentVersion))
                {
                    return DisseminationRepairResult.Unavailable(version: 0);
                }

                version = request.ToVersion ?? currentVersion;
                if (version > currentVersion)
                {
                    return DisseminationRepairResult.Unavailable(currentVersion);
                }

                if (request.FromVersion is { } peerVersion && peerVersion >= version)
                {
                    return DisseminationRepairResult.Current(version);
                }

                if (ReturnRepairChain
                    && _publishedValues.TryGetValue(request.Key, out var publishedValues))
                {
                    candidates = [];
                    long? expectedVersion = request.FromVersion;
                    foreach (var value in publishedValues.Values)
                    {
                        if (value.ToVersion > version
                            || expectedVersion is { } expected && value.ToVersion <= expected)
                        {
                            continue;
                        }

                        if (expectedVersion is null && value.FromVersion != 0
                            || expectedVersion is { } fromVersion
                            && value.FromVersion != 0
                            && value.FromVersion != fromVersion)
                        {
                            continue;
                        }

                        candidates.Add(value);
                        expectedVersion = value.ToVersion;
                        if (value.ToVersion == version)
                        {
                            break;
                        }
                    }
                }
                else
                {
                    candidates = [];
                }

                if (candidates.Count == 0 || candidates[^1].ToVersion != version)
                {
                    var value = _publishedValues.TryGetValue(request.Key, out var valuesByVersion)
                        && valuesByVersion.TryGetValue(version, out var publishedValue)
                        && publishedValue.FromVersion == 0
                            ? publishedValue
                            : CreateValue(request.Key, version);
                    candidates = [value];
                }
            }

            if (request.MaxItemCount <= 0)
            {
                return DisseminationRepairResult.InsufficientCapacity(version);
            }

            var values = ImmutableArray.CreateBuilder<DisseminationValue>();
            var byteCount = 0;
            foreach (var value in candidates)
            {
                if (values.Count >= request.MaxItemCount
                    || value.Payload.Length > request.MaxPayloadBytes
                    || value.Payload.Length > request.MaxBatchBytes - byteCount)
                {
                    break;
                }

                values.Add(value);
                byteCount += value.Payload.Length;
            }

            if (values.Count == 0)
            {
                return DisseminationRepairResult.InsufficientCapacity(version);
            }

            var isComplete = values[^1].ToVersion == version;
            return DisseminationRepairResult.Produced(
                version,
                values.ToImmutable(),
                isComplete);
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

            lock (_lock)
            {
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
                if (!_publishedValues.TryGetValue(value.Key, out var values))
                {
                    values = [];
                    _publishedValues.Add(value.Key, values);
                }

                values[version] = CreateValue(value.Key, version);
                ApplyCounts[value.Key] = ApplyCounts.TryGetValue(value.Key, out var count) ? count + 1 : 1;
            }

            ApplyObserved?.TrySetResult();
            return ValueTask.FromResult(DisseminationApplyResult.Applied);
        }
    }

    private sealed class FakeDisseminationService : IDisseminationService
    {
        private readonly Dictionary<(DisseminationNamespace Namespace, DisseminationKey Key), long> _knownVersions = [];

        public List<DisseminationValue> Values { get; } = new();

        public Queue<bool> Results { get; } = new();

        public Func<DisseminationValue, CancellationToken, ValueTask<bool>>? PublishHandler { get; set; }

        public async ValueTask<bool> Publish(
            IDisseminationNamespace disseminationNamespace,
            DisseminationKey key,
            long version,
            CancellationToken cancellationToken)
        {
            var stream = (disseminationNamespace.Name, key);
            var repair = disseminationNamespace.CreateRepair(new DisseminationRepairRequest(
                key,
                _knownVersions.TryGetValue(stream, out var knownVersion) ? knownVersion : null,
                version,
                maxItemCount: 1024,
                maxBatchBytes: 1024 * 1024,
                maxPayloadBytes: disseminationNamespace.Options.MaxPayloadBytes));
            if (repair.Status is not DisseminationRepairStatus.Produced || !repair.IsComplete)
            {
                return false;
            }

            Values.AddRange(repair.Values);
            var result = PublishHandler is null
                ? Results.Count == 0 || Results.Dequeue()
                : await PublishHandler(repair.Values[^1], cancellationToken);
            if (result)
            {
                _knownVersions[stream] = repair.Version;
            }

            return result;
        }
    }

    private sealed class FakeTransport
    {
        private readonly SiloAddress _localSilo;
        private readonly List<SiloAddress> _peers;
        private readonly Dictionary<SiloAddress, FakeDisseminationSystemTarget> _targets = new();

        public FakeTransport(SiloAddress localSilo, params SiloAddress[] peers)
        {
            _localSilo = localSilo;
            _peers = peers.ToList();
            MembershipManager = new FakeMembershipManager(GetMembershipSnapshot, RefreshMembership);
            GrainFactory = Substitute.For<IInternalGrainFactory>();
            GrainFactory
                .GetSystemTarget<IDisseminationSystemTarget>(Constants.DisseminationSystemTargetType, Arg.Any<SiloAddress>())
                .Returns(callInfo => GetSystemTarget(callInfo.ArgAt<SiloAddress>(1)));
        }

        public List<(SiloAddress Peer, DisseminationBroadcastBatch Batch)> BroadcastBatches { get; } = new();

        public List<(SiloAddress Peer, DisseminationAntiEntropyRequest Request)> AntiEntropyRequests { get; } = new();

        public IInternalGrainFactory GrainFactory { get; }

        public FakeMembershipManager MembershipManager { get; }

        public List<SiloAddress> Peers => _peers;

        public Dictionary<SiloAddress, SiloStatus> PeerStatuses { get; } = new();

        public Dictionary<SiloAddress, DateTime> StartTimes { get; } = new();

        public Dictionary<SiloAddress, int> TargetResolutionCounts { get; } = new();

        public Func<SiloAddress, DisseminationBroadcastBatch, CancellationToken, Task>? SendBroadcastHandler { get; set; }

        public Func<SiloAddress, DisseminationBroadcastBatch, CancellationToken, Task<DisseminationBroadcastResponse>>? SendBroadcastResponseHandler { get; set; }

        public Func<SiloAddress, DisseminationAntiEntropyRequest, CancellationToken, ValueTask<DisseminationAntiEntropyResponse>> ExchangeAntiEntropyHandler { get; set; } =
            static (peer, _, _) => ValueTask.FromResult(new DisseminationAntiEntropyResponse { Sender = peer });

        public Func<CancellationToken, Task>? RefreshMembershipHandler { get; set; }

        public SiloAddress LocalSilo => _localSilo;

        public int RefreshMembershipCallCount { get; private set; }

        public int GetTargetResolutionCount(SiloAddress peer)
        {
            lock (_targets)
            {
                return TargetResolutionCounts.TryGetValue(peer, out var count) ? count : 0;
            }
        }

        private IDisseminationSystemTarget GetSystemTarget(SiloAddress peer)
        {
            lock (_targets)
            {
                TargetResolutionCounts[peer] = TargetResolutionCounts.TryGetValue(peer, out var count) ? count + 1 : 1;
                if (!_targets.TryGetValue(peer, out var target))
                {
                    target = new FakeDisseminationSystemTarget(peer, this);
                    _targets.Add(peer, target);
                }

                return target;
            }
        }

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

        public async Task<DisseminationBroadcastResponse> SendBroadcast(
            SiloAddress peer,
            DisseminationBroadcastBatch batch,
            CancellationToken cancellationToken)
        {
            if (SendBroadcastResponseHandler is not null)
            {
                return await SendBroadcastResponseHandler(peer, batch, cancellationToken);
            }

            if (SendBroadcastHandler is not null)
            {
                await SendBroadcastHandler(peer, batch, cancellationToken);
            }
            else
            {
                lock (BroadcastBatches)
                {
                    BroadcastBatches.Add((peer, batch));
                }
            }

            return CreateAcknowledgment(batch);
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

            return ExchangeAntiEntropyHandler(peer, request, cancellationToken);
        }

        public static DisseminationBroadcastResponse CreateAcknowledgment(DisseminationBroadcastBatch batch)
        {
            var acknowledgments = new Dictionary<DisseminationNamespace, List<DigestEntry>>();
            foreach (var (namespaceName, values) in batch.Values)
            {
                acknowledgments[namespaceName] = values
                    .GroupBy(static value => value.Value.Key)
                    .Select(static stream => new DigestEntry(
                        stream.Key,
                        stream.Max(static value => value.Value.ToVersion)))
                    .ToList();
            }

            return new DisseminationBroadcastResponse { Acknowledgments = acknowledgments };
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

    private sealed class FakeDisseminationSystemTarget(SiloAddress peer, FakeTransport transport) : IDisseminationSystemTarget
    {
        public Task<DisseminationBroadcastResponse> PushBroadcast(
            DisseminationBroadcastBatch batch,
            CancellationToken cancellationToken) =>
            transport.SendBroadcast(peer, batch, cancellationToken);

        public async Task<DisseminationAntiEntropyResponse> ExchangeAntiEntropy(
            DisseminationAntiEntropyRequest request,
            CancellationToken cancellationToken) =>
            await transport.ExchangeAntiEntropy(peer, request, cancellationToken);
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
            var localDigest = _topic.Digests.SingleOrDefault();
            if (localDigest.Version == 0)
            {
                return new ModelRepairResponse(false, 0);
            }

            if (localDigest.Version <= peerVersion)
            {
                return new ModelRepairResponse(false, 0);
            }

            var repair = _topic.CreateRepair(new DisseminationRepairRequest(
                FakeNamespace.DefaultKey,
                peerVersion,
                toVersion: null,
                maxItemCount: 1,
                maxBatchBytes: 1024,
                maxPayloadBytes: 1024));
            return repair.Status is DisseminationRepairStatus.Produced
                ? new ModelRepairResponse(true, repair.Version)
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
        private readonly object _lock = new();
        private DateTimeOffset _utcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private long _timestamp;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_lock)
            {
                return _utcNow;
            }
        }

        public override long GetTimestamp()
        {
            lock (_lock)
            {
                return _timestamp;
            }
        }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan value)
        {
            lock (_lock)
            {
                _utcNow += value;
                _timestamp += value.Ticks;
            }
        }
    }

    private sealed class AutoAdvancingTimeProvider(TimeSpan step) : TimeProvider
    {
        private readonly object _lock = new();
        private DateTimeOffset _utcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            lock (_lock)
            {
                var result = _utcNow;
                _utcNow += step;
                return result;
            }
        }
    }

    private sealed class RecordingFakeTimeProvider : TimeProvider
    {
        private readonly FakeTimeProvider _inner = new();
        private readonly object _lock = new();
        private readonly List<TimeSpan> _timerDueTimes = [];
        private int _throwOnNextTimerChange;

        public IReadOnlyList<TimeSpan> TimerDueTimes
        {
            get
            {
                lock (_lock)
                {
                    return [.. _timerDueTimes];
                }
            }
        }

        public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();

        public override long GetTimestamp() => _inner.GetTimestamp();

        public override long TimestampFrequency => _inner.TimestampFrequency;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            RecordTimerChange(dueTime);
            return new RecordingTimer(this, _inner.CreateTimer(callback, state, dueTime, period));
        }

        public void Advance(TimeSpan duration) => _inner.Advance(duration);

        public void ThrowOnNextTimerChange() => ThrowOnNextTimerChanges(1);

        public void ThrowOnNextTimerChanges(int count) => Interlocked.Exchange(ref _throwOnNextTimerChange, count);

        private bool ShouldThrowOnTimerChange()
        {
            while (true)
            {
                var current = Volatile.Read(ref _throwOnNextTimerChange);
                if (current <= 0)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _throwOnNextTimerChange, current - 1, current) == current)
                {
                    return true;
                }
            }
        }

        private void RecordTimerChange(TimeSpan dueTime)
        {
            lock (_lock)
            {
                _timerDueTimes.Add(dueTime);
            }
        }

        private sealed class RecordingTimer(RecordingFakeTimeProvider owner, ITimer inner) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                owner.RecordTimerChange(dueTime);
                if (owner.ShouldThrowOnTimerChange())
                {
                    throw new InvalidOperationException("The test timer fails one scheduled change.");
                }

                return inner.Change(dueTime, period);
            }

            public void Dispose() => inner.Dispose();

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    private sealed class RecordingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        private int _warningCount;

        public int WarningCount => Volatile.Read(ref _warningCount);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning)
            {
                Interlocked.Increment(ref _warningCount);
            }
        }
    }

    // Subscribes to the dissemination DiagnosticListener and lets a test deterministically await the moment a
    // peer pump (re)arms its flush timer, replacing wall-clock Task.Delay bridges. Buffered, consume-once
    // semantics mean an event emitted before the wait is registered is still observed.
    private sealed class BroadcastScheduleObserver : IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly object _lock = new();
        private readonly List<DisseminationBroadcastScheduledEvent> _events = new();
        private readonly HashSet<DisseminationBroadcastScheduledEvent> _consumed = new();
        private readonly List<Waiter> _waiters = new();
        private readonly IDisposable _subscription;

        public BroadcastScheduleObserver()
        {
            _subscription = DisseminationEvents.Listener.Subscribe(
                this,
                static name => name == DisseminationEvents.BroadcastScheduledEventName);
        }

        public Task<DisseminationBroadcastScheduledEvent> WaitAsync(
            Func<DisseminationBroadcastScheduledEvent, bool> predicate,
            TimeSpan timeout)
        {
            TaskCompletionSource<DisseminationBroadcastScheduledEvent> completion;
            lock (_lock)
            {
                foreach (var scheduled in _events)
                {
                    if (!_consumed.Contains(scheduled) && predicate(scheduled))
                    {
                        _consumed.Add(scheduled);
                        return Task.FromResult(scheduled);
                    }
                }

                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add(new Waiter(predicate, completion));
            }

            return completion.Task.WaitAsync(timeout);
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Value is not DisseminationBroadcastScheduledEvent scheduled)
            {
                return;
            }

            TaskCompletionSource<DisseminationBroadcastScheduledEvent>? completion = null;
            lock (_lock)
            {
                _events.Add(scheduled);
                for (var i = 0; i < _waiters.Count; i++)
                {
                    if (_waiters[i].Predicate(scheduled))
                    {
                        completion = _waiters[i].Completion;
                        _waiters.RemoveAt(i);
                        _consumed.Add(scheduled);
                        break;
                    }
                }
            }

            completion?.TrySetResult(scheduled);
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void Dispose() => _subscription.Dispose();

        private sealed record Waiter(
            Func<DisseminationBroadcastScheduledEvent, bool> Predicate,
            TaskCompletionSource<DisseminationBroadcastScheduledEvent> Completion);
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
