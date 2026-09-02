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
using Microsoft.Extensions.Time.Testing;
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
            services,
            TimeProvider.System);
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
                getHashSummary: () =>
                {
                    requestLog.RecordProbe(peer);
                    var active = Interlocked.Increment(ref activeProbeCount);
                    UpdateMaximum(ref maximumActiveProbeCount, active);
                    if (peer == healthyPeer)
                    {
                        Interlocked.Decrement(ref activeProbeCount);
                        return Task.FromResult(summary);
                    }

                    return AwaitProbeAsync(hungProbeCompletions[peer].Task, () => Interlocked.Decrement(ref activeProbeCount));
                },
                getUpdate: version =>
                {
                    if (peer == healthyPeer)
                    {
                        healthyUpdateRequested.TrySetResult(version);
                    }

                    return Task.FromResult<ClusterManifestUpdate?>(update);
                },
                getLegacyManifest: () =>
                {
                    requestLog.RecordLegacyFetch(peer);
                    return directFetchRelease.Task;
                }));
        var grainFactory = CreateGrainFactory(targets);
        var membership = new TestClusterMembershipService(CreateActiveMembershipSnapshot(1, localSilo, peers));
        var provider = CreateClusterManifestProvider(localSilo, membership, grainFactory, timeProvider, logger);
        var repairedManifest = ObserveManifestAsync(provider, new MajorMinorVersion(1, 1));
        var lifecycle = await StartAsync(provider);

        try
        {
            await Task.WhenAll(requestLog.WaitForProbeCountAsync(3), requestLog.WaitForLegacyFetchCountAsync(peers.Length), healthyUpdateRequested.Task);
            var requestedVersion = await healthyUpdateRequested.Task;

            Assert.Equal(selectedPeers, requestLog.ProbeAddresses);
            Assert.Equal(3, maximumActiveProbeCount);
            Assert.Equal(3, requestLog.ProbeAddresses.Count);
            Assert.Equal(MajorMinorVersion.MinValue, requestedVersion);

            timeProvider.Advance(TimeSpan.FromSeconds(1));
            await logger.WaitForTimeoutCountAsync(2);
            Assert.Equal(2, logger.TimeoutCount);
            Assert.Equal(3, requestLog.ProbeAddresses.Count);

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
                getHashSummary: () =>
                {
                    requestLog.RecordProbe(peer);
                    return Task.FromResult(summary);
                },
                getUpdate: _ => Task.FromResult<ClusterManifestUpdate?>(update),
                getLegacyManifest: () =>
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
        var repairedManifest = ObserveManifestAsync(provider, new MajorMinorVersion(1, 1));
        var lifecycle = await StartAsync(provider);

        try
        {
            await Task.WhenAll(
                requestLog.WaitForProbeCountAsync(peers.Length),
                requestLog.WaitForLegacyFetchCountAsync(peers.Length));

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
                getHashSummary: () =>
                {
                    requestLog.RecordProbe(peer);
                    return pendingSummary.Task;
                },
                getUpdate: _ => Task.FromResult<ClusterManifestUpdate?>(null),
                getLegacyManifest: () =>
                {
                    requestLog.RecordLegacyFetch(peer);
                    return pendingLegacyFetch.Task;
                }));
        var grainFactory = CreateGrainFactory(targets);
        var membership = new TestClusterMembershipService(CreateActiveMembershipSnapshot(1, localSilo, peers));
        var provider = CreateClusterManifestProvider(localSilo, membership, grainFactory, new FakeTimeProvider(), NullLogger<ClusterManifestProvider>.Instance);
        var lifecycle = await StartAsync(provider);
        var stopped = false;

        try
        {
            await Task.WhenAll(requestLog.WaitForProbeCountAsync(3), requestLog.WaitForLegacyFetchCountAsync(peers.Length));

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
                getHashSummary: () =>
                {
                    requestLog.RecordProbe(peer);
                    return pendingSummary.Task;
                },
                getUpdate: _ => Task.FromResult<ClusterManifestUpdate?>(null),
                getLegacyManifest: () =>
                {
                    requestLog.RecordLegacyFetch(peer);
                    return legacyFetchRelease.Task;
                }));
        var grainFactory = CreateGrainFactory(targets);
        var membership = new TestClusterMembershipService(CreateActiveMembershipSnapshot(1, localSilo, peers));
        var provider = CreateClusterManifestProvider(localSilo, membership, grainFactory, new FakeTimeProvider(), NullLogger<ClusterManifestProvider>.Instance);
        var lifecycle = await StartAsync(provider);

        try
        {
            await Task.WhenAll(requestLog.WaitForProbeCountAsync(3), requestLog.WaitForLegacyFetchCountAsync(peers.Length));

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
                getHashSummary: () =>
                {
                    requestLog.RecordProbe(peer);
                    return Task.FromResult(summary);
                },
                getUpdate: version =>
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
                getLegacyManifest: () =>
                {
                    requestLog.RecordLegacyFetch(peer);
                    return directFetchRelease.Task;
                }));
        var grainFactory = CreateGrainFactory(targets);
        var membership = new TestClusterMembershipService(CreateActiveMembershipSnapshot(1, localSilo, peers));
        var provider = CreateClusterManifestProvider(localSilo, membership, grainFactory, new FakeTimeProvider(), NullLogger<ClusterManifestProvider>.Instance);
        var repairedManifest = ObserveManifestAsync(provider, new MajorMinorVersion(1, 1));
        var lifecycle = await StartAsync(provider);

        try
        {
            await Task.WhenAll(requestLog.WaitForLegacyFetchCountAsync(peers.Length), updateRequestsStarted.Task);

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
                getHashSummary: () =>
                {
                    requestLog.RecordProbe(peer);
                    return Task.FromResult(emptySummary);
                },
                getUpdate: _ => Task.FromResult<ClusterManifestUpdate?>(emptyUpdate),
                getLegacyManifest: () => Task.FromException<GrainManifest>(new InvalidOperationException("Direct fetch intentionally unavailable."))));
        var grainFactory = CreateGrainFactory(targets);
        var membership = new TestClusterMembershipService(CreateActiveMembershipSnapshot(1, localSilo, peers));
        var provider = CreateClusterManifestProvider(localSilo, membership, grainFactory, new FakeTimeProvider(), NullLogger<ClusterManifestProvider>.Instance);
        var lifecycle = await StartAsync(provider);

        try
        {
            await requestLog.WaitForProbeCountAsync(3);
            var secondAttemptStarted = requestLog.WaitForProbeCountAsync(6);
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

    private static ClusterManifestProvider CreateClusterManifestProvider(
        SiloAddress localSilo,
        TestClusterMembershipService membership,
        IInternalGrainFactory grainFactory,
        TimeProvider timeProvider,
        ILogger<ClusterManifestProvider> logger)
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
            logger,
            services,
            timeProvider);
    }

    private static ClusterMembershipSnapshot CreateActiveMembershipSnapshot(
        long version,
        SiloAddress localSilo,
        SiloAddress[] peers)
    {
        var members = new (SiloAddress SiloAddress, SiloStatus Status)[peers.Length + 1];
        members[0] = (localSilo, SiloStatus.Active);
        for (var index = 0; index < peers.Length; index++)
        {
            members[index + 1] = (peers[index], SiloStatus.Active);
        }

        return CreateMembershipSnapshot(version, members);
    }

    private static IInternalGrainFactory CreateGrainFactory(
        IReadOnlyDictionary<SiloAddress, TestClusterManifestSystemTarget> targets)
    {
        var grainFactory = Substitute.For<IInternalGrainFactory>();
        foreach (var (siloAddress, target) in targets)
        {
            grainFactory
                .GetSystemTarget<IClusterManifestSystemTarget>(Constants.ManifestProviderType, siloAddress)
                .Returns(target);
            grainFactory
                .GetSystemTarget<ISiloManifestSystemTarget>(Constants.ManifestProviderType, siloAddress)
                .Returns(target);
        }

        return grainFactory;
    }

    private static async Task<ClusterManifest> ObserveManifestAsync(
        ClusterManifestProvider provider,
        MajorMinorVersion expectedVersion)
    {
        await using var updates = provider.Updates.GetAsyncEnumerator();
        while (await updates.MoveNextAsync())
        {
            if (updates.Current.Version >= expectedVersion)
            {
                return updates.Current;
            }
        }

        throw new InvalidOperationException($"The manifest update stream ended before version {expectedVersion} was published.");
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
        Action onCompleted)
    {
        try
        {
            return await task;
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

    private sealed class TestClusterManifestSystemTarget(
        Func<Task<ClusterManifestHashSummary>> getHashSummary,
        Func<MajorMinorVersion, Task<ClusterManifestUpdate?>> getUpdate,
        Func<Task<GrainManifest>> getLegacyManifest) : IClusterManifestSystemTarget, ISiloManifestSystemTarget
    {
        public ValueTask<ClusterManifest> GetClusterManifest() => ValueTask.FromException<ClusterManifest>(
            new NotSupportedException("This test target only supports peer repair requests."));

        public ValueTask<ClusterManifestUpdate?> GetClusterManifestUpdate(MajorMinorVersion previousVersion) =>
            new(getUpdate(previousVersion));

        public ValueTask<ClusterManifestHashSummary> GetClusterManifestHashSummary() => new(getHashSummary());

        public ValueTask<ManifestHash> GetSiloManifestHash() => ValueTask.FromException<ManifestHash>(
            new InvalidOperationException("Use the legacy manifest fetch path."));

        public ValueTask<GrainManifest?> GetSiloManifestByHash(ManifestHash hash) => new((GrainManifest?)null);

        public ValueTask<GrainManifest> GetSiloManifest() => new(getLegacyManifest());
    }

    private sealed class ManifestRequestLog(int expectedProbeCount, int expectedLegacyFetchCount)
    {
        private readonly object _lock = new();
        private readonly List<SiloAddress> _probeAddresses = [];
        private readonly List<SiloAddress> _legacyFetchAddresses = [];
        private readonly Dictionary<int, TaskCompletionSource> _probeWaiters = [];
        private readonly Dictionary<int, TaskCompletionSource> _legacyFetchWaiters = [];

        public IReadOnlyList<SiloAddress> ProbeAddresses
        {
            get
            {
                lock (_lock)
                {
                    return _probeAddresses.ToArray();
                }
            }
        }

        public IReadOnlyList<SiloAddress> LegacyFetchAddresses
        {
            get
            {
                lock (_lock)
                {
                    return _legacyFetchAddresses.ToArray();
                }
            }
        }

        public void RecordProbe(SiloAddress address)
        {
            lock (_lock)
            {
                _probeAddresses.Add(address);
                CompleteWaiters(_probeWaiters, _probeAddresses.Count);
            }
        }

        public void RecordLegacyFetch(SiloAddress address)
        {
            lock (_lock)
            {
                _legacyFetchAddresses.Add(address);
                CompleteWaiters(_legacyFetchWaiters, _legacyFetchAddresses.Count);
            }
        }

        public Task WaitForProbeCountAsync(int count) => WaitForCountAsync(_probeWaiters, _probeAddresses, count, expectedProbeCount);

        public Task WaitForLegacyFetchCountAsync(int count) =>
            WaitForCountAsync(_legacyFetchWaiters, _legacyFetchAddresses, count, expectedLegacyFetchCount);

        private Task WaitForCountAsync(
            Dictionary<int, TaskCompletionSource> waiters,
            List<SiloAddress> addresses,
            int count,
            int expectedCount)
        {
            lock (_lock)
            {
                Assert.True(count <= expectedCount || expectedCount == 0, $"Expected no more than {expectedCount} requests, but waited for {count}.");
                if (addresses.Count >= count)
                {
                    return Task.CompletedTask;
                }

                if (!waiters.TryGetValue(count, out var completion))
                {
                    completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    waiters.Add(count, completion);
                }

                return completion.Task;
            }
        }

        private static void CompleteWaiters(Dictionary<int, TaskCompletionSource> waiters, int count)
        {
            foreach (var (expectedCount, completion) in waiters)
            {
                if (count >= expectedCount)
                {
                    completion.TrySetResult();
                }
            }
        }
    }

    private sealed class PeerProbeLogger(int expectedTimeoutCount) : ILogger<ClusterManifestProvider>
    {
        private readonly TaskCompletionSource _timeoutsObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _timeoutCount;

        public int TimeoutCount => Volatile.Read(ref _timeoutCount);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).StartsWith("Cluster manifest peer probe to ", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref _timeoutCount) == expectedTimeoutCount)
                {
                    _timeoutsObserved.TrySetResult();
                }
            }
        }

        public Task WaitForTimeoutCountAsync(int count)
        {
            Assert.Equal(expectedTimeoutCount, count);
            return _timeoutsObserved.Task;
        }
    }
}
