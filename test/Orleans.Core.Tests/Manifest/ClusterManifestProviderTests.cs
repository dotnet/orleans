using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
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
        var remoteManifest = CreateGrainManifest();
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

            membership.Update(CreateMembershipSnapshot(
                2,
                (localSilo, SiloStatus.Active),
                (remoteSilo, SiloStatus.ShuttingDown)));

            var current = provider.Current;

            Assert.Equal(new MajorMinorVersion(2, 0), current.Version);
            Assert.Contains(localSilo, current.Silos.Keys);
            Assert.DoesNotContain(remoteSilo, current.Silos.Keys);
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

    private static GrainManifest CreateGrainManifest()
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
                    new KeyValuePair<string, string>(WellKnownGrainInterfaceProperties.Version, "1")
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

        public TestClusterMembershipService(ClusterMembershipSnapshot initialSnapshot)
        {
            _updates = new AsyncEnumerable<ClusterMembershipSnapshot>(
                initialValue: initialSnapshot,
                updateValidator: (previous, proposed) => proposed.Version > previous.Version,
                onPublished: update => Volatile.Write(ref _currentSnapshot, update));
        }

        public ClusterMembershipSnapshot CurrentSnapshot
        {
            get => Volatile.Read(ref _currentSnapshot);
        }

        public IAsyncEnumerable<ClusterMembershipSnapshot> MembershipUpdates => _updates;

        public void Update(ClusterMembershipSnapshot snapshot) => _updates.Publish(snapshot);

        public ValueTask Refresh(MembershipVersion minimumVersion = default, CancellationToken cancellationToken = default) => default;

        public Task<bool> TryKill(SiloAddress siloAddress) => Task.FromResult(false);

        public void Dispose() => _updates.Dispose();
    }

    private sealed class TestSiloManifestSystemTarget(GrainManifest manifest) : ISiloManifestSystemTarget
    {
        public ValueTask<GrainManifest> GetSiloManifest(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(manifest);
        }
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

        public IAsyncEnumerable<ClusterManifest> Updates => GetUpdates();

        public GrainManifest LocalGrainManifest { get; } = CreateGrainManifest();

        private async IAsyncEnumerable<ClusterManifest> GetUpdates()
        {
            yield return Current;
            await Task.CompletedTask;
        }
    }
}
