using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Runtime.Metadata;
using Xunit;

namespace UnitTests.Manifest;

public partial class ClusterManifestProviderTests
{
    [Theory]
    [InlineData("error")]
    [InlineData("mismatch")]
    public async Task FallbackMetrics_DistinguishRpcErrorsAndMismatchedContent(string reason)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var metrics = new ManifestMetrics();
        var local = CreateSiloAddress(11111, 1);
        var peer = CreateSiloAddress(11112, 1);
        using var membership = new TestClusterMembershipService(CreateActiveMembershipSnapshot(1, local, [peer]));
        var remoteManifest = CreateGrainManifest();
        var advertised = new ManifestHash("mismatched-content");
        var remote = Substitute.For<IClusterManifestSystemTarget>();
        remote.GetSiloManifestHash(Arg.Any<CancellationToken>()).Returns(reason == "error"
            ? ValueTask.FromException<ManifestHash>(new NotSupportedException())
            : new ValueTask<ManifestHash>(advertised));
        remote.GetSiloManifestByHash(advertised, Arg.Any<CancellationToken>()).Returns(new ValueTask<GrainManifest?>(remoteManifest));
        var factory = CreateGrainFactory(peer, remoteManifest);
        factory.GetSystemTarget<IClusterManifestSystemTarget>(Constants.ManifestProviderType, peer).Returns(remote);
        await using var provider = CreateClusterManifestProvider(
            local, membership, factory, new FakeTimeProvider(), NullLogger<ClusterManifestProvider>.Instance, metrics.Instruments);
        await InitializeProviderAsync(provider, cancellationToken);

        Assert.True(await UpdateManifestAsync(provider, membership.CurrentSnapshot, cancellationToken));
        Assert.Equal(remoteManifest, provider.Current.Silos[peer]);
        var fallback = Assert.Single(metrics.Find(InstrumentNames.MANIFEST_FALLBACKS));
        Assert.Equal(1, fallback.Value);
        Assert.Equal(reason, Assert.Single(fallback.Tags).Value);
        Assert.Equal("success", Assert.Single(metrics.Find(InstrumentNames.MANIFEST_RETRIEVAL_DURATION)).Tags["status"]);
        Assert.False(GetCachedManifests(provider).ContainsKey(advertised));
    }

    [Fact]
    public async Task RetrievalMetrics_RecordCacheHitsMissesFallbackAndDuration()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var metrics = new ManifestMetrics();
        var time = new FakeTimeProvider();
        var local = CreateSiloAddress(11111, 1);
        var first = CreateSiloAddress(11112, 1);
        var second = CreateSiloAddress(11113, 1);
        using var membership = new TestClusterMembershipService(CreateActiveMembershipSnapshot(1, local, [first]));
        var remoteManifest = CreateGrainManifest();
        var hash = ManifestHashCalculator.ComputeHash(remoteManifest);
        var hashTarget = Substitute.For<IClusterManifestSystemTarget>();
        hashTarget.GetSiloManifestHash(Arg.Any<CancellationToken>()).Returns(new ValueTask<ManifestHash>(hash));
        hashTarget.GetSiloManifestByHash(hash, Arg.Any<CancellationToken>()).Returns(new ValueTask<GrainManifest?>((GrainManifest?)null));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<GrainManifest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var directTarget = Substitute.For<ISiloManifestSystemTarget>();
        directTarget.GetSiloManifest(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            entered.TrySetResult();
            return new ValueTask<GrainManifest>(release.Task);
        });
        var factory = Substitute.For<IInternalGrainFactory>();
        foreach (var peer in new[] { first, second })
        {
            factory.GetSystemTarget<IClusterManifestSystemTarget>(Constants.ManifestProviderType, peer).Returns(hashTarget);
            factory.GetSystemTarget<ISiloManifestSystemTarget>(Constants.ManifestProviderType, peer).Returns(directTarget);
        }

        await using var provider = CreateClusterManifestProvider(local, membership, factory, time, NullLogger<ClusterManifestProvider>.Instance, metrics.Instruments);
        await InitializeProviderAsync(provider, cancellationToken);
        try
        {
            var update = UpdateManifestAsync(provider, membership.CurrentSnapshot, cancellationToken);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            time.Advance(TimeSpan.FromMilliseconds(25));
            release.SetResult(remoteManifest);
            Assert.True(await update.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));

            membership.Update(CreateActiveMembershipSnapshot(2, local, [first, second]));
            Assert.True(await UpdateManifestAsync(provider, membership.CurrentSnapshot, cancellationToken));
            Assert.Single(directTarget.ReceivedCalls());
            Assert.Equal(1, metrics.Sum(InstrumentNames.MANIFEST_CACHE_LOOKUPS, ("result", "miss"), ("source", "silo")));
            Assert.Equal(1, metrics.Sum(InstrumentNames.MANIFEST_CACHE_LOOKUPS, ("result", "hit"), ("source", "silo")));
            Assert.Equal(1, metrics.Sum(InstrumentNames.MANIFEST_FALLBACKS, ("reason", "missing")));
            var durations = metrics.Find(InstrumentNames.MANIFEST_RETRIEVAL_DURATION);
            Assert.Equal(new[] { 25d, 0d }, durations.Select(measurement => measurement.Value));
            Assert.All(durations, measurement =>
            {
                Assert.Equal("ms", measurement.Unit);
                Assert.Equal("hash", measurement.Tags["mode"]);
                Assert.Equal("success", measurement.Tags["status"]);
                Assert.Equal(2, measurement.Tags.Count);
            });
            Assert.Empty(metrics.Find(InstrumentNames.MANIFEST_PEER_PROBES));
        }
        finally
        {
            release.TrySetResult(remoteManifest);
        }
    }

    [Theory]
    [InlineData("success")]
    [InlineData("error")]
    [InlineData("canceled")]
    public async Task RetrievalMetrics_RecordDirectOutcomeWithoutHashActivity(string outcome)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var metrics = new ManifestMetrics();
        var time = new FakeTimeProvider();
        var local = CreateSiloAddress(11111, 1);
        var peer = CreateSiloAddress(11112, 1);
        using var membership = new TestClusterMembershipService(CreateActiveMembershipSnapshot(1, local, [peer]));
        var release = new TaskCompletionSource<GrainManifest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var target = Substitute.For<ISiloManifestSystemTarget>();
        target.GetSiloManifest(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            entered.TrySetResult();
            return new ValueTask<GrainManifest>(release.Task);
        });
        var factory = Substitute.For<IInternalGrainFactory>();
        factory.GetSystemTarget<ISiloManifestSystemTarget>(Constants.ManifestProviderType, peer).Returns(target);
        await using var provider = CreateClusterManifestProvider(
            local, membership, factory, time, NullLogger<ClusterManifestProvider>.Instance,
            metrics.Instruments, new ClusterManifestOptions());
        await InitializeProviderAsync(provider, cancellationToken);
        var update = UpdateManifestAsync(provider, membership.CurrentSnapshot, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            time.Advance(TimeSpan.FromMilliseconds(12));
            switch (outcome)
            {
                case "success":
                    release.SetResult(CreateGrainManifest());
                    Assert.True(await update);
                    break;
                case "error":
                    release.SetException(new InvalidOperationException("Direct retrieval failed."));
                    Assert.False(await update);
                    break;
                case "canceled":
                    cancellation.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => update.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
                    break;
            }

            var duration = Assert.Single(metrics.Find(InstrumentNames.MANIFEST_RETRIEVAL_DURATION));
            Assert.Equal(12, duration.Value);
            Assert.Equal("ms", duration.Unit);
            Assert.Equal("direct", duration.Tags["mode"]);
            Assert.Equal(outcome, duration.Tags["status"]);
            Assert.Empty(metrics.Find(InstrumentNames.MANIFEST_CACHE_LOOKUPS));
            Assert.Empty(metrics.Find(InstrumentNames.MANIFEST_FALLBACKS));
            Assert.Empty(metrics.Find(InstrumentNames.MANIFEST_PEER_PROBES));
            Assert.Empty(metrics.Find(InstrumentNames.MANIFEST_PEER_REPAIRS));
        }
        finally
        {
            release.TrySetResult(CreateGrainManifest());
        }
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("canceled")]
    [InlineData("error")]
    [InlineData("success")]
    public async Task PeerMetrics_RecordLocalAttemptOutcomesAndAdmission(string outcome)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var metrics = new ManifestMetrics();
        var time = new FakeTimeProvider();
        var local = CreateSiloAddress(11111, 1);
        var peer = CreateSiloAddress(11112, 1);
        using var membership = new TestClusterMembershipService(CreateActiveMembershipSnapshot(1, local, [peer]));
        var pending = new TaskCompletionSource<ClusterManifestHashSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
        var target = Substitute.For<IClusterManifestSystemTarget>();
        target.GetClusterManifestHashSummary(Arg.Any<CancellationToken>()).Returns(new ValueTask<ClusterManifestHashSummary>(pending.Task));
        var factory = Substitute.For<IInternalGrainFactory>();
        factory.GetSystemTarget<IClusterManifestSystemTarget>(Constants.ManifestProviderType, peer).Returns(target);
        await using var provider = CreateClusterManifestProvider(local, membership, factory, time, NullLogger<ClusterManifestProvider>.Instance, metrics.Instruments);
        await InitializeProviderAsync(provider, cancellationToken);
        var cache = new ConcurrentDictionary<ManifestHash, GrainManifest>();
        var hash = ManifestHashCalculator.ComputeHash(provider.LocalGrainManifest);
        cache[hash] = provider.LocalGrainManifest;
        var probes = Enumerable.Range(0, 3).Select(_ => ProbePeerAsync(provider, peer, [peer], cache, cancellation.Token)).ToArray();
        try
        {
            await ProbePeerAsync(provider, peer, [peer], cache, cancellationToken);
            Assert.Equal(1, metrics.Sum(InstrumentNames.MANIFEST_PEER_PROBES, ("status", "skipped")));
            switch (outcome)
            {
                case "timeout":
                    time.Advance(TimeSpan.FromSeconds(1));
                    break;
                case "canceled":
                    cancellation.Cancel();
                    break;
                case "error":
                    pending.SetException(new NotSupportedException());
                    break;
                case "success":
                    pending.SetResult(new ClusterManifestHashSummary(new MajorMinorVersion(1, 0), new() { [peer] = hash }));
                    break;
            }

            if (outcome == "canceled")
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.WhenAll(probes));
            }
            else
            {
                await Task.WhenAll(probes);
            }

            Assert.Equal(3, metrics.Sum(InstrumentNames.MANIFEST_PEER_PROBES, ("status", outcome)));
            Assert.All(metrics.Find(InstrumentNames.MANIFEST_PEER_PROBES), measurement => Assert.Single(measurement.Tags));
            Assert.Empty(metrics.Find(InstrumentNames.MANIFEST_PEER_REPAIRS));
        }
        finally
        {
            pending.TrySetResult(new ClusterManifestHashSummary(new MajorMinorVersion(1, 0), []));
        }
    }

    private sealed class ManifestMetrics : IDisposable
    {
        private readonly ServiceProvider _services;
        private readonly MeterListener _listener = new();
        private readonly ConcurrentQueue<ManifestMeasurement> _measurements = new();

        public ManifestMetrics()
        {
            _services = new ServiceCollection().AddMetrics().AddSingleton<OrleansInstruments>().BuildServiceProvider();
            var orleans = _services.GetRequiredService<OrleansInstruments>();
            Instruments = new ClusterManifestInstruments(orleans);
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, orleans.Meter) && instrument.Name.StartsWith("orleans-manifest-", StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
            _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
            _listener.Start();
        }

        public ClusterManifestInstruments Instruments { get; }

        public ManifestMeasurement[] Find(string name) => _measurements.Where(measurement => measurement.Name == name).ToArray();

        public double Sum(string name, params (string Name, string Value)[] tags) =>
            Find(name).Where(measurement => tags.All(tag => Equals(measurement.Tags[tag.Name], tag.Value))).Sum(measurement => measurement.Value);

        private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags) =>
            _measurements.Enqueue(new(instrument.Name, instrument.Unit, value, tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value)));

        public void Dispose()
        {
            _listener.Dispose();
            _services.Dispose();
        }
    }

    private sealed record ManifestMeasurement(string Name, string? Unit, double Value, Dictionary<string, object?> Tags);
}
