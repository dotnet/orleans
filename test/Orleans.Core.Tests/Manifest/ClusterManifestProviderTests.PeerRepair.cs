using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Runtime.Metadata;
using Xunit;

namespace UnitTests.Manifest;

public partial class ClusterManifestProviderTests
{
    [Fact]
    public async Task PeerRepair_PartialResult_CompletesRemainingSilosWithoutWaitingForRepairedSiloFetch()
    {
        var localSilo = CreateSiloAddress(11111, 1);
        var peers = new[] { CreateSiloAddress(11112, 1), CreateSiloAddress(11113, 1) };
        var remoteManifest = CreateGrainManifest();
        var summary = new ClusterManifestHashSummary(
            new MajorMinorVersion(1, 1),
            new Dictionary<SiloAddress, ManifestHash> { [peers[0]] = ManifestHashCalculator.ComputeHash(remoteManifest) });
        var peerUpdate = new ClusterManifestUpdate(
            new MajorMinorVersion(1, 1),
            ImmutableDictionary<SiloAddress, GrainManifest>.Empty.Add(peers[0], remoteManifest),
            includesAllActiveServers: false);
        var pendingDirectFetch = new TaskCompletionSource<GrainManifest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var targets = peers.ToDictionary(
            peer => peer,
            peer => new TestClusterManifestSystemTarget(
                getHashSummary: _ => Task.FromResult(summary),
                getUpdate: (_, _) => Task.FromResult<ClusterManifestUpdate?>(peerUpdate),
                getLegacyManifest: _ => peer == peers[0] ? pendingDirectFetch.Task : Task.FromResult(remoteManifest)));
        using var membership = new TestClusterMembershipService(CreateActiveMembershipSnapshot(1, localSilo, peers));
        await using var provider = CreateClusterManifestProvider(localSilo, membership, CreateGrainFactory(targets));
        var observed = ObserveManifestAsync(provider, new MajorMinorVersion(1, 2), TestContext.Current.CancellationToken);
        var lifecycle = await StartAsync(provider, TestContext.Current.CancellationToken);
        try
        {
            var current = await observed.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.All(peers, peer => Assert.Equal(remoteManifest, current.Silos[peer]));
            Assert.False(pendingDirectFetch.Task.IsCompleted);
        }
        finally
        {
            await lifecycle.OnStop(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task PeerRepair_HungPeersAndHealthyLaterPeer_UsesAtMostThreeConcurrentProbesAndFakeOneSecondTimeout()
    {
        var localSilo = CreateSiloAddress(11111, 1);
        var peers = Enumerable.Range(11112, 4).Select(port => CreateSiloAddress(port, 1)).OrderBy(static address => address).ToArray();
        var timeProvider = new FakeTimeProvider();
        var requestLog = new ManifestRequestLog(expectedProbeCount: 3, expectedLegacyFetchCount: peers.Length);
        var logger = new PeerProbeLogger(expectedTimeoutCount: 2);
        var directFetchRelease = new TaskCompletionSource<GrainManifest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hungProbeCompletions = peers.ToDictionary(
            static peer => peer,
            static _ => new TaskCompletionSource<ClusterManifestHashSummary>(TaskCreationOptions.RunContinuationsAsynchronously));
        var selectedPeers = GetExpectedProbePeers(localSilo, peers, round: 1);
        var healthyPeer = selectedPeers[2];
        var remoteManifest = CreateGrainManifest();
        var remoteHashes = peers.ToDictionary(static peer => peer, _ => ManifestHashCalculator.ComputeHash(remoteManifest));
        var summary = new ClusterManifestHashSummary(new MajorMinorVersion(1, 1), remoteHashes);
        var update = new ClusterManifestUpdate(
            new MajorMinorVersion(1, 1),
            peers.ToImmutableDictionary(static peer => peer, _ => remoteManifest),
            includesAllActiveServers: true);
        var healthyUpdateRequested = new TaskCompletionSource<MajorMinorVersion>(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeProbeCount = 0;
        var maximumActiveProbeCount = 0;
        var targets = peers.ToDictionary(
            peer => peer,
            peer => new TestClusterManifestSystemTarget(
                getHashSummary: _ =>
                {
                    requestLog.RecordProbe(peer);
                    var active = Interlocked.Increment(ref activeProbeCount);
                    UpdateMaximum(ref maximumActiveProbeCount, active);
                    if (peer == healthyPeer)
                    {
                        Interlocked.Decrement(ref activeProbeCount);
                        return Task.FromResult(summary);
                    }

                    // This peer ignores RPC cancellation; its late completion is bounded by the test lifetime.
                    return AwaitProbeAsync(hungProbeCompletions[peer].Task, () => Interlocked.Decrement(ref activeProbeCount), TestContext.Current.CancellationToken);
                },
                getUpdate: (version, _) =>
                {
                    if (peer == healthyPeer)
                    {
                        healthyUpdateRequested.TrySetResult(version);
                    }

                    return Task.FromResult<ClusterManifestUpdate?>(update);
                },
                getLegacyManifest: _ =>
                {
                    requestLog.RecordLegacyFetch(peer);
                    return directFetchRelease.Task;
                }));
        var grainFactory = CreateGrainFactory(targets);
        var membership = new TestClusterMembershipService(CreateActiveMembershipSnapshot(1, localSilo, peers));
        var provider = CreateClusterManifestProvider(localSilo, membership, grainFactory, timeProvider, logger);
        var repairedManifest = ObserveManifestAsync(provider, new MajorMinorVersion(1, 1), TestContext.Current.CancellationToken);
        var lifecycle = await StartAsync(provider, TestContext.Current.CancellationToken);

        try
        {
            await Task.WhenAll(requestLog.WaitForProbeCountAsync(3, TestContext.Current.CancellationToken), requestLog.WaitForLegacyFetchCountAsync(peers.Length, TestContext.Current.CancellationToken), healthyUpdateRequested.Task);
            var requestedVersion = await healthyUpdateRequested.Task;

            Assert.Equal(selectedPeers, requestLog.ProbeAddresses);
            Assert.Equal(3, maximumActiveProbeCount);
            Assert.Equal(3, requestLog.ProbeAddresses.Count);
            Assert.Equal(MajorMinorVersion.MinValue, requestedVersion);

            timeProvider.Advance(TimeSpan.FromSeconds(1));
            await logger.WaitForTimeoutCountAsync(2, TestContext.Current.CancellationToken);
            Assert.Equal(2, logger.TimeoutCount);
            Assert.Equal(3, requestLog.ProbeAddresses.Count);

            foreach (var hungPeer in selectedPeers.Take(2))
            {
                hungProbeCompletions[hungPeer].TrySetException(
                    new InvalidOperationException($"Late peer probe failure from {hungPeer}."));
            }

            await logger.WaitForLateFailureCountAsync(2, TestContext.Current.CancellationToken);
            Assert.Equal(2, logger.LateFailureCount);

            var repaired = await repairedManifest;

            Assert.Equal(new MajorMinorVersion(1, 1), repaired.Version);
            Assert.All(peers, peer => Assert.Equal(remoteManifest, repaired.Silos[peer]));
            Assert.False(directFetchRelease.Task.IsCompleted);

            directFetchRelease.TrySetException(new InvalidOperationException("Peer repair already supplied the manifests."));
        }
        finally
        {
            await lifecycle.OnStop(TestContext.Current.CancellationToken);
            provider.Dispose();
            membership.Dispose();
        }
    }

    [Fact]
    public async Task PeerRepair_PartialResult_PublishesBeforeHungDirectFetchesComplete()
    {
        var localSilo = CreateSiloAddress(11111, 1);
        var peers = Enumerable.Range(11112, 3).Select(port => CreateSiloAddress(port, 1)).OrderBy(static address => address).ToArray();
        var repairedPeer = peers[0];
        var remoteManifest = CreateGrainManifest();
        var remoteHash = ManifestHashCalculator.ComputeHash(remoteManifest);
        var summary = new ClusterManifestHashSummary(
            new MajorMinorVersion(1, 1),
            new Dictionary<SiloAddress, ManifestHash> { [repairedPeer] = remoteHash });
        var update = new ClusterManifestUpdate(
            new MajorMinorVersion(1, 1),
            ImmutableDictionary<SiloAddress, GrainManifest>.Empty.Add(repairedPeer, remoteManifest),
            includesAllActiveServers: false);
        var pendingDirectFetch = new TaskCompletionSource<GrainManifest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestLog = new ManifestRequestLog(expectedProbeCount: peers.Length, expectedLegacyFetchCount: peers.Length);
        var targets = peers.ToDictionary(
            peer => peer,
            peer => new TestClusterManifestSystemTarget(
                getHashSummary: _ =>
                {
                    requestLog.RecordProbe(peer);
                    return Task.FromResult(summary);
                },
                getUpdate: (_, _) => Task.FromResult<ClusterManifestUpdate?>(update),
                getLegacyManifest: _ =>
                {
                    requestLog.RecordLegacyFetch(peer);
                    return pendingDirectFetch.Task;
                }));
        var grainFactory = CreateGrainFactory(targets);
        var membership = new TestClusterMembershipService(CreateActiveMembershipSnapshot(1, localSilo, peers));
        var provider = CreateClusterManifestProvider(
            localSilo,
            membership,
            grainFactory,
            new FakeTimeProvider(),
            NullLogger<ClusterManifestProvider>.Instance);
        var repairedManifest = ObserveManifestAsync(provider, new MajorMinorVersion(1, 1), TestContext.Current.CancellationToken);
        var lifecycle = await StartAsync(provider, TestContext.Current.CancellationToken);

        try
        {
            await Task.WhenAll(
                requestLog.WaitForProbeCountAsync(peers.Length, TestContext.Current.CancellationToken),
                requestLog.WaitForLegacyFetchCountAsync(peers.Length, TestContext.Current.CancellationToken));

            var repaired = await repairedManifest.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);

            Assert.Equal(remoteManifest, repaired.Silos[repairedPeer]);
            Assert.Contains(localSilo, repaired.Silos.Keys);
            Assert.DoesNotContain(peers.Skip(1), repaired.Silos.Keys.Contains);
            Assert.False(pendingDirectFetch.Task.IsCompleted);
        }
        finally
        {
            await lifecycle.OnStop(TestContext.Current.CancellationToken);
            provider.Dispose();
            membership.Dispose();
        }
    }

    [Fact]
    public async Task UpdateManifest_StartsLegacyFetchBeforeHungPeerProbesComplete()
    {
        var localSilo = CreateSiloAddress(11111, 1);
        var peers = Enumerable.Range(11112, 4).Select(port => CreateSiloAddress(port, 1)).OrderBy(static address => address).ToArray();
        var requestLog = new ManifestRequestLog(expectedProbeCount: 3, expectedLegacyFetchCount: peers.Length);
        var pendingSummary = new TaskCompletionSource<ClusterManifestHashSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
        var legacyFetchRelease = new TaskCompletionSource<GrainManifest>(TaskCreationOptions.RunContinuationsAsynchronously);
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
                    return legacyFetchRelease.Task;
                }));
        var grainFactory = CreateGrainFactory(targets);
        var membership = new TestClusterMembershipService(CreateActiveMembershipSnapshot(1, localSilo, peers));
        var provider = CreateClusterManifestProvider(localSilo, membership, grainFactory, new FakeTimeProvider(), NullLogger<ClusterManifestProvider>.Instance);
        var lifecycle = await StartAsync(provider, TestContext.Current.CancellationToken);

        try
        {
            await Task.WhenAll(requestLog.WaitForProbeCountAsync(3, TestContext.Current.CancellationToken), requestLog.WaitForLegacyFetchCountAsync(peers.Length, TestContext.Current.CancellationToken));

            Assert.Equal(3, requestLog.ProbeAddresses.Count);
            Assert.Equal(peers.Length, requestLog.LegacyFetchAddresses.Count);
            Assert.False(pendingSummary.Task.IsCompleted);

            legacyFetchRelease.TrySetResult(CreateGrainManifest());
        }
        finally
        {
            await lifecycle.OnStop(TestContext.Current.CancellationToken);
            provider.Dispose();
            membership.Dispose();
        }
    }

    [Fact]
    public async Task PeerRepair_RequestsUpdateFromMajorMinorVersionMinValue()
    {
        var localSilo = CreateSiloAddress(11111, 1);
        var peers = Enumerable.Range(11112, 3).Select(port => CreateSiloAddress(port, 1)).OrderBy(static address => address).ToArray();
        var requestLog = new ManifestRequestLog(expectedProbeCount: 3, expectedLegacyFetchCount: peers.Length);
        var directFetchRelease = new TaskCompletionSource<GrainManifest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var remoteManifest = CreateGrainManifest();
        var summary = new ClusterManifestHashSummary(
            new MajorMinorVersion(1, 1),
            peers.ToDictionary(static peer => peer, _ => ManifestHashCalculator.ComputeHash(remoteManifest)));
        var update = new ClusterManifestUpdate(
            new MajorMinorVersion(1, 1),
            peers.ToImmutableDictionary(static peer => peer, _ => remoteManifest),
            includesAllActiveServers: true);
        var requestedVersions = new List<MajorMinorVersion>();
        var updateRequestsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var targets = peers.ToDictionary(
            peer => peer,
            peer => new TestClusterManifestSystemTarget(
                getHashSummary: _ =>
                {
                    requestLog.RecordProbe(peer);
                    return Task.FromResult(summary);
                },
                getUpdate: (version, _) =>
                {
                    lock (requestedVersions)
                    {
                        requestedVersions.Add(version);
                        if (requestedVersions.Count == 3)
                        {
                            updateRequestsStarted.TrySetResult();
                        }
                    }

                    return Task.FromResult<ClusterManifestUpdate?>(update);
                },
                getLegacyManifest: _ =>
                {
                    requestLog.RecordLegacyFetch(peer);
                    return directFetchRelease.Task;
                }));
        var grainFactory = CreateGrainFactory(targets);
        var membership = new TestClusterMembershipService(CreateActiveMembershipSnapshot(1, localSilo, peers));
        var provider = CreateClusterManifestProvider(localSilo, membership, grainFactory, new FakeTimeProvider(), NullLogger<ClusterManifestProvider>.Instance);
        var repairedManifest = ObserveManifestAsync(provider, new MajorMinorVersion(1, 1), TestContext.Current.CancellationToken);
        var lifecycle = await StartAsync(provider, TestContext.Current.CancellationToken);

        try
        {
            await Task.WhenAll(requestLog.WaitForLegacyFetchCountAsync(peers.Length, TestContext.Current.CancellationToken), updateRequestsStarted.Task);

            lock (requestedVersions)
            {
                Assert.Equal(3, requestedVersions.Count);
                Assert.All(requestedVersions, version => Assert.Equal(MajorMinorVersion.MinValue, version));
            }

            directFetchRelease.TrySetException(new InvalidOperationException("Peer repair must supply the manifests after direct fetches fail."));
            var repaired = await repairedManifest;

            Assert.All(peers, peer => Assert.Equal(remoteManifest, repaired.Silos[peer]));
        }
        finally
        {
            await lifecycle.OnStop(TestContext.Current.CancellationToken);
            provider.Dispose();
            membership.Dispose();
        }
    }

    [Fact]
    public async Task PeerRepair_RepeatedMembershipUpdates_RotateObservedPeerSelections()
    {
        var localSilo = CreateSiloAddress(11111, 1);
        var peers = Enumerable.Range(11112, 4).Select(port => CreateSiloAddress(port, 1)).OrderBy(static address => address).ToArray();
        var requestLog = new ManifestRequestLog(expectedProbeCount: 6, expectedLegacyFetchCount: 0);
        var emptySummary = new ClusterManifestHashSummary(new MajorMinorVersion(1, 0), new Dictionary<SiloAddress, ManifestHash>());
        var emptyUpdate = new ClusterManifestUpdate(
            new MajorMinorVersion(1, 0),
            ImmutableDictionary<SiloAddress, GrainManifest>.Empty,
            includesAllActiveServers: false);
        var targets = peers.ToDictionary(
            peer => peer,
            peer => new TestClusterManifestSystemTarget(
                getHashSummary: _ =>
                {
                    requestLog.RecordProbe(peer);
                    return Task.FromResult(emptySummary);
                },
                getUpdate: (_, _) => Task.FromResult<ClusterManifestUpdate?>(emptyUpdate),
                getLegacyManifest: _ => Task.FromException<GrainManifest>(new InvalidOperationException("Direct fetch intentionally unavailable."))));
        var grainFactory = CreateGrainFactory(targets);
        var membership = new TestClusterMembershipService(CreateActiveMembershipSnapshot(1, localSilo, peers));
        var provider = CreateClusterManifestProvider(localSilo, membership, grainFactory, new FakeTimeProvider(), NullLogger<ClusterManifestProvider>.Instance);
        var lifecycle = await StartAsync(provider, TestContext.Current.CancellationToken);

        try
        {
            await requestLog.WaitForProbeCountAsync(3, TestContext.Current.CancellationToken);
            var secondAttemptStarted = requestLog.WaitForProbeCountAsync(6, TestContext.Current.CancellationToken);
            membership.Update(CreateActiveMembershipSnapshot(2, localSilo, peers));
            await secondAttemptStarted;

            var firstSelection = requestLog.ProbeAddresses.Take(3).ToArray();
            var secondSelection = requestLog.ProbeAddresses.Skip(3).Take(3).ToArray();

            Assert.Equal(3, firstSelection.Length);
            Assert.Equal(3, secondSelection.Length);
            Assert.NotEqual(firstSelection[0], secondSelection[0]);
            AssertContiguousCyclicSegment(peers, firstSelection);
            AssertContiguousCyclicSegment(peers, secondSelection);
        }
        finally
        {
            await lifecycle.OnStop(TestContext.Current.CancellationToken);
            provider.Dispose();
            membership.Dispose();
        }
    }

    [Fact]
    public async Task ClusterManifestProviderRejectsMismatchedPeerHashAndFallsBackToLegacyFetch()
    {
        var localSilo = CreateSiloAddress(11601, 1);
        var peers = Enumerable.Range(11602, 2).Select(port => CreateSiloAddress(port, 1)).OrderBy(static address => address).ToArray();
        var peerA = peers[0];
        var peerB = peers[1];
        var remoteManifestA = CreateGrainManifest();
        var remoteManifestB = new GrainManifest(
            ImmutableDictionary<GrainType, GrainProperties>.Empty,
            ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty);
        var legacyManifestB = new GrainManifest(
            ImmutableDictionary<GrainType, GrainProperties>.Empty.Add(
                TestGrainType,
                new GrainProperties(CreatePropertyDictionary(
                    new KeyValuePair<string, string>(WellKnownGrainTypeProperties.TypeName, "LegacyFallbackTest")))),
            ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty);
        // Use a fabricated hash that no other silo's manifest will ever validate against, so the
        // hash-cache reuse optimization cannot coincidentally "validate" peerB's mismatched claim.
        var mismatchedHashForB = new ManifestHash("intentionally-invalid-hash-for-peerB");
        Assert.NotEqual(mismatchedHashForB, ManifestHashCalculator.ComputeHash(remoteManifestA));
        Assert.NotEqual(mismatchedHashForB, ManifestHashCalculator.ComputeHash(remoteManifestB));

        var summary = new ClusterManifestHashSummary(
            new MajorMinorVersion(1, 1),
            new Dictionary<SiloAddress, ManifestHash>
            {
                [peerA] = ManifestHashCalculator.ComputeHash(remoteManifestA),
                [peerB] = mismatchedHashForB,
            });
        var update = new ClusterManifestUpdate(
            new MajorMinorVersion(1, 1),
            ImmutableDictionary<SiloAddress, GrainManifest>.Empty.Add(peerA, remoteManifestA).Add(peerB, remoteManifestB),
            includesAllActiveServers: true);

        var requestLog = new ManifestRequestLog(expectedProbeCount: peers.Length, expectedLegacyFetchCount: peers.Length);
        var targets = peers.ToDictionary(
            peer => peer,
            peer => new TestClusterManifestSystemTarget(
                getHashSummary: _ =>
                {
                    requestLog.RecordProbe(peer);
                    return Task.FromResult(summary);
                },
                getUpdate: (_, _) => Task.FromResult<ClusterManifestUpdate?>(update),
                getLegacyManifest: _ =>
                {
                    requestLog.RecordLegacyFetch(peer);
                    return Task.FromResult(peer.Equals(peerB) ? legacyManifestB : remoteManifestA);
                }));
        var grainFactory = CreateGrainFactory(targets);
        var membership = new TestClusterMembershipService(CreateActiveMembershipSnapshot(1, localSilo, peers));
        var provider = CreateClusterManifestProvider(localSilo, membership, grainFactory, new FakeTimeProvider(), NullLogger<ClusterManifestProvider>.Instance);
        var repairedManifest = ObserveManifestAsync(provider, new MajorMinorVersion(1, 2), TestContext.Current.CancellationToken);
        var lifecycle = await StartAsync(provider, TestContext.Current.CancellationToken);

        try
        {
            var repaired = await repairedManifest.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // The mismatched hash advertised for peerB must be rejected: its peer-supplied manifest never
            // enters the published result, and the legacy direct fetch is what ultimately resolves it.
            Assert.Same(legacyManifestB, repaired.Silos[peerB]);
            Assert.NotEqual(ManifestHashCalculator.ComputeHash(remoteManifestB), ManifestHashCalculator.ComputeHash(repaired.Silos[peerB]));
            Assert.Equal(ManifestHashCalculator.ComputeHash(remoteManifestA), ManifestHashCalculator.ComputeHash(repaired.Silos[peerA]));
            Assert.Contains(peerB, requestLog.LegacyFetchAddresses);
        }
        finally
        {
            await lifecycle.OnStop(TestContext.Current.CancellationToken);
            provider.Dispose();
            membership.Dispose();
        }
    }

    [Fact]
    public async Task ClusterManifestProviderRotatesBoundedPeerProbesBeforeLegacyFallback()
    {
        var localSilo = CreateSiloAddress(11701, 1);
        var peers = Enumerable.Range(11702, 4).Select(port => CreateSiloAddress(port, 1)).OrderBy(static address => address).ToArray();
        var timeProvider = new FakeTimeProvider();
        var requestLog = new ManifestRequestLog(expectedProbeCount: 3, expectedLegacyFetchCount: peers.Length);
        var logger = new PeerProbeLogger(expectedTimeoutCount: 3);
        var expectedProbedPeers = GetExpectedProbePeers(localSilo, peers, round: 1);
        var unprobedPeer = peers.Except(expectedProbedPeers).Single();
        var hungProbeCompletions = peers.ToDictionary(
            static peer => peer,
            static _ => new TaskCompletionSource<ClusterManifestHashSummary>(TaskCreationOptions.RunContinuationsAsynchronously));
        var directCompletions = peers.ToDictionary(
            static peer => peer,
            static _ => new TaskCompletionSource<GrainManifest>(TaskCreationOptions.RunContinuationsAsynchronously));
        // Each peer's fallback manifest must be structurally distinct: ClusterManifest canonicalizes
        // (deduplicates) structurally-equal GrainManifest instances within a single published manifest,
        // which would otherwise make different peers' entries reference-equal regardless of provenance.
        var legacyManifests = peers.ToDictionary(
            peer => peer,
            peer => new GrainManifest(
                ImmutableDictionary<GrainType, GrainProperties>.Empty.Add(
                    TestGrainType,
                    new GrainProperties(CreatePropertyDictionary(
                        new KeyValuePair<string, string>(WellKnownGrainTypeProperties.TypeName, $"LegacyFallbackTest-{peer}")))),
                ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty));
        var targets = peers.ToDictionary(
            peer => peer,
            peer => new TestClusterManifestSystemTarget(
                getHashSummary: _ =>
                {
                    requestLog.RecordProbe(peer);
                    return hungProbeCompletions[peer].Task;
                },
                getUpdate: (_, _) => Task.FromResult<ClusterManifestUpdate?>(null),
                getLegacyManifest: _ =>
                {
                    requestLog.RecordLegacyFetch(peer);
                    return directCompletions[peer].Task;
                }));
        var grainFactory = CreateGrainFactory(targets);
        var membership = new TestClusterMembershipService(CreateActiveMembershipSnapshot(1, localSilo, peers));
        var provider = CreateClusterManifestProvider(localSilo, membership, grainFactory, timeProvider, logger);
        var repairedManifest = ObserveManifestAsync(provider, new MajorMinorVersion(1, 1), TestContext.Current.CancellationToken);
        var lifecycle = await StartAsync(provider, TestContext.Current.CancellationToken);

        try
        {
            await Task.WhenAll(requestLog.WaitForProbeCountAsync(3, TestContext.Current.CancellationToken), requestLog.WaitForLegacyFetchCountAsync(peers.Length, TestContext.Current.CancellationToken));

            // Peer probing is bounded to at most three concurrent probes and selects the exact rotating,
            // contiguous cyclic segment of candidates; the fourth peer is never probed.
            Assert.Equal(expectedProbedPeers, requestLog.ProbeAddresses);
            Assert.Equal(3, requestLog.ProbeAddresses.Count);
            Assert.DoesNotContain(unprobedPeer, requestLog.ProbeAddresses);
            AssertContiguousCyclicSegment(peers, requestLog.ProbeAddresses);

            timeProvider.Advance(TimeSpan.FromSeconds(1));
            await logger.WaitForTimeoutCountAsync(3, TestContext.Current.CancellationToken);
            Assert.Equal(3, logger.TimeoutCount);

            foreach (var peer in peers)
            {
                directCompletions[peer].SetResult(legacyManifests[peer]);
            }

            var repaired = await repairedManifest.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // Since every bounded probe timed out, the fallback direct fetch is what supplies every silo,
            // including the peer that the bounded rotation never selected for probing.
            Assert.All(peers, peer => Assert.Same(legacyManifests[peer], repaired.Silos[peer]));
        }
        finally
        {
            await lifecycle.OnStop(TestContext.Current.CancellationToken);
            foreach (var peer in peers)
            {
                directCompletions[peer].TrySetResult(legacyManifests[peer]);
                hungProbeCompletions[peer].TrySetResult(new ClusterManifestHashSummary(new MajorMinorVersion(1, 0), []));
            }

            provider.Dispose();
            membership.Dispose();
        }
    }

    private static SiloAddress[] GetExpectedProbePeers(SiloAddress localSilo, SiloAddress[] peers, int round)
    {
        var start = (int)((uint)(localSilo.GetConsistentHashCode() + round) % (uint)peers.Length);
        return Enumerable.Range(0, Math.Min(3, peers.Length))
            .Select(index => peers[(start + index) % peers.Length])
            .ToArray();
    }

    private static async Task<ClusterManifestHashSummary> AwaitProbeAsync(
        Task<ClusterManifestHashSummary> task,
        Action onCompleted,
        CancellationToken cancellationToken)
    {
        try
        {
            return await task.WaitAsync(cancellationToken);
        }
        finally
        {
            onCompleted();
        }
    }

    private static void UpdateMaximum(ref int maximum, int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref maximum);
            if (current >= value || Interlocked.CompareExchange(ref maximum, value, current) == current)
            {
                return;
            }
        }
    }

    private static void AssertContiguousCyclicSegment(IReadOnlyList<SiloAddress> candidates, IReadOnlyList<SiloAddress> selection)
    {
        var start = Array.IndexOf(candidates.ToArray(), selection[0]);
        Assert.NotEqual(-1, start);
        Assert.Equal(
            selection,
            Enumerable.Range(0, selection.Count).Select(index => candidates[(start + index) % candidates.Count]));
    }
}
