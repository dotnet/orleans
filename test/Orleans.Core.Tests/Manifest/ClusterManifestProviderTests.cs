using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Runtime.Dissemination;
using Orleans.Runtime.Metadata;
using Orleans.Runtime.Utilities;
using Orleans.Runtime.Versions;
using Orleans.Runtime.Versions.Compatibility;
using Orleans.Runtime.Versions.Selector;
using Orleans.Serialization;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.TypeSystem;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;
using TestExtensions;
using Xunit;

namespace UnitTests.Manifest;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT"), TestCategory("Manifest")]
public class ClusterManifestProviderTests
{
    private static readonly GrainType TestGrainType = GrainType.Create("test");
    private static readonly GrainInterfaceType TestInterfaceType = GrainInterfaceType.Create("test.interface");

    [Fact]
    public void Current_WhenLocalSiloIsNotActive_ResolvesTypeFromLocalManifest()
    {
        var localSilo = CreateSiloAddress(11111, 1);
        using var membership = new TestClusterMembershipService(CreateMembershipSnapshot(
            1,
            (localSilo, SiloStatus.Created)));
        var grainFactory = CreateGrainFactory(CreateSiloAddress(11112, 1), CreateGrainManifest());
        var provider = CreateClusterManifestProvider(localSilo, membership, grainFactory);

        var current = provider.Current;
        var typeResolver = new Orleans.GrainInterfaceTypeToGrainTypeResolver(provider);

        Assert.Equal(new MajorMinorVersion(1, 0), current.Version);
        Assert.DoesNotContain(localSilo, current.Silos.Keys);
        Assert.Contains(provider.LocalGrainManifest, current.AllGrainManifests);
        Assert.Equal(TestGrainType, typeResolver.GetGrainType(TestInterfaceType));
    }

    [Fact]
    public void Current_WhenLocalSiloBecomesActive_IncludesLocalManifestSynchronously()
    {
        var localSilo = CreateSiloAddress(11111, 1);
        using var membership = new TestClusterMembershipService(CreateMembershipSnapshot(
            1,
            (localSilo, SiloStatus.Created)));
        var grainFactory = CreateGrainFactory(CreateSiloAddress(11112, 1), CreateGrainManifest());
        var provider = CreateClusterManifestProvider(localSilo, membership, grainFactory);

        Assert.DoesNotContain(localSilo, provider.Current.Silos.Keys);

        membership.Update(CreateMembershipSnapshot(
            2,
            (localSilo, SiloStatus.Active)));

        var current = provider.Current;

        Assert.Equal(new MajorMinorVersion(2, 0), current.Version);
        Assert.Contains(localSilo, current.Silos.Keys);
        Assert.Contains(provider.LocalGrainManifest, current.AllGrainManifests);
    }

    [Fact]
    public async Task Current_WhenMembershipVersionAdvances_PrunesNonActiveSilosAtFirstMinorVersion()
    {
        var localSilo = CreateSiloAddress(11111, 1);
        var remoteSilo = CreateSiloAddress(11112, 1);
        var remoteManifest = CreateGrainManifest("2");
        var remoteHash = ManifestHashCalculator.ComputeHash(remoteManifest);
        var membership = new TestClusterMembershipService(CreateMembershipSnapshot(
            1,
            (localSilo, SiloStatus.Active),
            (remoteSilo, SiloStatus.Active)));
        var grainFactory = CreateGrainFactory(remoteSilo, remoteManifest);
        var provider = CreateClusterManifestProvider(localSilo, membership, grainFactory);
        var lifecycle = await StartAsync(provider);

        try
        {
            await Until(() => provider.Current.Version == new MajorMinorVersion(1, 1)
                && provider.Current.Silos.ContainsKey(remoteSilo));
            Assert.True(provider.IsManifestCached(remoteHash));
            Assert.Equal(2, provider.ManifestCacheCount);

            membership.Update(CreateMembershipSnapshot(
                2,
                (localSilo, SiloStatus.Active),
                (remoteSilo, SiloStatus.ShuttingDown)));

            var current = provider.Current;

            Assert.Equal(new MajorMinorVersion(2, 0), current.Version);
            Assert.Contains(localSilo, current.Silos.Keys);
            Assert.DoesNotContain(remoteSilo, current.Silos.Keys);
            Assert.False(provider.IsManifestCached(remoteHash));
            Assert.Equal(1, provider.ManifestCacheCount);
        }
        finally
        {
            await lifecycle.OnStop(TestContext.Current.CancellationToken);
            membership.Dispose();
        }
    }

    [Fact]
    public async Task Current_WhenRemoteSiloBecomesActive_IncludesLocalManifestBeforeRemoteFetch()
    {
        var localSilo = CreateSiloAddress(11111, 1);
        var remoteSilo = CreateSiloAddress(11112, 1);
        var remoteManifest = CreateGrainManifest();
        var membership = new TestClusterMembershipService(CreateMembershipSnapshot(
            1,
            (localSilo, SiloStatus.Active),
            (remoteSilo, SiloStatus.Joining)));
        var grainFactory = CreateGrainFactory(remoteSilo, remoteManifest);
        var provider = CreateClusterManifestProvider(localSilo, membership, grainFactory);

        var current = provider.Current;
        Assert.Equal(new MajorMinorVersion(1, 0), current.Version);
        Assert.Contains(localSilo, current.Silos.Keys);
        Assert.DoesNotContain(remoteSilo, current.Silos.Keys);
        Assert.Contains(provider.LocalGrainManifest, current.AllGrainManifests);

        membership.Update(CreateMembershipSnapshot(
            2,
            (localSilo, SiloStatus.Active),
            (remoteSilo, SiloStatus.Active)));

        var pruned = provider.Current;
        Assert.Equal(new MajorMinorVersion(2, 0), pruned.Version);
        Assert.Contains(localSilo, pruned.Silos.Keys);
        Assert.DoesNotContain(remoteSilo, pruned.Silos.Keys);

        var lifecycle = await StartAsync(provider);
        try
        {
            await Until(() => provider.Current.Version == new MajorMinorVersion(2, 1)
                && provider.Current.Silos.ContainsKey(remoteSilo));
        }
        finally
        {
            await lifecycle.OnStop(TestContext.Current.CancellationToken);
            membership.Dispose();
        }
    }

    [Fact]
    public void GrainVersionManifest_UpdatesSupportedSilosWhenClusterManifestVersionChanges()
    {
        var localSilo = CreateSiloAddress(11111, 1);
        var remoteSilo = CreateSiloAddress(11112, 1);
        var clusterManifestProvider = new TestClusterManifestProvider(CreateClusterManifest(1, 0, localSilo, remoteSilo));
        var manifest = new GrainVersionManifest(clusterManifestProvider);

        var initial = manifest.GetSupportedSilos(TestGrainType).Result;
        Assert.Equal(new[] { localSilo, remoteSilo }, initial.OrderBy(static silo => silo));

        clusterManifestProvider.Current = CreateClusterManifest(2, 0, localSilo);

        var updated = manifest.GetSupportedSilos(TestGrainType).Result;
        Assert.Equal(new[] { localSilo }, updated);
    }

    [Fact]
    public void CachedVersionSelectorManager_RefreshesSuitableSilosWhenClusterManifestVersionChanges()
    {
        var localSilo = CreateSiloAddress(11111, 1);
        var remoteSilo = CreateSiloAddress(11112, 1);
        var clusterManifestProvider = new TestClusterManifestProvider(CreateClusterManifest(1, 0, localSilo, remoteSilo));
        using var membership = new TestClusterMembershipService(CreateMembershipSnapshot(
            1,
            (localSilo, SiloStatus.Active),
            (remoteSilo, SiloStatus.Active)));
        var manifest = new GrainVersionManifest(clusterManifestProvider);
        var selectorManager = CreateCachedVersionSelectorManager(manifest, membership);

        var initial = selectorManager.GetSuitableSilos(TestGrainType, TestInterfaceType, requestedVersion: 1);
        SiloAddress[] initialSilos = initial.SuitableSilos;
        Assert.Equal(new[] { localSilo, remoteSilo }, initialSilos.OrderBy(static silo => silo));
        Assert.Equal(new[] { localSilo, remoteSilo }, selectorManager.GetSupportedSilos(TestGrainType).OrderBy(static silo => silo));

        membership.Update(CreateMembershipSnapshot(
            2,
            (localSilo, SiloStatus.ShuttingDown),
            (remoteSilo, SiloStatus.Active)));
        clusterManifestProvider.Current = CreateClusterManifest(2, 0, remoteSilo);

        var updated = selectorManager.GetSuitableSilos(TestGrainType, TestInterfaceType, requestedVersion: 1);
        SiloAddress[] updatedSilos = updated.SuitableSilos;
        Assert.Equal(new[] { remoteSilo }, updatedSilos);
        Assert.Equal(new[] { remoteSilo }, selectorManager.GetSupportedSilos(TestGrainType));
    }

    [Fact]
    public void CachedVersionSelectorManager_RefreshesSuitableSilosWhenManifestMinorVersionChanges()
    {
        var localSilo = CreateSiloAddress(11111, 1);
        var remoteSilo = CreateSiloAddress(11112, 1);
        var clusterManifestProvider = new TestClusterManifestProvider(CreateClusterManifest(1, 0, localSilo));
        using var membership = new TestClusterMembershipService(CreateMembershipSnapshot(
            1,
            (localSilo, SiloStatus.Active),
            (remoteSilo, SiloStatus.Active)));
        var selectorManager = CreateCachedVersionSelectorManager(
            new GrainVersionManifest(clusterManifestProvider),
            membership);

        Assert.Equal(new[] { localSilo }, selectorManager.GetSuitableSilos(TestGrainType, TestInterfaceType, requestedVersion: 1).SuitableSilos);

        clusterManifestProvider.Current = CreateClusterManifest(1, 1, localSilo, remoteSilo);

        Assert.Equal(
            new[] { localSilo, remoteSilo },
            selectorManager.GetSuitableSilos(TestGrainType, TestInterfaceType, requestedVersion: 1).SuitableSilos.OrderBy(static silo => silo));
    }

    [Fact]
    public async Task CachedVersionSelectorManager_RetriesUntilMembershipAndManifestVersionsConverge()
    {
        var localSilo = CreateSiloAddress(11111, 1);
        var remoteSilo = CreateSiloAddress(11112, 1);
        var clusterManifestProvider = new TestClusterManifestProvider(CreateClusterManifest(1, 0, localSilo));
        using var membership = new TestClusterMembershipService(CreateMembershipSnapshot(
            2,
            (localSilo, SiloStatus.Active),
            (remoteSilo, SiloStatus.Active)));
        var selectorManager = CreateCachedVersionSelectorManager(
            new GrainVersionManifest(clusterManifestProvider),
            membership);

        var suitableSilosRead = membership.ObserveNextSnapshotRead();
        var suitableSilosTask = Task.Run(
            () => selectorManager.GetSuitableSilos(TestGrainType, TestInterfaceType, requestedVersion: 1));
        await suitableSilosRead.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(suitableSilosTask.IsCompleted);

        clusterManifestProvider.Current = CreateClusterManifest(2, 0, remoteSilo);

        var suitableSilos = await suitableSilosTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(new[] { remoteSilo }, suitableSilos.SuitableSilos);

        membership.Update(CreateMembershipSnapshot(
            3,
            (localSilo, SiloStatus.Active),
            (remoteSilo, SiloStatus.Active)));
        var supportedSilosRead = membership.ObserveNextSnapshotRead();
        var supportedSilosTask = Task.Run(() => selectorManager.GetSupportedSilos(TestGrainType));
        await supportedSilosRead.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(supportedSilosTask.IsCompleted);

        clusterManifestProvider.Current = CreateClusterManifest(3, 0, localSilo, remoteSilo);

        var supportedSilos = await supportedSilosTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(new[] { localSilo, remoteSilo }, supportedSilos.OrderBy(static silo => silo));
    }

    /// <summary>
    /// When two or more manifests are missing simultaneously, the provider should attempt to fill them from a single
    /// peer's cluster manifest hash summary before falling back to individual per-silo manifest fetches. When the
    /// advertised hash is already present in the local manifest cache (e.g. because it matches a manifest already
    /// known to this silo), the value is reused directly and no additional peer needs to be contacted at all.
    /// </summary>
    [Fact]
    public async Task Current_FillsMultipleMissingManifestsFromSinglePeerHashSummaryCache()
    {
        var localSilo = CreateSiloAddress(11131, 1);
        var peer1 = CreateSiloAddress(11132, 1);
        var peer2 = CreateSiloAddress(11133, 1);
        var membership = new TestClusterMembershipService(CreateMembershipSnapshot(
            1,
            (localSilo, SiloStatus.Active)));
        var grainFactory = Substitute.For<IInternalGrainFactory>();
        var siloManifestProvider = CreateSiloManifestProvider();
        var localManifest = siloManifestProvider.SiloManifest;
        var localHash = ManifestHashCalculator.ComputeHash(localManifest);

        // Both peers advertise the same manifest hash as the local silo (a common case in a homogeneous cluster),
        // so both should be satisfied entirely from the local manifest cache after a single hash-summary request to peer1.
        var hashSummary = new ClusterManifestHashSummary(
            new MajorMinorVersion(1, 0),
            ImmutableDictionary<SiloAddress, ManifestHash>.Empty.Add(peer1, localHash).Add(peer2, localHash));
        var peer1Target = new FakeClusterManifestSystemTarget(hashSummary);
        grainFactory
            .GetSystemTarget<IClusterManifestSystemTarget>(Constants.ManifestProviderType, peer1)
            .Returns(peer1Target);

        var services = new ServiceCollection().AddSingleton(grainFactory).BuildServiceProvider();
        var localSiloDetails = Substitute.For<ILocalSiloDetails>();
        localSiloDetails.SiloAddress.Returns(localSilo);

        var provider = new ClusterManifestProvider(
            localSiloDetails,
            siloManifestProvider,
            membership,
            Substitute.For<IFatalErrorHandler>(),
            NullLogger<ClusterManifestProvider>.Instance,
            services);

        var lifecycle = await StartAsync(provider);
        try
        {
            membership.Update(CreateMembershipSnapshot(
                2,
                (localSilo, SiloStatus.Active),
                (peer1, SiloStatus.Active),
                (peer2, SiloStatus.Active)));

            await Until(() => provider.Current.Silos.ContainsKey(peer1) && provider.Current.Silos.ContainsKey(peer2));

            Assert.Same(localManifest, provider.Current.Silos[peer1]);
            Assert.Same(localManifest, provider.Current.Silos[peer2]);
            Assert.Equal(1, peer1Target.HashSummaryRequests);
            Assert.Equal(0, peer1Target.UpdateRequests);
            grainFactory.DidNotReceive().GetSystemTarget<IClusterManifestSystemTarget>(Constants.ManifestProviderType, peer2);
        }
        finally
        {
            await lifecycle.OnStop();
            membership.Dispose();
        }
    }

    /// <summary>
    /// A single missing manifest (below the batch-fill threshold) is fetched via the new hash-based path
    /// (<see cref="IClusterManifestSystemTarget.GetSiloManifestHash"/> + <see cref="IClusterManifestSystemTarget.GetSiloManifestByHash"/>)
    /// and must not fall back to the legacy <see cref="ISiloManifestSystemTarget"/> when the hash-based fetch succeeds.
    /// </summary>
    [Fact]
    public async Task Current_FetchesSingleMissingManifestViaHashPathWithoutLegacyFallback()
    {
        var localSilo = CreateSiloAddress(11134, 1);
        var remoteSilo = CreateSiloAddress(11135, 1);
        var remoteManifest = CreateGrainManifest();
        var remoteHash = ManifestHashCalculator.ComputeHash(remoteManifest);
        var membership = new TestClusterMembershipService(CreateMembershipSnapshot(
            1,
            (localSilo, SiloStatus.Active)));
        var grainFactory = Substitute.For<IInternalGrainFactory>();
        var siloManifestProvider = CreateSiloManifestProvider();
        var remoteTarget = new FakeHashOnlyClusterManifestSystemTarget(remoteHash, remoteManifest);
        grainFactory
            .GetSystemTarget<IClusterManifestSystemTarget>(Constants.ManifestProviderType, remoteSilo)
            .Returns(remoteTarget);
        var legacyTarget = new RecordingSiloManifestSystemTarget();
        grainFactory
            .GetSystemTarget<ISiloManifestSystemTarget>(Constants.ManifestProviderType, remoteSilo)
            .Returns(legacyTarget);

        var services = new ServiceCollection().AddSingleton(grainFactory).BuildServiceProvider();
        var localSiloDetails = Substitute.For<ILocalSiloDetails>();
        localSiloDetails.SiloAddress.Returns(localSilo);

        var provider = new ClusterManifestProvider(
            localSiloDetails,
            siloManifestProvider,
            membership,
            Substitute.For<IFatalErrorHandler>(),
            NullLogger<ClusterManifestProvider>.Instance,
            services);

        var lifecycle = await StartAsync(provider);
        try
        {
            membership.Update(CreateMembershipSnapshot(
                2,
                (localSilo, SiloStatus.Active),
                (remoteSilo, SiloStatus.Active)));

            await Until(() => provider.Current.Silos.ContainsKey(remoteSilo));

            Assert.Same(remoteManifest, provider.Current.Silos[remoteSilo]);
            Assert.False(legacyTarget.WasInvoked);
        }
        finally
        {
            await lifecycle.OnStop();
            membership.Dispose();
        }
    }

    [Fact]
    public async Task Current_FillsMultipleMissingManifestsFromPeerUpdateWhenHashesMatch()
    {
        var localSilo = CreateSiloAddress(11136, 1);
        var peer1 = CreateSiloAddress(11137, 1);
        var peer2 = CreateSiloAddress(11138, 1);
        var manifest1 = CreateGrainManifest("2");
        var manifest2 = CreateGrainManifest("3");
        var hash1 = ManifestHashCalculator.ComputeHash(manifest1);
        var hash2 = ManifestHashCalculator.ComputeHash(manifest2);
        var membership = new TestClusterMembershipService(CreateMembershipSnapshot(
            1,
            (localSilo, SiloStatus.Active)));
        var grainFactory = Substitute.For<IInternalGrainFactory>();
        var summary = new ClusterManifestHashSummary(
            new MajorMinorVersion(2, 0),
            ImmutableDictionary<SiloAddress, ManifestHash>.Empty.Add(peer1, hash1).Add(peer2, hash2));
        var update = new ClusterManifestUpdate(
            new MajorMinorVersion(2, 0),
            ImmutableDictionary<SiloAddress, GrainManifest>.Empty.Add(peer1, manifest1).Add(peer2, manifest2),
            includesAllActiveServers: true);
        var peer1Target = new FakeClusterManifestSystemTarget(summary, update);
        grainFactory
            .GetSystemTarget<IClusterManifestSystemTarget>(Constants.ManifestProviderType, peer1)
            .Returns(peer1Target);
        var services = new ServiceCollection().AddSingleton(grainFactory).BuildServiceProvider();
        var localSiloDetails = Substitute.For<ILocalSiloDetails>();
        localSiloDetails.SiloAddress.Returns(localSilo);
        var provider = new ClusterManifestProvider(
            localSiloDetails,
            CreateSiloManifestProvider(),
            membership,
            Substitute.For<IFatalErrorHandler>(),
            NullLogger<ClusterManifestProvider>.Instance,
            services);

        var lifecycle = await StartAsync(provider);
        try
        {
            membership.Update(CreateMembershipSnapshot(
                2,
                (localSilo, SiloStatus.Active),
                (peer1, SiloStatus.Active),
                (peer2, SiloStatus.Active)));

            await Until(() => provider.Current.Silos.ContainsKey(peer1) && provider.Current.Silos.ContainsKey(peer2));

            Assert.Equal(hash1, ManifestHashCalculator.ComputeHash(provider.Current.Silos[peer1]));
            Assert.Equal(hash2, ManifestHashCalculator.ComputeHash(provider.Current.Silos[peer2]));
            Assert.Equal(1, peer1Target.HashSummaryRequests);
            Assert.Equal(1, peer1Target.UpdateRequests);
            grainFactory.DidNotReceive().GetSystemTarget<IClusterManifestSystemTarget>(Constants.ManifestProviderType, peer2);
        }
        finally
        {
            await lifecycle.OnStop();
            membership.Dispose();
        }
    }

    [Fact]
    public async Task Current_CachesPeerUpdateManifestsWhenConcurrentPublicationWins()
    {
        var localSilo = CreateSiloAddress(11141, 1);
        var peer1 = CreateSiloAddress(11142, 1);
        var peer2 = CreateSiloAddress(11143, 1);
        var manifest1 = CreateGrainManifest("2");
        var manifest2 = CreateGrainManifest("3");
        var hash1 = ManifestHashCalculator.ComputeHash(manifest1);
        var hash2 = ManifestHashCalculator.ComputeHash(manifest2);
        var membership = new TestClusterMembershipService(CreateMembershipSnapshot(
            1,
            (localSilo, SiloStatus.Active)));
        var grainFactory = Substitute.For<IInternalGrainFactory>();
        var summary = new ClusterManifestHashSummary(
            new MajorMinorVersion(2, 0),
            ImmutableDictionary<SiloAddress, ManifestHash>.Empty.Add(peer1, hash1).Add(peer2, hash2));
        var update = new ClusterManifestUpdate(
            new MajorMinorVersion(2, 0),
            ImmutableDictionary<SiloAddress, GrainManifest>.Empty.Add(peer1, manifest1).Add(peer2, manifest2),
            includesAllActiveServers: true);
        var updateRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseUpdate = new TaskCompletionSource<ClusterManifestUpdate?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer1Target = new FakeClusterManifestSystemTarget(
            summary,
            getUpdate: _ =>
            {
                updateRequested.TrySetResult();
                return new(releaseUpdate.Task);
            });
        grainFactory
            .GetSystemTarget<IClusterManifestSystemTarget>(Constants.ManifestProviderType, peer1)
            .Returns(peer1Target);
        var services = new ServiceCollection().AddSingleton(grainFactory).BuildServiceProvider();
        var localSiloDetails = Substitute.For<ILocalSiloDetails>();
        localSiloDetails.SiloAddress.Returns(localSilo);
        var provider = new ClusterManifestProvider(
            localSiloDetails,
            CreateSiloManifestProvider(),
            membership,
            Substitute.For<IFatalErrorHandler>(),
            NullLogger<ClusterManifestProvider>.Instance,
            services);

        var lifecycle = await StartAsync(provider);
        try
        {
            membership.Update(CreateMembershipSnapshot(
                2,
                (localSilo, SiloStatus.Active),
                (peer1, SiloStatus.Active),
                (peer2, SiloStatus.Active)));
            await updateRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));

            membership.Update(CreateMembershipSnapshot(
                3,
                (localSilo, SiloStatus.Active),
                (peer1, SiloStatus.Active),
                (peer2, SiloStatus.Active)));
            Assert.Equal(new MajorMinorVersion(3, 0), provider.Current.Version);
            releaseUpdate.SetResult(update);

            await Until(() => provider.Current.Version == new MajorMinorVersion(3, 1));

            Assert.Equal(1, peer1Target.UpdateRequests);
            Assert.True(provider.IsManifestCached(hash1));
            Assert.True(provider.IsManifestCached(hash2));
        }
        finally
        {
            await lifecycle.OnStop();
            membership.Dispose();
        }
    }

    [Fact]
    public async Task Current_CachesHashFetchedManifestWhenConcurrentPublicationWins()
    {
        var localSilo = CreateSiloAddress(11144, 1);
        var remoteSilo = CreateSiloAddress(11145, 1);
        var remoteManifest = CreateGrainManifest("2");
        var remoteHash = ManifestHashCalculator.ComputeHash(remoteManifest);
        var membership = new TestClusterMembershipService(CreateMembershipSnapshot(
            1,
            (localSilo, SiloStatus.Active)));
        var grainFactory = Substitute.For<IInternalGrainFactory>();
        var manifestRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseManifest = new TaskCompletionSource<GrainManifest?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var remoteTarget = new FakeHashOnlyClusterManifestSystemTarget(
            remoteHash,
            remoteManifest,
            getManifest: _ =>
            {
                manifestRequested.TrySetResult();
                return new(releaseManifest.Task);
            });
        grainFactory
            .GetSystemTarget<IClusterManifestSystemTarget>(Constants.ManifestProviderType, remoteSilo)
            .Returns(remoteTarget);
        var services = new ServiceCollection().AddSingleton(grainFactory).BuildServiceProvider();
        var localSiloDetails = Substitute.For<ILocalSiloDetails>();
        localSiloDetails.SiloAddress.Returns(localSilo);
        var provider = new ClusterManifestProvider(
            localSiloDetails,
            CreateSiloManifestProvider(),
            membership,
            Substitute.For<IFatalErrorHandler>(),
            NullLogger<ClusterManifestProvider>.Instance,
            services);

        var lifecycle = await StartAsync(provider);
        try
        {
            membership.Update(CreateMembershipSnapshot(
                2,
                (localSilo, SiloStatus.Active),
                (remoteSilo, SiloStatus.Active)));
            await manifestRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));

            membership.Update(CreateMembershipSnapshot(
                3,
                (localSilo, SiloStatus.Active),
                (remoteSilo, SiloStatus.Active)));
            Assert.Equal(new MajorMinorVersion(3, 0), provider.Current.Version);
            releaseManifest.SetResult(remoteManifest);

            await Until(() => provider.Current.Version == new MajorMinorVersion(3, 1));

            Assert.Equal(1, remoteTarget.ManifestRequests);
            Assert.True(provider.IsManifestCached(remoteHash));
        }
        finally
        {
            await lifecycle.OnStop();
            membership.Dispose();
        }
    }

    [Fact]
    public async Task Current_FallsBackToLegacyManifestWhenHashResponseDoesNotMatch()
    {
        var localSilo = CreateSiloAddress(11139, 1);
        var remoteSilo = CreateSiloAddress(11140, 1);
        var advertisedManifest = CreateGrainManifest("2");
        var mismatchedManifest = CreateGrainManifest("3");
        var legacyManifest = CreateGrainManifest("4");
        var membership = new TestClusterMembershipService(CreateMembershipSnapshot(
            1,
            (localSilo, SiloStatus.Active)));
        var grainFactory = Substitute.For<IInternalGrainFactory>();
        grainFactory
            .GetSystemTarget<IClusterManifestSystemTarget>(Constants.ManifestProviderType, remoteSilo)
            .Returns(new FakeHashOnlyClusterManifestSystemTarget(
                ManifestHashCalculator.ComputeHash(advertisedManifest),
                mismatchedManifest));
        var legacyTarget = new RecordingSiloManifestSystemTarget(legacyManifest);
        grainFactory
            .GetSystemTarget<ISiloManifestSystemTarget>(Constants.ManifestProviderType, remoteSilo)
            .Returns(legacyTarget);
        var services = new ServiceCollection().AddSingleton(grainFactory).BuildServiceProvider();
        var localSiloDetails = Substitute.For<ILocalSiloDetails>();
        localSiloDetails.SiloAddress.Returns(localSilo);
        var provider = new ClusterManifestProvider(
            localSiloDetails,
            CreateSiloManifestProvider(),
            membership,
            Substitute.For<IFatalErrorHandler>(),
            NullLogger<ClusterManifestProvider>.Instance,
            services);

        var lifecycle = await StartAsync(provider);
        try
        {
            membership.Update(CreateMembershipSnapshot(
                2,
                (localSilo, SiloStatus.Active),
                (remoteSilo, SiloStatus.Active)));

            await Until(() => provider.Current.Silos.ContainsKey(remoteSilo));

            Assert.Same(legacyManifest, provider.Current.Silos[remoteSilo]);
            Assert.True(legacyTarget.WasInvoked);
        }
        finally
        {
            await lifecycle.OnStop();
            membership.Dispose();
        }
    }

    private static ClusterManifestProvider CreateClusterManifestProvider(
        SiloAddress localSilo,
        TestClusterMembershipService membership,
        IInternalGrainFactory grainFactory)
    {
        var siloManifestProvider = CreateSiloManifestProvider();
        grainFactory
            .GetSystemTarget<ISiloManifestSystemTarget>(Constants.ManifestProviderType, localSilo)
            .Returns(new TestSiloManifestSystemTarget(siloManifestProvider.SiloManifest));

        var services = new ServiceCollection()
            .AddSingleton(grainFactory)
            .BuildServiceProvider();

        var localSiloDetails = Substitute.For<ILocalSiloDetails>();
        localSiloDetails.SiloAddress.Returns(localSilo);

        return new ClusterManifestProvider(
            localSiloDetails,
            siloManifestProvider,
            membership,
            Substitute.For<IFatalErrorHandler>(),
            NullLogger<ClusterManifestProvider>.Instance,
            services);
    }

    private static IInternalGrainFactory CreateGrainFactory(SiloAddress remoteSilo, GrainManifest remoteManifest)
    {
        var grainFactory = Substitute.For<IInternalGrainFactory>();
        grainFactory
            .GetSystemTarget<ISiloManifestSystemTarget>(Constants.ManifestProviderType, remoteSilo)
            .Returns(new TestSiloManifestSystemTarget(remoteManifest));
        return grainFactory;
    }

    private static CachedVersionSelectorManager CreateCachedVersionSelectorManager(
        GrainVersionManifest manifest,
        IClusterMembershipService membership)
    {
        var services = new ServiceCollection();
        services.AddOptions<GrainVersioningOptions>();
        services.AddKeyedSingleton<VersionSelectorStrategy, AllCompatibleVersions>(nameof(AllCompatibleVersions));
        services.AddKeyedSingleton<CompatibilityStrategy, BackwardCompatible>(nameof(BackwardCompatible));
        services.AddKeyedSingleton<IVersionSelector, AllCompatibleVersionsSelector>(typeof(AllCompatibleVersions));
        services.AddKeyedSingleton<ICompatibilityDirector, BackwardCompatilityDirector>(typeof(BackwardCompatible));
        var serviceProvider = services.BuildServiceProvider();
        var options = serviceProvider.GetRequiredService<IOptions<GrainVersioningOptions>>();

        return new CachedVersionSelectorManager(
            manifest,
            new VersionSelectorManager(serviceProvider, options),
            new CompatibilityDirectorManager(serviceProvider, options),
            membership);
    }

    private static ClusterManifest CreateClusterManifest(long major, long minor, params SiloAddress[] silos)
    {
        var manifest = CreateGrainManifest();
        return new ClusterManifest(
            new MajorMinorVersion(major, minor),
            silos.ToImmutableDictionary(silo => silo, _ => manifest));
    }

    private static GrainManifest CreateGrainManifest(string interfaceVersion = "1")
    {
        var grains = ImmutableDictionary.CreateRange(
        [
            new KeyValuePair<GrainType, GrainProperties>(
                TestGrainType,
                new GrainProperties(CreatePropertyDictionary(
                [
                    new KeyValuePair<string, string>(WellKnownGrainTypeProperties.TypeName, "Test"),
                    new KeyValuePair<string, string>(WellKnownGrainTypeProperties.FullTypeName, "UnitTests.Grains.Test"),
                    new KeyValuePair<string, string>($"{WellKnownGrainTypeProperties.ImplementedInterfacePrefix}0", TestInterfaceType.ToString())
                ])))
        ]);
        var interfaces = ImmutableDictionary.CreateRange(
        [
            new KeyValuePair<GrainInterfaceType, GrainInterfaceProperties>(
                TestInterfaceType,
                new GrainInterfaceProperties(CreatePropertyDictionary(
                [
                    new KeyValuePair<string, string>(WellKnownGrainInterfaceProperties.TypeName, "ITest"),
                    new KeyValuePair<string, string>(WellKnownGrainInterfaceProperties.Version, interfaceVersion)
                ])))
        ]);

        return new GrainManifest(grains, interfaces);
    }

    private static ImmutableDictionary<string, string> CreatePropertyDictionary(params KeyValuePair<string, string>[] properties)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal, StringComparer.Ordinal);
        foreach (var property in properties)
        {
            builder.Add(property.Key, property.Value);
        }

        return builder.ToImmutable();
    }

    private static SiloManifestProvider CreateSiloManifestProvider()
    {
        var typeConverter = CreateTypeConverter();
        var interfaceTypeResolver = new GrainInterfaceTypeResolver([new TestGrainInterfaceTypeProvider()], typeConverter);
        var typeNameProvider = new TypeNameGrainPropertiesProvider();
        var options = new GrainTypeOptions();
        options.Classes.Add(typeof(TestManifestGrain));
        options.Interfaces.Add(typeof(ITestManifestGrain));

        return new SiloManifestProvider(
            [typeNameProvider, new ImplementedInterfaceProvider(interfaceTypeResolver)],
            [typeNameProvider, new TestGrainInterfacePropertiesProvider()],
            Options.Create(options),
            new GrainTypeResolver([new TestGrainTypeProvider()], typeConverter),
            interfaceTypeResolver,
            typeConverter);
    }

    internal interface ITestManifestGrain : IGrainWithStringKey;

    internal sealed class TestManifestGrain : ITestManifestGrain;

    private sealed class TestGrainTypeProvider : IGrainTypeProvider
    {
        public bool TryGetGrainType(Type type, out GrainType grainType)
        {
            if (type == typeof(TestManifestGrain))
            {
                grainType = TestGrainType;
                return true;
            }

            grainType = default;
            return false;
        }
    }

    private sealed class TestGrainInterfaceTypeProvider : IGrainInterfaceTypeProvider
    {
        public bool TryGetGrainInterfaceType(Type type, out GrainInterfaceType grainInterfaceType)
        {
            if (type == typeof(ITestManifestGrain))
            {
                grainInterfaceType = TestInterfaceType;
                return true;
            }

            grainInterfaceType = default;
            return false;
        }
    }

    private sealed class TestGrainInterfacePropertiesProvider : IGrainInterfacePropertiesProvider
    {
        public void Populate(Type interfaceType, GrainInterfaceType grainInterfaceType, Dictionary<string, string> properties)
        {
            properties[WellKnownGrainInterfaceProperties.Version] = "1";
        }
    }

    private static Orleans.Serialization.TypeSystem.TypeConverter CreateTypeConverter()
    {
        return new Orleans.Serialization.TypeSystem.TypeConverter(
            Array.Empty<ITypeConverter>(),
            Array.Empty<ITypeNameFilter>(),
            Array.Empty<ITypeFilter>(),
            Options.Create(new TypeManifestOptions { AllowAllTypes = true }),
            new CachedTypeResolver());
    }

    private static ClusterMembershipSnapshot CreateMembershipSnapshot(
        long version,
        params (SiloAddress SiloAddress, SiloStatus Status)[] members)
    {
        var builder = ImmutableDictionary.CreateBuilder<SiloAddress, ClusterMember>();
        foreach (var (siloAddress, status) in members)
        {
            builder[siloAddress] = new ClusterMember(siloAddress, status, siloAddress.ToString());
        }

        return new ClusterMembershipSnapshot(builder.ToImmutable(), new MembershipVersion(version));
    }

    private static SiloAddress CreateSiloAddress(int port, int generation)
    {
        return SiloAddress.New(new IPEndPoint(IPAddress.Loopback, port), generation);
    }

    private static async Task<SiloLifecycleSubject> StartAsync(ClusterManifestProvider provider)
    {
        var lifecycle = new SiloLifecycleSubject(NullLoggerFactory.Instance.CreateLogger<SiloLifecycleSubject>());
        ((ILifecycleParticipant<ISiloLifecycle>)provider).Participate(lifecycle);
        await lifecycle.OnStart();
        return lifecycle;
    }

    private static async Task Until(Func<bool> condition)
    {
        var timeout = 10_000;
        while (!condition() && (timeout -= 10) > 0)
        {
            await Task.Delay(10);
        }

        Assert.True(timeout > 0);
    }

    private sealed class TestClusterMembershipService : IClusterMembershipService, IDisposable
    {
        private readonly AsyncEnumerable<ClusterMembershipSnapshot> _updates;
        private ClusterMembershipSnapshot _currentSnapshot = ClusterMembershipSnapshot.Default;
        private TaskCompletionSource? _nextSnapshotRead;

        public TestClusterMembershipService(ClusterMembershipSnapshot initialSnapshot)
        {
            _updates = new AsyncEnumerable<ClusterMembershipSnapshot>(
                initialValue: initialSnapshot,
                updateValidator: (previous, proposed) => proposed.Version > previous.Version,
                onPublished: update => Volatile.Write(ref _currentSnapshot, update));
        }

        public ClusterMembershipSnapshot CurrentSnapshot
        {
            get
            {
                Volatile.Read(ref _nextSnapshotRead)?.TrySetResult();
                return Volatile.Read(ref _currentSnapshot);
            }
        }

        public IAsyncEnumerable<ClusterMembershipSnapshot> MembershipUpdates => _updates;

        public void Update(ClusterMembershipSnapshot snapshot) => _updates.Publish(snapshot);

        public Task ObserveNextSnapshotRead()
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref _nextSnapshotRead, completion);
            return completion.Task;
        }

        public ValueTask Refresh(MembershipVersion minimumVersion = default, CancellationToken cancellationToken = default) => default;

        public Task<bool> TryKill(SiloAddress siloAddress) => Task.FromResult(false);

        public void Dispose() => _updates.Dispose();
    }

    private sealed class TestSiloManifestSystemTarget(GrainManifest manifest) : ISiloManifestSystemTarget
    {
        public ValueTask<GrainManifest> GetSiloManifest() => new(manifest);
    }

    /// <summary>
    /// Fake <see cref="IClusterManifestSystemTarget"/> that only supports <see cref="GetClusterManifestHashSummary"/>,
    /// used to verify the batch-fill path in <see cref="ClusterManifestProvider"/> that resolves multiple missing
    /// manifests from a single peer's hash summary.
    /// </summary>
    private sealed class FakeClusterManifestSystemTarget(
        ClusterManifestHashSummary hashSummary,
        ClusterManifestUpdate? update = null,
        Func<MajorMinorVersion, ValueTask<ClusterManifestUpdate?>>? getUpdate = null) : IClusterManifestSystemTarget
    {
        public int HashSummaryRequests { get; private set; }

        public int UpdateRequests { get; private set; }

        public ValueTask<ClusterManifest> GetClusterManifest() => throw new NotSupportedException();

        public ValueTask<ClusterManifestUpdate?> GetClusterManifestUpdate(MajorMinorVersion previousVersion)
        {
            UpdateRequests++;
            return getUpdate?.Invoke(previousVersion) ?? new(update);
        }

        public ValueTask<ClusterManifestHashSummary> GetClusterManifestHashSummary()
        {
            HashSummaryRequests++;
            return new(hashSummary);
        }

        public ValueTask<ManifestHash> GetSiloManifestHash() => throw new NotSupportedException();

        public ValueTask<GrainManifest?> GetSiloManifestByHash(ManifestHash hash) => throw new NotSupportedException();
    }

    /// <summary>
    /// Fake <see cref="IClusterManifestSystemTarget"/> that only supports the single-silo hash-based fetch path
    /// (<see cref="GetSiloManifestHash"/> + <see cref="GetSiloManifestByHash"/>).
    /// </summary>
    private sealed class FakeHashOnlyClusterManifestSystemTarget(
        ManifestHash hash,
        GrainManifest manifest,
        Func<ManifestHash, ValueTask<GrainManifest?>>? getManifest = null) : IClusterManifestSystemTarget
    {
        public int ManifestRequests { get; private set; }

        public ValueTask<ClusterManifest> GetClusterManifest() => throw new NotSupportedException();

        public ValueTask<ClusterManifestUpdate?> GetClusterManifestUpdate(MajorMinorVersion previousVersion) => throw new NotSupportedException();

        public ValueTask<ClusterManifestHashSummary> GetClusterManifestHashSummary() => throw new NotSupportedException();

        public ValueTask<ManifestHash> GetSiloManifestHash() => new(hash);

        public ValueTask<GrainManifest?> GetSiloManifestByHash(ManifestHash requestedHash)
        {
            ManifestRequests++;
            return getManifest?.Invoke(requestedHash) ?? new(requestedHash == hash ? manifest : null);
        }
    }

    /// <summary>
    /// Fake legacy <see cref="ISiloManifestSystemTarget"/> that records whether it was ever invoked, so tests can
    /// assert that the legacy fallback path was not used when the hash-based fetch succeeds.
    /// </summary>
    private sealed class RecordingSiloManifestSystemTarget(GrainManifest? manifest = null) : ISiloManifestSystemTarget
    {
        public bool WasInvoked { get; private set; }

        public ValueTask<GrainManifest> GetSiloManifest()
        {
            WasInvoked = true;
            return manifest is null
                ? throw new InvalidOperationException("The legacy manifest fetch path should not be used when the hash-based fetch succeeds.")
                : new(manifest);
        }
    }

    private sealed class TestClusterManifestProvider(ClusterManifest initialManifest) : IClusterManifestProvider
    {
        public ClusterManifest Current { get; set; } = initialManifest;

        public IAsyncEnumerable<ClusterManifest> Updates => GetUpdates();

        public GrainManifest LocalGrainManifest { get; } = CreateGrainManifest();

        private async IAsyncEnumerable<ClusterManifest> GetUpdates()
        {
            yield return Current;
            await Task.CompletedTask;
        }
    }
}
