using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.Metadata;
using Orleans.Runtime;
using Xunit;

namespace UnitTests.Manifest;

public partial class ClusterManifestProviderTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task PeerRepair_LocalCompletionReleasesSlotsBeforeLateResponses(bool waitForUpdate, bool cancelCaller, bool lateFailure)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var localSilo = CreateSiloAddress(11111, 1);
        var peers = Enumerable.Range(11112, 4).Select(port => CreateSiloAddress(port, 1)).ToArray();
        using var membership = new TestClusterMembershipService(CreateActiveMembershipSnapshot(1, localSilo, peers));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeProvider = new FakeTimeProvider();
        var logger = new PeerProbeLogger(expectedTimeoutCount: 3);
        var requests = new ManifestRequestLog(expectedProbeCount: 6, expectedLegacyFetchCount: 8);
        var pendingSummary = new TaskCompletionSource<ClusterManifestHashSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingUpdate = new TaskCompletionSource<ClusterManifestUpdate?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allUpdatesEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateManifest = CreateGrainManifest();
        var lateHash = ManifestHashCalculator.ComputeHash(lateManifest);
        var summary = new ClusterManifestHashSummary(
            new MajorMinorVersion(1, 0),
            peers.ToDictionary(peer => peer, _ => lateHash));
        var lateUpdate = new ClusterManifestUpdate(
            new MajorMinorVersion(1, 0),
            peers.ToImmutableDictionary(peer => peer, _ => lateManifest),
            includesAllActiveServers: true);
        var updateRequests = 0;
        var recovered = false;
        GrainManifest recoveredManifest = null!;
        var targets = peers.ToDictionary(
            peer => peer,
            peer => new TestClusterManifestSystemTarget(
                getHashSummary: _ =>
                {
                    requests.RecordProbe(peer);
                    return waitForUpdate || recovered ? Task.FromResult(summary) : pendingSummary.Task;
                },
                getUpdate: (_, _) =>
                {
                    if (Interlocked.Increment(ref updateRequests) == 3)
                    {
                        allUpdatesEntered.TrySetResult();
                    }

                    return recovered ? Task.FromResult<ClusterManifestUpdate?>(null) : pendingUpdate.Task;
                },
                getLegacyManifest: _ =>
                {
                    requests.RecordLegacyFetch(peer);
                    return recovered ? Task.FromResult(recoveredManifest)
                        : Task.FromException<GrainManifest>(new InvalidOperationException("Direct fetch temporarily unavailable."));
                }));
        await using var provider = CreateClusterManifestProvider(localSilo, membership, CreateGrainFactory(targets), timeProvider, logger);
        recoveredManifest = provider.LocalGrainManifest;
        Assert.NotEqual(lateHash, ManifestHashCalculator.ComputeHash(recoveredManifest));
        var initial = provider.Current;
        var originalCache = GetCachedManifests(provider);
        await InitializeProviderAsync(provider, cancellationToken);
        try
        {
            var first = UpdateManifestAsync(provider, membership.CurrentSnapshot, cancellation.Token);
            await requests.WaitForProbeCountAsync(3, cancellationToken);
            if (waitForUpdate)
            {
                await allUpdatesEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }

            var atCapacity = ProbePeerAsync(provider, peers[0], peers, originalCache, cancellationToken);
            await atCapacity.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.Equal(3, requests.ProbeAddresses.Count);
            Assert.False(first.IsCompleted);

            if (cancelCaller)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
            }
            else
            {
                timeProvider.Advance(TimeSpan.FromSeconds(1));
                Assert.False(await first.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
            }

            Assert.Same(initial, provider.Current);
            recovered = true;
            membership.Update(CreateActiveMembershipSnapshot(2, localSilo, peers));
            var retry = UpdateManifestAsync(provider, membership.CurrentSnapshot, cancellationToken);
            Assert.True(await retry.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
            Assert.Equal(6, requests.ProbeAddresses.Count);
            Assert.Equal(8, requests.LegacyFetchAddresses.Count);
            Assert.Equal(5, provider.Current.Silos.Count);
            Assert.All(peers, peer => Assert.Same(recoveredManifest, provider.Current.Silos[peer]));
            Assert.False(waitForUpdate ? pendingUpdate.Task.IsCompleted : pendingSummary.Task.IsCompleted);
            var current = provider.Current;
            var currentCache = GetCachedManifests(provider);

            if (lateFailure)
            {
                if (waitForUpdate)
                {
                    pendingUpdate.SetException(new InvalidOperationException("Late update failure."));
                }
                else
                {
                    pendingSummary.SetException(new InvalidOperationException("Late summary failure."));
                }

                await logger.WaitForLateFailureCountAsync(3, cancellationToken);
            }
            else
            {
                pendingSummary.TrySetResult(summary);
                pendingUpdate.TrySetResult(lateUpdate);
                await Task.WhenAll(pendingSummary.Task, pendingUpdate.Task);
            }

            Assert.Same(current, provider.Current);
            Assert.Same(currentCache, GetCachedManifests(provider));
            Assert.False(originalCache.ContainsKey(lateHash));
            Assert.False(currentCache.ContainsKey(lateHash));
            Assert.Equal(6, requests.ProbeAddresses.Count);
        }
        finally
        {
            pendingSummary.TrySetResult(summary);
            pendingUpdate.TrySetResult(null);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PeerRepair_Cancellation_ReachesSummaryAndUpdateRpc(bool waitForUpdate, bool cancelCaller)
    {
        var localSilo = CreateSiloAddress(11111, 1);
        var remoteSilo = CreateSiloAddress(11112, 1);
        using var membership = new TestClusterMembershipService(CreateActiveMembershipSnapshot(1, localSilo, [remoteSilo]));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var timeProvider = new FakeTimeProvider();
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingSummary = new TaskCompletionSource<ClusterManifestHashSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingUpdate = new TaskCompletionSource<ClusterManifestUpdate?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var summary = new ClusterManifestHashSummary(new MajorMinorVersion(1, 0), new Dictionary<SiloAddress, ManifestHash>());
        var summaryToken = default(CancellationToken);
        var remote = Substitute.For<IClusterManifestSystemTarget>();
        remote.GetClusterManifestHashSummary(Arg.Any<CancellationToken>()).Returns(call =>
        {
            summaryToken = call.Arg<CancellationToken>();
            if (waitForUpdate)
            {
                return new ValueTask<ClusterManifestHashSummary>(summary);
            }

            entered.TrySetResult(summaryToken);
            return new ValueTask<ClusterManifestHashSummary>(pendingSummary.Task);
        });
        remote.GetClusterManifestUpdate(MajorMinorVersion.MinValue, Arg.Any<CancellationToken>()).Returns(call =>
        {
            entered.TrySetResult(call.Arg<CancellationToken>());
            return new ValueTask<ClusterManifestUpdate?>(pendingUpdate.Task);
        });
        var grainFactory = Substitute.For<IInternalGrainFactory>();
        grainFactory.GetSystemTarget<IClusterManifestSystemTarget>(Constants.ManifestProviderType, remoteSilo).Returns(remote);
        await using var provider = CreateClusterManifestProvider(localSilo, membership, grainFactory, timeProvider, NullLogger<ClusterManifestProvider>.Instance);
        await InitializeProviderAsync(provider, cancellation.Token);
        var probe = ProbePeerAsync(provider, remoteSilo, new[] { remoteSilo }, GetCachedManifests(provider), cancellation.Token);
        try
        {
            var rpcToken = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.True(rpcToken.CanBeCanceled);
            Assert.Equal(summaryToken, rpcToken);
            Assert.False(rpcToken.IsCancellationRequested);
            if (cancelCaller)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            }
            else
            {
                timeProvider.Advance(TimeSpan.FromSeconds(1));
                await probe.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                Assert.False(cancellation.IsCancellationRequested);
            }

            Assert.True(rpcToken.IsCancellationRequested);
        }
        finally
        {
            cancellation.Cancel();
            pendingSummary.TrySetResult(summary);
            pendingUpdate.TrySetResult(null);
        }
    }

    [Fact]
    public async Task PeerRepair_CanceledPeerProbe_UsesLegacyManifest()
    {
        var localSilo = CreateSiloAddress(11111, 1);
        var peers = new[] { CreateSiloAddress(11112, 1), CreateSiloAddress(11113, 1) };
        var remoteManifest = CreateGrainManifest();
        var targets = peers.ToDictionary(
            peer => peer,
            peer => new TestClusterManifestSystemTarget(
                getHashSummary: _ => Task.FromCanceled<ClusterManifestHashSummary>(new CancellationToken(canceled: true)),
                getUpdate: (_, _) => Task.FromResult<ClusterManifestUpdate?>(null),
                getLegacyManifest: _ => Task.FromResult(remoteManifest)));
        using var membership = new TestClusterMembershipService(CreateActiveMembershipSnapshot(1, localSilo, peers));
        await using var provider = CreateClusterManifestProvider(localSilo, membership, CreateGrainFactory(targets));
        var observed = ObserveManifestAsync(provider, new MajorMinorVersion(1, 1), TestContext.Current.CancellationToken);
        var lifecycle = await StartAsync(provider, TestContext.Current.CancellationToken);
        try
        {
            var current = await observed.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.All(peers, peer => Assert.Equal(remoteManifest, current.Silos[peer]));
        }
        finally
        {
            await lifecycle.OnStop(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task PeerRepair_StopCancellation_CompletesHungProbeProcessing()
    {
        var localSilo = CreateSiloAddress(11111, 1);
        var peers = Enumerable.Range(11112, 4).Select(port => CreateSiloAddress(port, 1)).OrderBy(static address => address).ToArray();
        var requestLog = new ManifestRequestLog(expectedProbeCount: 3, expectedLegacyFetchCount: peers.Length);
        var pendingSummary = new TaskCompletionSource<ClusterManifestHashSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingLegacyFetch = new TaskCompletionSource<GrainManifest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var targets = peers.ToDictionary(
            peer => peer,
            peer => new TestClusterManifestSystemTarget(
                getHashSummary: _ =>
                {
                    requestLog.RecordProbe(peer);
                    return pendingSummary.Task;
                },
                getUpdate: (_, _) => Task.FromResult<ClusterManifestUpdate?>(null),
                getLegacyManifest: _ =>
                {
                    requestLog.RecordLegacyFetch(peer);
                    return pendingLegacyFetch.Task;
                }));
        var grainFactory = CreateGrainFactory(targets);
        var membership = new TestClusterMembershipService(CreateActiveMembershipSnapshot(1, localSilo, peers));
        var provider = CreateClusterManifestProvider(localSilo, membership, grainFactory, new FakeTimeProvider(), NullLogger<ClusterManifestProvider>.Instance);
        var lifecycle = await StartAsync(provider, TestContext.Current.CancellationToken);
        var stopped = false;

        try
        {
            await Task.WhenAll(requestLog.WaitForProbeCountAsync(3, TestContext.Current.CancellationToken), requestLog.WaitForLegacyFetchCountAsync(peers.Length, TestContext.Current.CancellationToken));

            await lifecycle.OnStop(TestContext.Current.CancellationToken);
            stopped = true;

            Assert.Equal(GetExpectedProbePeers(localSilo, peers, round: 1), requestLog.ProbeAddresses);
            Assert.Equal(3, requestLog.ProbeAddresses.Count);
            Assert.DoesNotContain(peers[0], provider.Current.Silos.Keys);
        }
        finally
        {
            if (!stopped)
            {
                await lifecycle.OnStop(TestContext.Current.CancellationToken);
            }

            provider.Dispose();
            membership.Dispose();
        }
    }

    [Fact]
    public async Task ClusterManifestProviderPreservesCallerCancellationDuringPeerFill()
    {
        var localSilo = CreateSiloAddress(11801, 1);
        var peers = Enumerable.Range(11802, 2).Select(port => CreateSiloAddress(port, 1)).OrderBy(static address => address).ToArray();
        var requestLog = new ManifestRequestLog(expectedProbeCount: peers.Length, expectedLegacyFetchCount: peers.Length);
        var pendingSummary = new TaskCompletionSource<ClusterManifestHashSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingLegacyManifest = new TaskCompletionSource<GrainManifest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var targets = peers.ToDictionary(
            peer => peer,
            peer => new TestClusterManifestSystemTarget(
                getHashSummary: _ =>
                {
                    requestLog.RecordProbe(peer);
                    return pendingSummary.Task;
                },
                getUpdate: (_, _) => Task.FromResult<ClusterManifestUpdate?>(null),
                getLegacyManifest: _ =>
                {
                    requestLog.RecordLegacyFetch(peer);
                    return pendingLegacyManifest.Task;
                }));
        var grainFactory = CreateGrainFactory(targets);
        var membership = new TestClusterMembershipService(CreateActiveMembershipSnapshot(1, localSilo, peers));
        var provider = CreateClusterManifestProvider(localSilo, membership, grainFactory, new FakeTimeProvider(), NullLogger<ClusterManifestProvider>.Instance);
        var lifecycle = await StartAsync(provider, TestContext.Current.CancellationToken);
        var stopped = false;

        try
        {
            await Task.WhenAll(
                requestLog.WaitForProbeCountAsync(peers.Length, TestContext.Current.CancellationToken),
                requestLog.WaitForLegacyFetchCountAsync(peers.Length, TestContext.Current.CancellationToken));

            var legacyFetchCountBeforeStop = requestLog.LegacyFetchAddresses.Count;

            await lifecycle.OnStop(TestContext.Current.CancellationToken);
            stopped = true;

            Assert.Equal(peers.Length, requestLog.ProbeAddresses.Count);
            Assert.Equal(legacyFetchCountBeforeStop, requestLog.LegacyFetchAddresses.Count);
            Assert.DoesNotContain(peers[0], provider.Current.Silos.Keys);
            Assert.DoesNotContain(peers[1], provider.Current.Silos.Keys);
        }
        finally
        {
            if (!stopped)
            {
                await lifecycle.OnStop(TestContext.Current.CancellationToken);
            }

            pendingLegacyManifest.TrySetResult(CreateGrainManifest());
            pendingSummary.TrySetResult(new ClusterManifestHashSummary(new MajorMinorVersion(1, 0), []));
            provider.Dispose();
            membership.Dispose();
        }
    }
}
