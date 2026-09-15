using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Runtime.Versions;
using Orleans.Runtime.Versions.Compatibility;
using Orleans.Runtime.Versions.Selector;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;
using TestExtensions;
using Xunit;

namespace UnitTests.Manifest;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT"), TestCategory("Manifest")]
public partial class ClusterManifestProviderTests
{
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
        var remoteManifest = CreateGrainManifest();
        var membership = new TestClusterMembershipService(CreateMembershipSnapshot(
            1,
            (localSilo, SiloStatus.Active),
            (remoteSilo, SiloStatus.Active)));
        var grainFactory = CreateGrainFactory(remoteSilo, remoteManifest);
        var provider = CreateClusterManifestProvider(localSilo, membership, grainFactory);
        var observed = ObserveManifestAsync(provider, new MajorMinorVersion(1, 1), TestContext.Current.CancellationToken);
        var lifecycle = await StartAsync(provider, TestContext.Current.CancellationToken);

        try
        {
            var initial = await observed.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(new MajorMinorVersion(1, 1), initial.Version);
            Assert.Contains(remoteSilo, initial.Silos.Keys);
            Assert.Equal(2, GetCachedManifests(provider).Count);

            membership.Update(CreateMembershipSnapshot(
                2,
                (localSilo, SiloStatus.Active),
                (remoteSilo, SiloStatus.ShuttingDown)));

            var current = provider.Current;

            Assert.Equal(new MajorMinorVersion(2, 0), current.Version);
            Assert.Contains(localSilo, current.Silos.Keys);
            Assert.DoesNotContain(remoteSilo, current.Silos.Keys);
            Assert.Same(provider.LocalGrainManifest, Assert.Single(GetCachedManifests(provider)).Value);
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

        var lifecycle = await StartAsync(provider, TestContext.Current.CancellationToken);
        try
        {
            await Until(() => provider.Current.Version == new MajorMinorVersion(2, 1)
                && provider.Current.Silos.ContainsKey(remoteSilo), TestContext.Current.CancellationToken);
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
        var manifest = new GrainVersionManifest(clusterManifestProvider);
        var selectorManager = CreateCachedVersionSelectorManager(manifest);

        var initial = selectorManager.GetSuitableSilos(TestGrainType, TestInterfaceType, requestedVersion: 1);
        SiloAddress[] initialSilos = initial.SuitableSilos;
        Assert.Equal(new[] { localSilo, remoteSilo }, initialSilos.OrderBy(static silo => silo));
        Assert.Equal(new[] { localSilo, remoteSilo }, selectorManager.GetSupportedSilos(TestGrainType).OrderBy(static silo => silo));

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
        var selectorManager = CreateCachedVersionSelectorManager(new GrainVersionManifest(clusterManifestProvider));

        Assert.Equal(new[] { localSilo }, selectorManager.GetSuitableSilos(TestGrainType, TestInterfaceType, requestedVersion: 1).SuitableSilos);

        clusterManifestProvider.Current = CreateClusterManifest(1, 1, localSilo, remoteSilo);

        Assert.Equal(
            new[] { localSilo, remoteSilo },
            selectorManager.GetSuitableSilos(TestGrainType, TestInterfaceType, requestedVersion: 1).SuitableSilos.OrderBy(static silo => silo));
    }

    [Fact]
    public void GrainVersionManifest_CapturedSnapshotRemainsConsistentAfterManifestAdvances()
    {
        var localSilo = CreateSiloAddress(11111, 1);
        var remoteSilo = CreateSiloAddress(11112, 1);
        var clusterManifestProvider = new TestClusterManifestProvider(CreateClusterManifest(1, 0, localSilo));
        var manifest = new GrainVersionManifest(clusterManifestProvider);
        var snapshot = manifest.Capture();

        clusterManifestProvider.Current = CreateClusterManifest(2, 0, remoteSilo);

        Assert.Equal(new MajorMinorVersion(1, 0), snapshot.Version);
        Assert.Equal(new[] { localSilo }, snapshot.GetSupportedSilos(TestGrainType));

        var updated = manifest.Capture();
        Assert.Equal(new MajorMinorVersion(2, 0), updated.Version);
        Assert.Equal(new[] { remoteSilo }, updated.GetSupportedSilos(TestGrainType));
    }

    [Fact]
    public void GrainVersionManifest_DoesNotIntersectSilosWithDifferentIPv6Scopes()
    {
        var addressBytes = IPAddress.Parse("fe80::1").GetAddressBytes();
        var grainSilo = SiloAddress.New(new IPAddress(addressBytes, scopeid: 1), 11111, 1);
        var interfaceSilo = SiloAddress.New(new IPAddress(addressBytes, scopeid: 2), 11111, 1);
        var completeManifest = CreateGrainManifest();
        var grainOnlyManifest = new GrainManifest(
            completeManifest.Grains,
            ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty);
        var interfaceOnlyManifest = new GrainManifest(
            ImmutableDictionary<GrainType, GrainProperties>.Empty,
            completeManifest.Interfaces);
        var clusterManifest = new ClusterManifest(
            new MajorMinorVersion(1, 0),
            ImmutableDictionary.CreateRange(
            [
                new KeyValuePair<SiloAddress, GrainManifest>(grainSilo, grainOnlyManifest),
                new KeyValuePair<SiloAddress, GrainManifest>(interfaceSilo, interfaceOnlyManifest),
            ]));
        var manifest = new GrainVersionManifest(new TestClusterManifestProvider(clusterManifest));

        var result = manifest.GetSupportedSilos(
            TestGrainType,
            TestInterfaceType,
            versions: [1]);

        Assert.Empty(result.Result[1]);
    }

    [Fact]
    public async Task CachedVersionSelectorManager_ResetDoesNotPublishInFlightResult()
    {
        var silo = CreateSiloAddress(11111, 1);
        var selectorManager = CreateCachedVersionSelectorManager(
            new GrainVersionManifest(
                new TestClusterManifestProvider(CreateClusterManifest(1, 0, silo))));
        var selector = new BlockingVersionSelector();
        selectorManager.VersionSelectorManager.Default = selector;

        var firstCall = Task.Run(
            () => selectorManager.GetSuitableSilos(TestGrainType, TestInterfaceType, requestedVersion: 1),
            TestContext.Current.CancellationToken);
        await selector.Entered.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        selectorManager.ResetCache();
        selector.Release();

        Assert.Equal(new[] { silo }, (await firstCall).SuitableSilos);
        Assert.Equal(new[] { silo }, selectorManager.GetSuitableSilos(TestGrainType, TestInterfaceType, requestedVersion: 1).SuitableSilos);
        Assert.Equal(2, selector.CallCount);
    }

    [Fact]
    public async Task CachedVersionSelectorManager_SerializesRefreshesForTheSameKey()
    {
        var localSilo = CreateSiloAddress(11111, 1);
        var remoteSilo = CreateSiloAddress(11112, 1);
        var manifestProvider = new TestClusterManifestProvider(CreateClusterManifest(1, 0, localSilo));
        var selectorManager = CreateCachedVersionSelectorManager(new GrainVersionManifest(manifestProvider));
        var selector = new BlockingVersionSelector();
        selectorManager.VersionSelectorManager.Default = selector;

        var firstCall = Task.Run(
            () => selectorManager.GetSuitableSilos(TestGrainType, TestInterfaceType, requestedVersion: 1),
            TestContext.Current.CancellationToken);
        await selector.Entered.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        manifestProvider.Current = CreateClusterManifest(2, 0, remoteSilo);
        var secondCall = Task.Run(
            () => selectorManager.GetSuitableSilos(TestGrainType, TestInterfaceType, requestedVersion: 1),
            TestContext.Current.CancellationToken);
        Assert.False(secondCall.IsCompleted);
        Assert.Equal(1, selector.CallCount);

        selector.Release();

        Assert.Equal(new[] { localSilo }, (await firstCall).SuitableSilos);
        Assert.Equal(new[] { remoteSilo }, (await secondCall).SuitableSilos);
        Assert.Equal(new[] { remoteSilo }, selectorManager.GetSuitableSilos(TestGrainType, TestInterfaceType, requestedVersion: 1).SuitableSilos);
        Assert.Equal(2, selector.CallCount);
    }

    private static CachedVersionSelectorManager CreateCachedVersionSelectorManager(GrainVersionManifest manifest)
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
            new CompatibilityDirectorManager(serviceProvider, options));
    }

    private static ClusterManifest CreateClusterManifest(long major, long minor, params SiloAddress[] silos)
    {
        var manifest = CreateGrainManifest();
        return new ClusterManifest(
            new MajorMinorVersion(major, minor),
            silos.ToImmutableDictionary(silo => silo, _ => manifest));
    }

    private static async Task Until(Func<bool> condition, CancellationToken cancellationToken)
    {
        var timeout = 10_000;
        while (!condition() && (timeout -= 10) > 0)
        {
            await Task.Delay(10, cancellationToken);
        }

        Assert.True(timeout > 0);
    }

    private sealed class BlockingVersionSelector : IVersionSelector
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public Task Entered => _entered.Task;

        public int CallCount => Volatile.Read(ref _callCount);

        public ushort[] GetSuitableVersion(
            ushort requestedVersion,
            ushort[] availableVersions,
            ICompatibilityDirector compatibilityDirector)
        {
            if (Interlocked.Increment(ref _callCount) == 1)
            {
                _entered.TrySetResult();
                _release.Task.GetAwaiter().GetResult();
            }

            return availableVersions;
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class TestClusterManifestProvider(ClusterManifest initialManifest) : IClusterManifestProvider
    {
        public ClusterManifest Current { get; set; } = initialManifest;

        public IAsyncEnumerable<ClusterManifest> Updates => GetUpdates(TestContext.Current.CancellationToken);

        public GrainManifest LocalGrainManifest { get; } = CreateGrainManifest();

        private async IAsyncEnumerable<ClusterManifest> GetUpdates([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return Current;
            await Task.CompletedTask;
        }
    }
}
