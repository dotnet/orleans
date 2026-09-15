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
using Orleans.Runtime.Metadata;
using Xunit;

namespace UnitTests.Manifest;

public partial class ClusterManifestProviderTests
{
    [Fact]
    public async Task ConcurrentColdFetches_PublishCanonicalManifestAfterBodiesComplete()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var local = CreateSiloAddress(11111, 1);
        var peers = new[] { CreateSiloAddress(11112, 1), CreateSiloAddress(11113, 1) };
        using var membership = new TestClusterMembershipService(CreateActiveMembershipSnapshot(1, local, peers));
        var manifests = peers.Select(_ => CreateGrainManifest()).ToArray();
        var hash = ManifestHashCalculator.ComputeHash(manifests[0]);
        var bodies = peers.Select(_ => new TaskCompletionSource<GrainManifest?>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var requests = 0;
        var factory = Substitute.For<IInternalGrainFactory>();
        for (var index = 0; index < peers.Length; index++)
        {
            var body = bodies[index];
            var target = Substitute.For<IClusterManifestSystemTarget>();
            target.GetClusterManifestHashSummary(Arg.Any<CancellationToken>()).Returns(ValueTask.FromException<ClusterManifestHashSummary>(new NotSupportedException()));
            target.GetSiloManifestHash(Arg.Any<CancellationToken>()).Returns(new ValueTask<ManifestHash>(hash));
            target.GetSiloManifestByHash(hash, Arg.Any<CancellationToken>()).Returns(_ =>
            {
                requests++;
                return new ValueTask<GrainManifest?>(body.Task);
            });
            factory.GetSystemTarget<IClusterManifestSystemTarget>(Constants.ManifestProviderType, peers[index]).Returns(target);
        }

        await using var provider = CreateClusterManifestProvider(local, membership, factory);
        await InitializeProviderAsync(provider, cancellationToken);
        var update = UpdateManifestAsync(provider, membership.CurrentSnapshot, cancellationToken);
        try
        {
            Assert.Equal(2, requests);
            Assert.False(update.IsCompleted);
            bodies[0].SetResult(manifests[0]);
            Assert.DoesNotContain(peers[1], provider.Current.Silos.Keys);
            bodies[1].SetResult(manifests[1]);
            Assert.True(await update.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
            Assert.Equal(manifests[0], provider.Current.Silos[peers[0]]);
            Assert.Same(provider.Current.Silos[peers[0]], provider.Current.Silos[peers[1]]);
            Assert.Equal(2, GetCachedManifests(provider).Count);
        }
        finally
        {
            bodies[0].TrySetResult(manifests[0]);
            bodies[1].TrySetResult(manifests[1]);
        }
    }

    [Fact]
    public async Task CompletedDirectRetrieval_PublishesBeforeOptionalPeerDeadline()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var local = CreateSiloAddress(11111, 1);
        var peers = new[] { CreateSiloAddress(11112, 1), CreateSiloAddress(11113, 1) };
        var time = new FakeTimeProvider();
        using var metrics = new ManifestMetrics();
        var requests = new ManifestRequestLog(expectedProbeCount: 2, expectedLegacyFetchCount: 2);
        var pendingSummary = new TaskCompletionSource<ClusterManifestHashSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingDirect = new TaskCompletionSource<GrainManifest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokens = new List<CancellationToken>();
        var targets = peers.ToDictionary(peer => peer, peer => new TestClusterManifestSystemTarget(
            getHashSummary: token =>
            {
                tokens.Add(token);
                requests.RecordProbe(peer);
                return pendingSummary.Task;
            },
            getUpdate: (_, _) => throw new InvalidOperationException("The pending summary has not completed."),
            getLegacyManifest: _ =>
            {
                requests.RecordLegacyFetch(peer);
                return pendingDirect.Task;
            }));
        using var membership = new TestClusterMembershipService(CreateActiveMembershipSnapshot(1, local, peers));
        await using var provider = CreateClusterManifestProvider(
            local, membership, CreateGrainFactory(targets), time, NullLogger<ClusterManifestProvider>.Instance, metrics.Instruments);
        await InitializeProviderAsync(provider, cancellationToken);
        var before = time.GetTimestamp();
        var update = UpdateManifestAsync(provider, membership.CurrentSnapshot, cancellationToken);
        try
        {
            await requests.WaitForProbeCountAsync(2, cancellationToken);
            await requests.WaitForLegacyFetchCountAsync(2, cancellationToken);
            pendingDirect.SetResult(CreateGrainManifest());

            Assert.True(await update.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
            Assert.Equal(before, time.GetTimestamp());
            Assert.False(pendingSummary.Task.IsCompleted);
            Assert.Equal(3, provider.Current.Silos.Count);
            Assert.All(tokens, token => Assert.True(token.IsCancellationRequested));
            Assert.Equal(2, metrics.Sum(InstrumentNames.MANIFEST_PEER_PROBES, ("status", "canceled")));
            Assert.Empty(metrics.Find(InstrumentNames.MANIFEST_PEER_REPAIRS));
            var current = provider.Current;
            pendingSummary.SetResult(new ClusterManifestHashSummary(new MajorMinorVersion(1, 1), []));
            Assert.Same(current, provider.Current);
        }
        finally
        {
            pendingSummary.TrySetResult(new ClusterManifestHashSummary(new MajorMinorVersion(1, 0), []));
            pendingDirect.TrySetResult(CreateGrainManifest());
        }
    }

    [Fact]
    public async Task PeerRepairMetrics_CountOnlyPublishedMissingEntries()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var local = CreateSiloAddress(11111, 1);
        var peers = new[] { CreateSiloAddress(11112, 1), CreateSiloAddress(11113, 1) };
        using var metrics = new ManifestMetrics();
        var remoteManifest = CreateGrainManifest();
        var hash = ManifestHashCalculator.ComputeHash(remoteManifest);
        var summary = new ClusterManifestHashSummary(new MajorMinorVersion(1, 1), peers.ToDictionary(peer => peer, _ => hash));
        var remoteUpdate = new ClusterManifestUpdate(
            new MajorMinorVersion(1, 1), peers.ToImmutableDictionary(peer => peer, _ => remoteManifest), true);
        var targets = peers.ToDictionary(peer => peer, _ => new TestClusterManifestSystemTarget(
            getHashSummary: _ => Task.FromResult(summary),
            getUpdate: (_, _) => Task.FromResult<ClusterManifestUpdate?>(remoteUpdate),
            getLegacyManifest: _ => Task.FromException<GrainManifest>(new InvalidOperationException("Use peer repair."))));
        using var membership = new TestClusterMembershipService(CreateActiveMembershipSnapshot(1, local, peers));
        await using var provider = CreateClusterManifestProvider(
            local, membership, CreateGrainFactory(targets), new FakeTimeProvider(), NullLogger<ClusterManifestProvider>.Instance, metrics.Instruments);
        await InitializeProviderAsync(provider, cancellationToken);

        Assert.True(await UpdateManifestAsync(provider, membership.CurrentSnapshot, cancellationToken));
        Assert.Equal(2, metrics.Sum(InstrumentNames.MANIFEST_PEER_REPAIRS));
        Assert.Equal(2, metrics.Sum(InstrumentNames.MANIFEST_PEER_PROBES, ("status", "success")));
        Assert.Equal(2, metrics.Sum(InstrumentNames.MANIFEST_CACHE_LOOKUPS, ("result", "miss"), ("source", "peer")));
        Assert.All(peers, peer => Assert.Equal(remoteManifest, provider.Current.Silos[peer]));

        Assert.True(await UpdateManifestAsync(provider, membership.CurrentSnapshot, cancellationToken));
        Assert.Equal(2, metrics.Sum(InstrumentNames.MANIFEST_PEER_REPAIRS));
        Assert.All(metrics.Find(InstrumentNames.MANIFEST_PEER_REPAIRS), measurement => Assert.Empty(measurement.Tags));
    }
}
