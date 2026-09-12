using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Metadata;
using Orleans.Runtime;
using TestExtensions;
using Xunit;

namespace UnitTests.Manifest;

public partial class ClusterManifestProviderTests
{
    private static readonly FieldInfo MemoizedHashesField = typeof(ManifestHashCalculator)
        .GetField("Hashes", BindingFlags.Static | BindingFlags.NonPublic)!;

    [Fact]
    public async Task ClientProvider_UpdateCancellation_DoesNotFetchLegacyManifest()
    {
        var provider = (ClientClusterManifestProvider)RuntimeHelpers.GetUninitializedObject(
            typeof(ClientClusterManifestProvider));
        var remoteProvider = Substitute.For<IClusterManifestSystemTarget>();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        remoteProvider
            .GetClusterManifestUpdate(default, cancellation.Token)
            .Returns(_ => new ValueTask<ClusterManifestUpdate?>(
                Task.FromCanceled<ClusterManifestUpdate?>(cancellation.Token)));
        var method = typeof(ClientClusterManifestProvider).GetMethod(
            "GetClusterManifestUpdate",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task<ClusterManifestUpdate?>)method.Invoke(
            provider,
            [remoteProvider, default(MajorMinorVersion), cancellation.Token])!;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        var call = Assert.Single(remoteProvider.ReceivedCalls());
        Assert.Equal(nameof(IClusterManifestSystemTarget.GetClusterManifestUpdate), call.GetMethodInfo().Name);
    }

    [Fact]
    public void ManifestRpcTargetsHonorPreCanceledTokens()
    {
        var target = (ClusterManifestSystemTarget)RuntimeHelpers.GetUninitializedObject(typeof(ClusterManifestSystemTarget));
        var cancellationToken = new CancellationToken(canceled: true);

        Assert.Throws<OperationCanceledException>(() => { _ = target.GetClusterManifestHashSummary(cancellationToken); });
        Assert.Throws<OperationCanceledException>(() => { _ = target.GetSiloManifestHash(cancellationToken); });
        Assert.Throws<OperationCanceledException>(() => { _ = target.GetSiloManifestByHash(default, cancellationToken); });
    }

    [Fact]
    public async Task DefaultOptions_RetrieveDirectlyWithoutHashesOrPeerRepair()
    {
        var localSilo = CreateSiloAddress(11111, 1);
        var peers = new[] { CreateSiloAddress(11112, 1), CreateSiloAddress(11113, 1) };
        var snapshot = CreateActiveMembershipSnapshot(1, localSilo, peers);
        using var membership = new TestClusterMembershipService(snapshot);
        var grainFactory = Substitute.For<IInternalGrainFactory>();
        var remoteManifest = CreateGrainManifest();
        var legacy = Substitute.For<ISiloManifestSystemTarget>();
        legacy.GetSiloManifest(Arg.Any<CancellationToken>()).Returns(new ValueTask<GrainManifest>(remoteManifest));
        foreach (var peer in peers)
        {
            grainFactory.GetSystemTarget<ISiloManifestSystemTarget>(Constants.ManifestProviderType, peer).Returns(legacy);
        }

        var options = new ClusterManifestOptions();
        Assert.False(options.EnableContentAddressedRetrieval);
        await using var provider = CreateClusterManifestProvider(localSilo, membership, grainFactory, options);
        var memoizedHashes = GetMemoizedManifestHashes();

        Assert.False(memoizedHashes.TryGetValue(provider.LocalGrainManifest, out _));
        Assert.Null(GetCachedManifests(provider));

        // Runtime settings are captured at construction, even if the options object is later changed.
        options.EnableContentAddressedRetrieval = true;
        await InitializeProviderAsync(provider, TestContext.Current.CancellationToken);
        Assert.True(await UpdateManifestAsync(provider, snapshot, TestContext.Current.CancellationToken));

        Assert.Equal(new MajorMinorVersion(1, 1), provider.Current.Version);
        Assert.All(peers, peer => Assert.Same(remoteManifest, provider.Current.Silos[peer]));
        Assert.Equal(2, legacy.ReceivedCalls().Count());
        Assert.All(legacy.ReceivedCalls(), call => Assert.Equal(TestContext.Current.CancellationToken, call.GetArguments()[0]));
        Assert.DoesNotContain(grainFactory.ReceivedCalls(), call =>
            call.GetMethodInfo().GetGenericArguments().Contains(typeof(IClusterManifestSystemTarget)));
        Assert.Null(GetCachedManifests(provider));
        Assert.False(memoizedHashes.TryGetValue(provider.LocalGrainManifest, out _));
        Assert.False(memoizedHashes.TryGetValue(remoteManifest, out _));
        Assert.Equal(0, typeof(ClusterManifestProvider).GetField("_peerProbeRound", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(provider));

        membership.Update(CreateMembershipSnapshot(2, (localSilo, SiloStatus.Active)));
        Assert.Equal(new MajorMinorVersion(2, 0), provider.Current.Version);
        Assert.Equal(localSilo, Assert.Single(provider.Current.Silos).Key);
        Assert.Null(GetCachedManifests(provider));
        Assert.False(memoizedHashes.TryGetValue(provider.LocalGrainManifest, out _));
    }

    [Fact]
    public async Task DefaultOptions_CallerCancellationStopsDirectFetch()
    {
        var localSilo = CreateSiloAddress(11111, 1);
        var remoteSilo = CreateSiloAddress(11112, 1);
        var snapshot = CreateActiveMembershipSnapshot(1, localSilo, [remoteSilo]);
        using var membership = new TestClusterMembershipService(snapshot);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pending = new TaskCompletionSource<GrainManifest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var remote = Substitute.For<ISiloManifestSystemTarget>();
        remote.GetSiloManifest(Arg.Any<CancellationToken>()).Returns(call =>
        {
            entered.SetResult(call.Arg<CancellationToken>());
            return new ValueTask<GrainManifest>(pending.Task);
        });
        var grainFactory = Substitute.For<IInternalGrainFactory>();
        grainFactory.GetSystemTarget<ISiloManifestSystemTarget>(Constants.ManifestProviderType, remoteSilo).Returns(remote);
        await using var provider = CreateClusterManifestProvider(localSilo, membership, grainFactory, new ClusterManifestOptions());
        await InitializeProviderAsync(provider, cancellation.Token);
        var update = UpdateManifestAsync(provider, snapshot, cancellation.Token);
        try
        {
            Assert.Equal(cancellation.Token, await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => update.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.DoesNotContain(remoteSilo, provider.Current.Silos.Keys);
            Assert.Null(GetCachedManifests(provider));
        }
        finally
        {
            pending.TrySetResult(CreateGrainManifest());
        }
    }

    [Fact]
    public async Task DefaultServer_ComputesHashesOnDemandAndCachesSummaryByVersion()
    {
        var localSilo = CreateSiloAddress(11111, 1);
        using var membership = new TestClusterMembershipService(CreateMembershipSnapshot(1, (localSilo, SiloStatus.Active)));
        var grainFactory = Substitute.For<IInternalGrainFactory>();
        await using var provider = CreateClusterManifestProvider(localSilo, membership, grainFactory, new ClusterManifestOptions());
        using var services = new ServiceCollection()
            .AddMetrics()
            .AddSingleton<OrleansInstruments>()
            .AddSingleton<CatalogInstruments>()
            .AddSingleton<SchedulerInstruments>()
            .AddSingleton<GrainInstruments>()
            .AddSingleton<MessagingInstruments>()
            .AddSingleton<MessagingProcessingInstruments>()
            .BuildServiceProvider();
        var localSiloDetails = Substitute.For<ILocalSiloDetails>();
        localSiloDetails.SiloAddress.Returns(localSilo);
        var shared = new SystemTargetShared(
            runtimeClient: null!,
            localSiloDetails,
            NullLoggerFactory.Instance,
            Options.Create(new SchedulingOptions()),
            grainReferenceActivator: null!,
            timerRegistry: null!,
            new ActivationDirectory(services.GetRequiredService<CatalogInstruments>()),
            services.GetRequiredService<SchedulerInstruments>(),
            services.GetRequiredService<GrainInstruments>(),
            services.GetRequiredService<MessagingInstruments>(),
            services.GetRequiredService<MessagingProcessingInstruments>());
        using var target = new ClusterManifestSystemTarget(membership, provider, shared);
        var memoizedHashes = GetMemoizedManifestHashes();

        Assert.False(memoizedHashes.TryGetValue(provider.LocalGrainManifest, out _));
        Assert.Same(provider.LocalGrainManifest, await target.GetSiloManifest(TestContext.Current.CancellationToken));
        Assert.Same(provider.Current, await target.GetClusterManifest(TestContext.Current.CancellationToken));
        Assert.False(memoizedHashes.TryGetValue(provider.LocalGrainManifest, out _));

        var hash = await target.GetSiloManifestHash(TestContext.Current.CancellationToken);
        Assert.True(memoizedHashes.TryGetValue(provider.LocalGrainManifest, out var memoized));
        Assert.Equal(memoized.Value, hash);
        Assert.Same(provider.LocalGrainManifest, await target.GetSiloManifestByHash(hash, TestContext.Current.CancellationToken));
        Assert.Null(await target.GetSiloManifestByHash(new ManifestHash("mismatch"), TestContext.Current.CancellationToken));
        var summary = await target.GetClusterManifestHashSummary(TestContext.Current.CancellationToken);
        Assert.Equal(new MajorMinorVersion(1, 0), summary.Version);
        Assert.Equal(hash, Assert.Single(summary.SiloManifestHashes).Value);
        Assert.Same(summary, await target.GetClusterManifestHashSummary(TestContext.Current.CancellationToken));

        membership.Update(CreateMembershipSnapshot(2, (localSilo, SiloStatus.ShuttingDown)));
        var updated = await target.GetClusterManifestHashSummary(TestContext.Current.CancellationToken);
        Assert.NotSame(summary, updated);
        Assert.Equal(new MajorMinorVersion(2, 0), updated.Version);
        Assert.Empty(updated.SiloManifestHashes);
        Assert.Null(GetCachedManifests(provider));
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("body")]
    [InlineData("legacy")]
    public async Task DirectFetch_CallerCancellation_ReachesRpcAndStopsWaiting(string phase)
    {
        var localSilo = CreateSiloAddress(11111, 1);
        var remoteSilo = CreateSiloAddress(11112, 1);
        var snapshot = CreateActiveMembershipSnapshot(1, localSilo, [remoteSilo]);
        using var membership = new TestClusterMembershipService(snapshot);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingHash = new TaskCompletionSource<ManifestHash>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingBody = new TaskCompletionSource<GrainManifest?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingLegacy = new TaskCompletionSource<GrainManifest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var remoteManifest = CreateGrainManifest();
        var hash = ManifestHashCalculator.ComputeHash(remoteManifest);
        var remote = Substitute.For<IClusterManifestSystemTarget>();
        remote.GetSiloManifestHash(Arg.Any<CancellationToken>()).Returns(call =>
        {
            if (phase == "hash")
            {
                entered.TrySetResult(call.Arg<CancellationToken>());
                return new ValueTask<ManifestHash>(pendingHash.Task);
            }

            return phase == "legacy" ? ValueTask.FromException<ManifestHash>(new NotSupportedException()) : new ValueTask<ManifestHash>(hash);
        });
        remote.GetSiloManifestByHash(hash, Arg.Any<CancellationToken>()).Returns(call =>
        {
            entered.TrySetResult(call.Arg<CancellationToken>());
            return new ValueTask<GrainManifest?>(pendingBody.Task);
        });
        var legacy = Substitute.For<ISiloManifestSystemTarget>();
        legacy.GetSiloManifest(Arg.Any<CancellationToken>()).Returns(call =>
        {
            entered.TrySetResult(call.Arg<CancellationToken>());
            return new ValueTask<GrainManifest>(pendingLegacy.Task);
        });
        var grainFactory = Substitute.For<IInternalGrainFactory>();
        grainFactory.GetSystemTarget<IClusterManifestSystemTarget>(Constants.ManifestProviderType, remoteSilo).Returns(remote);
        grainFactory.GetSystemTarget<ISiloManifestSystemTarget>(Constants.ManifestProviderType, remoteSilo).Returns(legacy);
        await using var provider = CreateClusterManifestProvider(localSilo, membership, grainFactory);
        await InitializeProviderAsync(provider, cancellation.Token);
        var update = UpdateManifestAsync(provider, snapshot, cancellation.Token);
        try
        {
            var rpcToken = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(cancellation.Token, rpcToken);
            cancellation.Cancel();

            Assert.True(rpcToken.IsCancellationRequested);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => update.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.DoesNotContain(remoteSilo, provider.Current.Silos.Keys);
            if (phase != "legacy")
            {
                Assert.Empty(legacy.ReceivedCalls());
            }
        }
        finally
        {
            cancellation.Cancel();
            pendingHash.TrySetResult(hash);
            pendingBody.TrySetResult(remoteManifest);
            pendingLegacy.TrySetResult(remoteManifest);
        }
    }

    [Fact]
    public async Task UpdateManifest_ReusesVerifiedManifestBeforePublication()
    {
        var localSilo = CreateSiloAddress(11111, 1);
        var peers = new[] { CreateSiloAddress(11112, 1), CreateSiloAddress(11113, 1), CreateSiloAddress(11114, 1) };
        var snapshot = CreateActiveMembershipSnapshot(1, localSilo, peers);
        using var membership = new TestClusterMembershipService(snapshot);
        var grainFactory = Substitute.For<IInternalGrainFactory>();
        var remoteManifest = CreateGrainManifest();
        var remoteHash = ManifestHashCalculator.ComputeHash(remoteManifest);
        var firstTarget = new CanonicalManifestCacheTarget(remoteHash, remoteManifest);
        var secondTarget = new CanonicalManifestCacheTarget(remoteHash, remoteManifest);
        grainFactory.GetSystemTarget<IClusterManifestSystemTarget>(Constants.ManifestProviderType, peers[0])
            .Returns(firstTarget);
        grainFactory.GetSystemTarget<IClusterManifestSystemTarget>(Constants.ManifestProviderType, peers[1])
            .Returns(secondTarget);
        var pendingHash = new TaskCompletionSource<ManifestHash>(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowTarget = Substitute.For<IClusterManifestSystemTarget>();
        slowTarget.GetClusterManifestHashSummary(Arg.Any<CancellationToken>()).Returns(ValueTask.FromException<ClusterManifestHashSummary>(new NotSupportedException()));
        slowTarget.GetSiloManifestHash(Arg.Any<CancellationToken>()).Returns(new ValueTask<ManifestHash>(pendingHash.Task));
        grainFactory.GetSystemTarget<IClusterManifestSystemTarget>(Constants.ManifestProviderType, peers[2])
            .Returns(slowTarget);
        await using var provider = CreateClusterManifestProvider(localSilo, membership, grainFactory);
        var localHash = ManifestHashCalculator.ComputeHash(provider.LocalGrainManifest);
        Assert.Same(provider.LocalGrainManifest, Assert.Single(GetCachedManifests(provider)).Value);
        Assert.NotEqual(localHash, remoteHash);
        var initialManifest = provider.Current;
        var cache = GetCachedManifests(provider);
        await InitializeProviderAsync(provider, TestContext.Current.CancellationToken);
        var update = UpdateManifestAsync(provider, snapshot, TestContext.Current.CancellationToken);

        Assert.False(update.IsCompleted);
        Assert.Same(initialManifest, provider.Current);
        Assert.Same(cache, GetCachedManifests(provider));
        Assert.Same(remoteManifest, cache[remoteHash]);
        Assert.Equal(1, firstTarget.HashRequests);
        Assert.Equal(1, secondTarget.HashRequests);
        Assert.Equal(1, firstTarget.ManifestByHashRequests + secondTarget.ManifestByHashRequests);
        Assert.Equal(0, firstTarget.LegacyManifestRequests + secondTarget.LegacyManifestRequests);

        pendingHash.SetResult(localHash);
        Assert.True(await update.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.NotSame(cache, GetCachedManifests(provider));
        Assert.Equal(new MajorMinorVersion(1, 1), provider.Current.Version);
        Assert.Equal(remoteManifest, provider.Current.Silos[peers[0]]);
        Assert.Equal(remoteManifest, provider.Current.Silos[peers[1]]);
    }

    [Fact]
    public async Task UpdateManifest_StaleFetchCannotPopulateCurrentCache()
    {
        var localSilo = CreateSiloAddress(11111, 1);
        var remoteSilo = CreateSiloAddress(11112, 1);
        var snapshot = CreateActiveMembershipSnapshot(1, localSilo, [remoteSilo]);
        using var membership = new TestClusterMembershipService(snapshot);
        var grainFactory = Substitute.For<IInternalGrainFactory>();
        var remoteManifest = CreateGrainManifest();
        var remoteTarget = Substitute.For<IClusterManifestSystemTarget>();
        var pendingManifest = new TaskCompletionSource<GrainManifest?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fetchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hash = ManifestHashCalculator.ComputeHash(remoteManifest);
        remoteTarget.GetSiloManifestHash(Arg.Any<CancellationToken>()).Returns(new ValueTask<ManifestHash>(hash));
        remoteTarget.GetSiloManifestByHash(hash, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            fetchStarted.TrySetResult();
            return new ValueTask<GrainManifest?>(pendingManifest.Task);
        });
        grainFactory.GetSystemTarget<IClusterManifestSystemTarget>(Constants.ManifestProviderType, remoteSilo)
            .Returns(remoteTarget);
        await using var provider = CreateClusterManifestProvider(localSilo, membership, grainFactory);
        Assert.Equal(new MajorMinorVersion(1, 0), provider.Current.Version);
        await InitializeProviderAsync(provider, TestContext.Current.CancellationToken);
        var update = UpdateManifestAsync(provider, snapshot, TestContext.Current.CancellationToken);
        await fetchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var fetchCache = GetCachedManifests(provider);

        membership.Update(CreateMembershipSnapshot(2, (localSilo, SiloStatus.Active)));
        Assert.Equal(new MajorMinorVersion(2, 0), provider.Current.Version);
        pendingManifest.SetResult(remoteManifest);

        Assert.False(await update.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.DoesNotContain(remoteSilo, provider.Current.Silos.Keys);
        Assert.Same(remoteManifest, fetchCache[hash]);
        Assert.NotSame(fetchCache, GetCachedManifests(provider));
        Assert.Same(provider.LocalGrainManifest, Assert.Single(GetCachedManifests(provider)).Value);
    }

    [Fact]
    public async Task DirectFetch_CanceledHashRequest_UsesLegacyManifest()
    {
        var localSilo = CreateSiloAddress(11111, 1);
        var remoteSilo = CreateSiloAddress(11112, 1);
        var remoteManifest = CreateGrainManifest();
        using var membership = new TestClusterMembershipService(CreateActiveMembershipSnapshot(1, localSilo, [remoteSilo]));
        var grainFactory = CreateGrainFactory(remoteSilo, remoteManifest);
        var remoteTarget = Substitute.For<IClusterManifestSystemTarget>();
        remoteTarget.GetSiloManifestHash(Arg.Any<CancellationToken>()).Returns(new ValueTask<ManifestHash>(
            Task.FromCanceled<ManifestHash>(new CancellationToken(canceled: true))));
        grainFactory.GetSystemTarget<IClusterManifestSystemTarget>(Constants.ManifestProviderType, remoteSilo)
            .Returns(remoteTarget);
        await using var provider = CreateClusterManifestProvider(localSilo, membership, grainFactory);
        var observed = ObserveManifestAsync(provider, new MajorMinorVersion(1, 1), TestContext.Current.CancellationToken);
        var lifecycle = await StartAsync(provider, TestContext.Current.CancellationToken);
        try
        {
            var current = await observed.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(remoteManifest, current.Silos[remoteSilo]);
        }
        finally
        {
            await lifecycle.OnStop(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    [TestCategory("BVT")]
    public async Task ClusterManifestProviderReusesCacheForCanonicalManifestHash()
    {
        var localSilo = CreateSiloAddress(11201, 1);
        var remoteSilo = CreateSiloAddress(11202, 1);
        using var membership = new TestClusterMembershipService(CreateMembershipSnapshot(
            1,
            (localSilo, SiloStatus.Active)));
        var grainFactory = Substitute.For<IInternalGrainFactory>();
        var provider = CreateClusterManifestProvider(localSilo, membership, grainFactory);
        var canonicalHash = ManifestHashCalculator.ComputeHash(provider.LocalGrainManifest);
        var remoteTarget = new CanonicalManifestCacheTarget(canonicalHash, provider.LocalGrainManifest);
        grainFactory
            .GetSystemTarget<IClusterManifestSystemTarget>(Constants.ManifestProviderType, remoteSilo)
            .Returns(remoteTarget);
        grainFactory
            .GetSystemTarget<ISiloManifestSystemTarget>(Constants.ManifestProviderType, remoteSilo)
            .Returns(remoteTarget);
        var lifecycle = await StartAsync(provider, TestContext.Current.CancellationToken);

        try
        {
            var observed = ObserveManifestAsync(provider, new MajorMinorVersion(2, 1), TestContext.Current.CancellationToken);
            membership.Update(CreateMembershipSnapshot(
                2,
                (localSilo, SiloStatus.Active),
                (remoteSilo, SiloStatus.Active)));

            var current = await observed.WaitAsync(
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);

            Assert.Same(provider.LocalGrainManifest, current.Silos[remoteSilo]);
            Assert.Equal(canonicalHash, ManifestHashCalculator.ComputeHash(current.Silos[remoteSilo]));
            Assert.Equal(1, remoteTarget.HashRequests);
            Assert.Equal(0, remoteTarget.ManifestByHashRequests);
            Assert.Equal(0, remoteTarget.LegacyManifestRequests);
        }
        finally
        {
            await lifecycle.OnStop(TestContext.Current.CancellationToken);
            provider.Dispose();
        }
    }

    private static ConditionalWeakTable<GrainManifest, StrongBox<ManifestHash>> GetMemoizedManifestHashes() =>
        (ConditionalWeakTable<GrainManifest, StrongBox<ManifestHash>>)MemoizedHashesField.GetValue(null)!;

    private sealed class CanonicalManifestCacheTarget(ManifestHash hash, GrainManifest fallbackManifest)
        : IClusterManifestSystemTarget, ISiloManifestSystemTarget
    {
        private int _hashRequests;
        private int _manifestByHashRequests;
        private int _legacyManifestRequests;

        public int HashRequests => Volatile.Read(ref _hashRequests);

        public int ManifestByHashRequests => Volatile.Read(ref _manifestByHashRequests);

        public int LegacyManifestRequests => Volatile.Read(ref _legacyManifestRequests);

        public ValueTask<ClusterManifest> GetClusterManifest(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromException<ClusterManifest>(new NotSupportedException());
        }

        public ValueTask<ClusterManifestUpdate?> GetClusterManifestUpdate(
            MajorMinorVersion previousVersion,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromException<ClusterManifestUpdate?>(new NotSupportedException());
        }

        public ValueTask<ClusterManifestHashSummary> GetClusterManifestHashSummary(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromException<ClusterManifestHashSummary>(new NotSupportedException());
        }

        public ValueTask<ManifestHash> GetSiloManifestHash(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _hashRequests);
            return new(hash);
        }

        public ValueTask<GrainManifest?> GetSiloManifestByHash(ManifestHash requestedHash, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _manifestByHashRequests);
            return new(fallbackManifest);
        }

        public ValueTask<GrainManifest> GetSiloManifest(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _legacyManifestRequests);
            return new(fallbackManifest);
        }
    }
}
