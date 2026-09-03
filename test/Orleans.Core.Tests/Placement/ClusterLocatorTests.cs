using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.Configuration;
using Orleans.GrainReferences;
using Orleans.Hosting;
using Orleans.Metadata;
using Orleans.Placement;
using Orleans.Runtime;
using Orleans.Runtime.Placement;
using TestExtensions;
using Xunit;

namespace UnitTests.Placement;

[TestArea("Placement")]
[TestCategory("BVT")]
[TestSuite("BVT")]
public sealed class ClusterLocatorTests
{
    private static readonly GrainType LocatedGrainType = GrainType.Create("located");
    private static readonly GrainType LocalGrainType = GrainType.Create("local");

    [Fact]
    public void AttributePopulatesClusterLocatorProperty()
    {
        var properties = new Dictionary<string, string>();

        new ClusterLocatorAttribute("tenant-region").Populate(
            Substitute.For<IServiceProvider>(),
            typeof(ClusterLocatorTests),
            LocatedGrainType,
            properties);

        Assert.Equal("tenant-region", properties[WellKnownGrainTypeProperties.ClusterLocator]);
    }

    [Fact]
    public async Task ResolverSelectsKeyedLocator()
    {
        var locator = new TestClusterLocator("remote");
        var services = CreateServices(locator);
        var resolver = services.GetRequiredService<ClusterLocatorResolver>();
        var referenceResolver = services.GetRequiredService<ClusterReferenceResolver>();
        var reference = UniversalReference.CreateVirtual(
            new GrainId(LocatedGrainType, IdSpan.Create("key")),
            default,
            "service");

        Assert.Same(locator, resolver.Resolve(LocatedGrainType));
        var result = await referenceResolver.Resolve(
            reference,
            cancellationToken: TestContext.Current.CancellationToken);
        var cachedResult = await referenceResolver.Resolve(
            reference,
            cancellationToken: TestContext.Current.CancellationToken);
        var contextualResult = await referenceResolver.Resolve(
            reference,
            new Dictionary<string, object> { ["tenant"] = "one" },
            TestContext.Current.CancellationToken);
        Assert.Equal(new ClusterIdentity("service", "remote"), result);
        Assert.Equal(result, cachedResult);
        Assert.Equal(result, contextualResult);
        Assert.Equal(2, locator.CallCount);
    }

    [Fact]
    public async Task ReferenceWithoutLocatorResolvesLocally()
    {
        var locator = new TestClusterLocator("remote");
        var services = CreateServices(locator);
        var resolver = services.GetRequiredService<ClusterReferenceResolver>();
        var reference = UniversalReference.CreateVirtual(
            new GrainId(LocalGrainType, IdSpan.Create("key")),
            default,
            "service");

        var result = await resolver.Resolve(
            reference,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new ClusterIdentity("service", "local"), result);
        Assert.Equal(0, locator.CallCount);
    }

    [Fact]
    public void MetaclusterBindingUsesLocatorMetadata()
    {
        var services = CreateServices(new TestClusterLocator("remote"));
        var resolver = services.GetRequiredService<UniversalReferenceBindingResolver>();

        Assert.Equal(UniversalReferenceBinding.Virtual, resolver.GetBinding(LocatedGrainType));
        Assert.Equal(UniversalReferenceBinding.Cluster, resolver.GetBinding(LocalGrainType));
    }

    [Fact]
    public void MetaclusterBinding_ClientsAndSystemTargets_AreClusterBound()
    {
        var services = CreateServices(new TestClusterLocator("remote"));
        var resolver = services.GetRequiredService<UniversalReferenceBindingResolver>();
        var clientType = ClientGrainId.Create("binding-client").GrainId.Type;
        var systemTargetType = SystemTargetGrainId.CreateGrainType("binding-system-target");

        Assert.Equal(UniversalReferenceBinding.Cluster, resolver.GetBinding(clientType));
        Assert.Equal(UniversalReferenceBinding.Cluster, resolver.GetBinding(systemTargetType));
    }

    [Fact]
    public async Task RendezvousLocatorReturnsStableActiveCluster()
    {
        var topology = new MetaclusterTopology(
            "service",
            7,
            ImmutableDictionary<string, MetaclusterCluster>.Empty
                .WithComparers(System.StringComparer.Ordinal)
                .Add("a", new MetaclusterCluster("a", MetaclusterClusterState.Active, []))
                .Add("b", new MetaclusterCluster("b", MetaclusterClusterState.Active, []))
                .Add("removed", new MetaclusterCluster("removed", MetaclusterClusterState.Removed, [])));
        var locator = new RendezvousClusterLocator(new TestTopologyProvider(topology));
        var grainId = new GrainId(LocatedGrainType, IdSpan.Create("key"));
        var properties = new GrainProperties(
            ImmutableDictionary<string, string>.Empty.WithComparers(System.StringComparer.Ordinal, System.StringComparer.Ordinal));
        var context = new ClusterLocationContext("service", "a", properties);

        var first = await locator.Locate(grainId, context, TestContext.Current.CancellationToken);
        var second = await locator.Locate(grainId, context, TestContext.Current.CancellationToken);

        Assert.Equal(first, second);
        Assert.Contains(first.ClusterId, new[] { "a", "b" });
        Assert.Equal(7, first.TopologyEpoch);
    }

    [Fact]
    public async Task InMemoryDirectorySelectsOneOwnerAndMovesByVersion()
    {
        var timeProvider = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var directory = new InMemoryClusterDirectory(timeProvider);
        var grainId = new GrainId(LocatedGrainType, IdSpan.Create("owned"));

        var leaseDuration = TimeSpan.FromMinutes(1);
        var first = await directory.GetOrCreate(
            grainId,
            "a",
            topologyEpoch: 1,
            leaseDuration,
            TestContext.Current.CancellationToken);
        var concurrent = await directory.GetOrCreate(
            grainId,
            "b",
            topologyEpoch: 1,
            leaseDuration,
            TestContext.Current.CancellationToken);
        var staleMove = await directory.TryMove(
            grainId,
            first.Version + 1,
            "b",
            topologyEpoch: 2,
            leaseDuration,
            TestContext.Current.CancellationToken);
        timeProvider.Advance(leaseDuration);
        var moved = await directory.TryMove(
            grainId,
            first.Version,
            "b",
            topologyEpoch: 2,
            leaseDuration,
            TestContext.Current.CancellationToken);

        Assert.Equal(first, concurrent);
        Assert.Null(staleMove);
        Assert.NotNull(moved);
        Assert.Equal("b", moved.ClusterId);
        Assert.True(moved.Version > first.Version);
        Assert.True(moved.FencingToken > first.FencingToken);
    }

    [Fact]
    public async Task ExpiredOwnershipCanBeReacquiredWithHigherFence()
    {
        var timeProvider = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var directory = new InMemoryClusterDirectory(timeProvider);
        var grainId = new GrainId(LocatedGrainType, IdSpan.Create("expired"));
        var first = await directory.GetOrCreate(
            grainId,
            "a",
            topologyEpoch: 1,
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        timeProvider.Advance(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await directory.GetOrCreate(
                grainId,
                "b",
                topologyEpoch: 0,
                TimeSpan.FromMinutes(1),
                TestContext.Current.CancellationToken));

        var second = await directory.GetOrCreate(
            grainId,
            "b",
            topologyEpoch: 2,
            TimeSpan.FromMinutes(1),
            TestContext.Current.CancellationToken);

        Assert.Equal("b", second.ClusterId);
        Assert.True(second.Version > first.Version);
        Assert.True(second.FencingToken > first.FencingToken);
    }

    [Fact]
    public async Task NamedDirectoryLocatorsUseIndependentProviders()
    {
        var services = new ServiceCollection();
        services.AddDirectoryClusterLocator<InMemoryClusterDirectory>("one");
        services.AddDirectoryClusterLocator<InMemoryClusterDirectory>("two");
        using var serviceProvider = services.BuildServiceProvider();

        var first = serviceProvider.GetRequiredKeyedService<IClusterDirectory>("one");
        var second = serviceProvider.GetRequiredKeyedService<IClusterDirectory>("two");
        var grainId = new GrainId(LocatedGrainType, IdSpan.Create("isolated"));
        var entry = await first.GetOrCreate(
            grainId,
            "east",
            topologyEpoch: 1,
            TimeSpan.FromMinutes(1),
            TestContext.Current.CancellationToken);

        Assert.NotSame(first, second);
        Assert.Equal(entry, await first.Lookup(grainId, TestContext.Current.CancellationToken));
        Assert.Null(await second.Lookup(grainId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NamedDirectoryLocatorsResolveAgainstMatchingProviders()
    {
        var services = new ServiceCollection();
        services.AddDirectoryClusterLocator<InMemoryClusterDirectory>("one");
        services.AddDirectoryClusterLocator<InMemoryClusterDirectory>("two");
        services.AddSingleton<IMetaclusterTopologyProvider>(
            new TestTopologyProvider(
                new MetaclusterTopology(
                    "service",
                    1,
                    ImmutableDictionary<string, MetaclusterCluster>.Empty
                        .WithComparers(System.StringComparer.Ordinal)
                        .Add("local", new MetaclusterCluster("local", MetaclusterClusterState.Active, []))
                        .Add("east", new MetaclusterCluster("east", MetaclusterClusterState.Active, [])))));
        var manifestProvider = Substitute.For<IClusterManifestProvider>();
        manifestProvider.LocalGrainManifest.Returns(
            new GrainManifest(
                ImmutableDictionary<GrainType, GrainProperties>.Empty,
                ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty));
        services.AddSingleton(manifestProvider);
        services.AddSingleton<GrainPropertiesResolver>();
        services.AddSingleton<ClusterPlacementStrategyResolver>();
        services.AddSingleton<ClusterPlacementDirectorResolver>();
        services.Configure<MetaclusterOptions>(options => options.Enabled = true);
        using var serviceProvider = services.BuildServiceProvider();
        var first = serviceProvider.GetRequiredKeyedService<IClusterDirectory>("one");
        var second = serviceProvider.GetRequiredKeyedService<IClusterDirectory>("two");
        var secondLocator = serviceProvider.GetRequiredKeyedService<IClusterLocator>("two");
        var grainId = new GrainId(LocatedGrainType, IdSpan.Create("locator-isolation"));
        await first.GetOrCreate(
            grainId,
            "east",
            topologyEpoch: 1,
            TimeSpan.FromMinutes(1),
            TestContext.Current.CancellationToken);

        var location = await secondLocator.Locate(
            grainId,
            new ClusterLocationContext(
                "service",
                "local",
                new GrainProperties(
                    ImmutableDictionary<string, string>.Empty
                        .WithComparers(StringComparer.Ordinal, StringComparer.Ordinal))),
            TestContext.Current.CancellationToken);
        var firstEntry = await first.Lookup(grainId, TestContext.Current.CancellationToken);
        var secondEntry = await second.Lookup(grainId, TestContext.Current.CancellationToken);

        Assert.Equal("local", location.ClusterId);
        Assert.False(location.IsExistingOwner);
        Assert.NotNull(firstEntry);
        Assert.Equal("east", firstEntry.ClusterId);
        Assert.NotNull(secondEntry);
        Assert.Equal("local", secondEntry.ClusterId);
    }

    [Fact]
    public void InvalidLeaseConfigurationIsRejected()
    {
        var options = new MetaclusterOptions
        {
            Enabled = true,
            ClusterOwnershipLeaseDuration = TimeSpan.FromSeconds(10),
            ClusterOwnershipLeaseRenewalWindow = TimeSpan.FromSeconds(10)
        };
        var validator = new MetaclusterOptionsValidator(Options.Create(options));

        Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);
    }

    private static ServiceProvider CreateServices(IClusterLocator locator)
    {
        var locatedProperties = new GrainProperties(
            ImmutableDictionary<string, string>.Empty
                .WithComparers(System.StringComparer.Ordinal, System.StringComparer.Ordinal)
                .Add(WellKnownGrainTypeProperties.ClusterLocator, "test"));
        var localProperties = new GrainProperties(
            ImmutableDictionary<string, string>.Empty.WithComparers(System.StringComparer.Ordinal, System.StringComparer.Ordinal));
        var manifest = new GrainManifest(
            ImmutableDictionary<GrainType, GrainProperties>.Empty
                .Add(LocatedGrainType, locatedProperties)
                .Add(LocalGrainType, localProperties),
            ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty);
        var clusterManifest = new ClusterManifest(
            MajorMinorVersion.Zero,
            ImmutableDictionary<SiloAddress, GrainManifest>.Empty,
            [manifest]);
        var manifestProvider = Substitute.For<IClusterManifestProvider>();
        manifestProvider.Current.Returns(clusterManifest);
        manifestProvider.LocalGrainManifest.Returns(manifest);

        var services = new ServiceCollection();
        services.Configure<ClusterOptions>(options =>
        {
            options.ServiceId = "service";
            options.ClusterId = "local";
        });
        services.Configure<MetaclusterOptions>(options => options.Enabled = true);
        services.AddSingleton(manifestProvider);
        services.AddSingleton<GrainPropertiesResolver>();
        services.AddKeyedSingleton(TimeProviderNames.SystemTimers, TimeProvider.System);
        services.AddKeyedSingleton("test", locator);
        services.AddSingleton<IMetaclusterTopologyProvider>(
            new TestTopologyProvider(
                new MetaclusterTopology(
                    "service",
                    1,
                    ImmutableDictionary<string, MetaclusterCluster>.Empty
                        .WithComparers(System.StringComparer.Ordinal)
                        .Add("local", new MetaclusterCluster("local", MetaclusterClusterState.Active, []))
                        .Add("remote", new MetaclusterCluster("remote", MetaclusterClusterState.Active, [])))));
        services.AddSingleton<ClusterLocatorResolver>();
        services.AddSingleton<ClusterReferenceResolver>();
        services.AddSingleton<UniversalReferenceBindingResolver>();
        return services.BuildServiceProvider();
    }

    private sealed class TestClusterLocator(string clusterId) : IClusterLocator
    {
        public int CallCount { get; private set; }

        public ValueTask<ClusterLocation> Locate(
            GrainId grainId,
            ClusterLocationContext context,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return new(new ClusterLocation(clusterId, Version: 1, TopologyEpoch: 1, IsExistingOwner: false));
        }
    }

    private sealed class TestTopologyProvider(MetaclusterTopology topology) : IMetaclusterTopologyProvider
    {
        public ValueTask<MetaclusterTopology> GetTopology(CancellationToken cancellationToken = default) => new(topology);

        public async IAsyncEnumerable<MetaclusterTopology> Watch(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return topology;
            await Task.CompletedTask;
        }
    }

    [Fact]
    public void ClusterLocatorAttribute_PopulatesExpectedManifestProperty()
    {
        var properties = new Dictionary<string, string>();

        new ClusterLocatorAttribute("regional").Populate(
            Substitute.For<IServiceProvider>(),
            typeof(ClusterLocatorTests),
            LocatedGrainType,
            properties);

        Assert.Equal("regional", properties[WellKnownGrainTypeProperties.ClusterLocator]);
        Assert.Single(properties);
    }

    [Fact]
    public void ClusterPlacementAttribute_PopulatesExpectedManifestProperty()
    {
        var properties = new Dictionary<string, string>();
        var strategy = new Phase4PlacementStrategy();

        new Phase4PlacementAttribute(strategy).Populate(
            Substitute.For<IServiceProvider>(),
            typeof(ClusterLocatorTests),
            LocatedGrainType,
            properties);

        Assert.Equal(nameof(Phase4PlacementStrategy), properties[WellKnownGrainTypeProperties.ClusterPlacementStrategy]);
        Assert.Same(strategy, strategy.PopulatedBy);
    }

    [Fact]
    public void ClusterLocatorResolver_UsesNamedRegistration()
    {
        var selected = new SequenceClusterLocator(new ClusterLocation("west", 1, 1, false));
        var other = new SequenceClusterLocator(new ClusterLocation("east", 1, 1, false));
        var fixture = CreatePhase4ResolverFixture(
            locatorName: "selected",
            locators: [("selected", selected), ("other", other)]);

        var actual = fixture.LocatorResolver.Resolve(LocatedGrainType);

        Assert.Same(selected, actual);
        Assert.NotSame(other, actual);
    }

    [Fact]
    public void ClusterLocatorResolver_MissingNamedRegistration_FailsClearly()
    {
        var fixture = CreatePhase4ResolverFixture(locatorName: "missing", locators: []);

        var exception = Assert.Throws<KeyNotFoundException>(() => fixture.LocatorResolver.Resolve(LocatedGrainType));

        Assert.Contains("missing", exception.Message, StringComparison.Ordinal);
        Assert.Contains(LocatedGrainType.ToString(), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClusterReferenceResolver_VirtualReference_UsesSelectedLocator()
    {
        var locator = new SequenceClusterLocator(new ClusterLocation("west", 17, 4, false));
        var fixture = CreatePhase4ResolverFixture(
            locatorName: "selected",
            topology: Phase4Topology("service", 4, ("local", MetaclusterClusterState.Active), ("west", MetaclusterClusterState.Active)),
            locators: [("selected", locator)]);
        var reference = VirtualReference("service", "virtual");

        var actual = await fixture.ReferenceResolver.Resolve(
            reference,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new ClusterIdentity("service", "west"), actual);
        Assert.Equal(1, locator.CallCount);
        Assert.Equal(reference.GrainId, locator.LastGrainId);
    }

    [Fact]
    public async Task ClusterReferenceResolver_ClusterBoundReference_BypassesLocator()
    {
        var locator = new SequenceClusterLocator(new ClusterLocation("west", 1, 1, false));
        var fixture = CreatePhase4ResolverFixture(
            locatorName: "selected",
            topology: Phase4Topology("service", 1, ("local", MetaclusterClusterState.Active), ("bound", MetaclusterClusterState.Draining)),
            locators: [("selected", locator)]);
        var reference = UniversalReference.CreateCluster(
            GrainId.Create(LocatedGrainType, "bound-key"),
            default,
            "service",
            "bound");

        var first = await fixture.ReferenceResolver.Resolve(
            reference,
            cancellationToken: TestContext.Current.CancellationToken);
        var second = await fixture.ReferenceResolver.Resolve(
            reference,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new ClusterIdentity("service", "bound"), first);
        Assert.Equal(first, second);
        Assert.Equal(0, locator.CallCount);
    }

    [Fact]
    public async Task ClusterReferenceResolver_ClusterBoundReference_RejectsRemovedCluster()
    {
        var locator = new SequenceClusterLocator(new ClusterLocation("west", 1, 1, false));
        var fixture = CreatePhase4ResolverFixture(
            locatorName: "selected",
            topology: Phase4Topology("service", 1, ("local", MetaclusterClusterState.Active), ("removed", MetaclusterClusterState.Removed)),
            locators: [("selected", locator)]);
        var reference = UniversalReference.CreateCluster(
            GrainId.Create(LocatedGrainType, "removed-key"),
            default,
            "service",
            "removed");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.ReferenceResolver.Resolve(
                reference,
                cancellationToken: TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("unavailable cluster", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, locator.CallCount);
    }

    [Fact]
    public async Task ClusterReferenceResolver_ServiceMismatch_FailsWithoutCaching()
    {
        var locator = new SequenceClusterLocator(new ClusterLocation("west", 1, 1, false));
        var fixture = CreatePhase4ResolverFixture(locatorName: "selected", locators: [("selected", locator)]);
        var reference = VirtualReference("different-service", "mismatch");

        var first = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.ReferenceResolver.Resolve(
                reference,
                cancellationToken: TestContext.Current.CancellationToken).AsTask());
        var second = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.ReferenceResolver.Resolve(
                reference,
                cancellationToken: TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("different-service", first.Message, StringComparison.Ordinal);
        Assert.Equal(first.Message, second.Message);
        Assert.Equal(0, locator.CallCount);
    }

    [Fact]
    public async Task ClusterReferenceResolver_DisabledMetacluster_ResolvesLegacyVirtualReferenceLocally()
    {
        var locator = new SequenceClusterLocator(new ClusterLocation("west", 1, 1, false));
        var fixture = CreatePhase4ResolverFixture(
            serviceId: "configured-service",
            localClusterId: "local",
            locatorName: "selected",
            enabled: false,
            locators: [("selected", locator)]);
        var reference = VirtualReference("default", "legacy");

        var result = await fixture.ReferenceResolver.Resolve(
            reference,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new ClusterIdentity("configured-service", "local"), result);
        Assert.Equal(0, locator.CallCount);
    }

    [Fact]
    public async Task ClusterReferenceResolver_DisabledMetacluster_RejectsForeignServiceVirtualReference()
    {
        var fixture = CreatePhase4ResolverFixture(
            serviceId: "configured-service",
            localClusterId: "local",
            enabled: false);
        var reference = VirtualReference("foreign-service", "foreign");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.ReferenceResolver.Resolve(
                reference,
                cancellationToken: TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("foreign-service", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("configured-service")]
    [InlineData(ClusterOptions.DefaultServiceId)]
    public void ClusterReferenceResolver_DisabledMetacluster_RecognizesVirtualReferenceAsLocal(string serviceId)
    {
        var fixture = CreatePhase4ResolverFixture(
            serviceId: "configured-service",
            localClusterId: "local",
            enabled: false);
        var reference = VirtualReference(serviceId, "local");

        var result = fixture.ReferenceResolver.TryResolveLocal(reference, out var cluster);

        Assert.True(result);
        Assert.Equal(new ClusterIdentity("configured-service", "local"), cluster);
    }

    [Fact]
    public void ClusterReferenceResolver_ClusterBoundFastPath_ValidatesLocalIdentity()
    {
        var fixture = CreatePhase4ResolverFixture();
        var local = UniversalReference.CreateCluster(
            GrainId.Create(LocalGrainType, "local"),
            default,
            "service",
            "local");
        var remote = UniversalReference.CreateCluster(
            GrainId.Create(LocalGrainType, "remote"),
            default,
            "service",
            "west");
        var foreignService = UniversalReference.CreateCluster(
            GrainId.Create(LocalGrainType, "foreign"),
            default,
            "foreign-service",
            "local");

        Assert.True(fixture.ReferenceResolver.TryResolveLocal(local, out var cluster));
        Assert.Equal(new ClusterIdentity("service", "local"), cluster);
        Assert.False(fixture.ReferenceResolver.TryResolveLocal(remote, out _));
        var exception = Assert.Throws<InvalidOperationException>(
            () => fixture.ReferenceResolver.TryResolveLocal(foreignService, out _));
        Assert.Contains("does not match the local service", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClusterReferenceResolver_ZeroCacheDuration_DoesNotReuseResolution()
    {
        var locator = new SequenceClusterLocator(
            new ClusterLocation("west", 1, 1, false),
            new ClusterLocation("west", 2, 1, false));
        var fixture = CreatePhase4ResolverFixture(
            locatorName: "selected",
            cacheDuration: TimeSpan.Zero,
            topology: Phase4Topology("service", 1, ("local", MetaclusterClusterState.Active), ("west", MetaclusterClusterState.Active)),
            locators: [("selected", locator)]);
        var reference = VirtualReference("service", "uncached");

        Assert.Equal(
            new ClusterIdentity("service", "west"),
            await fixture.ReferenceResolver.Resolve(
                reference,
                cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(
            new ClusterIdentity("service", "west"),
            await fixture.ReferenceResolver.Resolve(
                reference,
                cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, locator.CallCount);
    }

    [Fact]
    public async Task ClusterReferenceResolver_CacheHit_ReusesOnlySameUniversalIdentity()
    {
        var locator = new SequenceClusterLocator(
            new ClusterLocation("west", 1, 1, false),
            new ClusterLocation("west", 2, 1, false));
        var fixture = CreatePhase4ResolverFixture(
            locatorName: "selected",
            topology: Phase4Topology("service", 1, ("local", MetaclusterClusterState.Active), ("west", MetaclusterClusterState.Active)),
            locators: [("selected", locator)]);
        var firstReference = VirtualReference("service", "first");
        var secondReference = VirtualReference("service", "second");

        var first = await fixture.ReferenceResolver.Resolve(
            firstReference,
            cancellationToken: TestContext.Current.CancellationToken);
        var cached = await fixture.ReferenceResolver.Resolve(
            firstReference,
            cancellationToken: TestContext.Current.CancellationToken);
        var distinct = await fixture.ReferenceResolver.Resolve(
            secondReference,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(first, cached);
        Assert.Equal(new ClusterIdentity("service", "west"), distinct);
        Assert.Equal(2, locator.CallCount);
    }

    [Fact]
    public async Task ClusterReferenceResolver_CacheEntry_ExpiresAtExactBoundary()
    {
        var locator = new SequenceClusterLocator(
            new ClusterLocation("west", 1, 1, false),
            new ClusterLocation("east", 2, 1, false));
        var fixture = CreatePhase4ResolverFixture(
            locatorName: "selected",
            cacheDuration: TimeSpan.FromMinutes(5),
            topology: Phase4Topology(
                "service",
                1,
                ("local", MetaclusterClusterState.Active),
                ("west", MetaclusterClusterState.Active),
                ("east", MetaclusterClusterState.Active)),
            locators: [("selected", locator)]);
        var reference = VirtualReference("service", "boundary");

        var first = await fixture.ReferenceResolver.Resolve(
            reference,
            cancellationToken: TestContext.Current.CancellationToken);
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        var second = await fixture.ReferenceResolver.Resolve(
            reference,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new ClusterIdentity("service", "west"), first);
        Assert.Equal(new ClusterIdentity("service", "east"), second);
        Assert.Equal(2, locator.CallCount);
    }

    [Fact]
    public async Task ClusterReferenceResolver_OwnershipValidator_IsNeverCached()
    {
        var locator = new OwnershipSequenceClusterLocator(
            new ClusterLocation("west", 1, 1, true),
            new ClusterLocation("east", 2, 1, true));
        var fixture = CreatePhase4ResolverFixture(
            locatorName: "selected",
            topology: Phase4Topology(
                "service",
                1,
                ("local", MetaclusterClusterState.Active),
                ("west", MetaclusterClusterState.Active),
                ("east", MetaclusterClusterState.Active)),
            locators: [("selected", locator)]);
        var reference = VirtualReference("service", "owned");

        var first = await fixture.ReferenceResolver.Resolve(
            reference,
            cancellationToken: TestContext.Current.CancellationToken);
        var second = await fixture.ReferenceResolver.Resolve(
            reference,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new ClusterIdentity("service", "west"), first);
        Assert.Equal(new ClusterIdentity("service", "east"), second);
        Assert.Equal(2, locator.CallCount);
    }

    [Fact]
    public async Task ClusterReferenceResolver_TopologyChange_InvalidatesStaleCacheEntry()
    {
        var locator = new SequenceClusterLocator(
            new ClusterLocation("east", 1, 1, false),
            new ClusterLocation("west", 2, 2, false));
        var topology = new Phase4MutableTopologyProvider(
            Phase4Topology("service", 1, ("east", MetaclusterClusterState.Active), ("west", MetaclusterClusterState.Active)));
        var fixture = CreatePhase4ResolverFixture(
            locatorName: "selected",
            topologyProvider: topology,
            locators: [("selected", locator)]);
        var reference = VirtualReference("service", "moving");
        var first = await fixture.ReferenceResolver.Resolve(
            reference,
            cancellationToken: TestContext.Current.CancellationToken);
        topology.Current = Phase4Topology(
            "service",
            2,
            ("east", MetaclusterClusterState.Removed),
            ("west", MetaclusterClusterState.Active));

        var second = await fixture.ReferenceResolver.Resolve(
            reference,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new ClusterIdentity("service", "east"), first);
        Assert.Equal(new ClusterIdentity("service", "west"), second);
        Assert.Equal(2, locator.CallCount);
    }

    [Fact]
    public async Task ClusterReferenceResolver_TopologyEpochChangesThreeTimes_ThrowsAfterThirdRetry()
    {
        var locator = new SequenceClusterLocator(
            new ClusterLocation("west", 1, 1, false),
            new ClusterLocation("west", 2, 2, false),
            new ClusterLocation("west", 3, 3, false));
        var topology = new SequenceTopologyProvider(
            Phase4Topology("service", 2, ("west", MetaclusterClusterState.Active)),
            Phase4Topology("service", 3, ("west", MetaclusterClusterState.Active)),
            Phase4Topology("service", 4, ("west", MetaclusterClusterState.Active)));
        var fixture = CreatePhase4ResolverFixture(
            locatorName: "selected",
            topologyProvider: topology,
            locators: [("selected", locator)]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.ReferenceResolver.Resolve(
                VirtualReference("service", "changing"),
                cancellationToken: TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("changed repeatedly", exception.Message, StringComparison.Ordinal);
        Assert.Equal(3, locator.CallCount);
        Assert.Equal(3, topology.CallCount);
    }

    [Fact]
    public async Task ClusterReferenceResolver_Cancellation_StopsRetryAndDoesNotCache()
    {
        var locator = new CancellationThenSuccessLocator();
        var fixture = CreatePhase4ResolverFixture(
            locatorName: "selected",
            topology: Phase4Topology("service", 1, ("west", MetaclusterClusterState.Active)),
            locators: [("selected", locator)]);
        using var cancellation = new CancellationTokenSource();

        var pending = fixture.ReferenceResolver.Resolve(
            VirtualReference("service", "cancelled"),
            cancellationToken: cancellation.Token).AsTask();
        await locator.FirstCallStarted.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(
            new ClusterIdentity("service", "west"),
            await fixture.ReferenceResolver.Resolve(
                VirtualReference("service", "cancelled"),
                cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, locator.CallCount);
    }

    [Fact]
    public async Task RendezvousClusterLocator_NoActiveClusters_FailsDeterministically()
    {
        var topology = Phase4Topology(
            "service",
            9,
            ("draining", MetaclusterClusterState.Draining),
            ("removed", MetaclusterClusterState.Removed));
        var locator = new RendezvousClusterLocator(new Phase4MutableTopologyProvider(topology));

        var first = await Assert.ThrowsAsync<InvalidOperationException>(
            () => locator.Locate(
                GrainId.Create(LocatedGrainType, "none"),
                Phase4Context("service"),
                TestContext.Current.CancellationToken).AsTask());
        var second = await Assert.ThrowsAsync<InvalidOperationException>(
            () => locator.Locate(
                GrainId.Create(LocatedGrainType, "none"),
                Phase4Context("service"),
                TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(first.Message, second.Message);
        Assert.Contains("'9'", first.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RendezvousClusterLocator_ExcludesDrainingAndRemovedClusters()
    {
        var topology = Phase4Topology(
            "service",
            5,
            ("active", MetaclusterClusterState.Active),
            ("draining", MetaclusterClusterState.Draining),
            ("removed", MetaclusterClusterState.Removed));
        var locator = new RendezvousClusterLocator(new Phase4MutableTopologyProvider(topology));

        var result = await locator.Locate(
            GrainId.Create(LocatedGrainType, "eligible"),
            Phase4Context("service"),
            TestContext.Current.CancellationToken);

        Assert.Equal("active", result.ClusterId);
        Assert.Equal(5, result.TopologyEpoch);
        Assert.False(result.IsExistingOwner);
    }

    [Fact]
    public async Task RendezvousClusterLocator_ServiceMismatch_Fails()
    {
        var locator = new RendezvousClusterLocator(
            new Phase4MutableTopologyProvider(Phase4Topology("topology-service", 3, ("active", MetaclusterClusterState.Active))));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => locator.Locate(
                GrainId.Create(LocatedGrainType, "mismatch"),
                Phase4Context("reference-service"),
                TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("topology-service", exception.Message, StringComparison.Ordinal);
        Assert.Contains("reference-service", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RendezvousClusterLocator_RemovingWinner_MapsToDeterministicRemainingCluster()
    {
        var provider = new Phase4MutableTopologyProvider(
            Phase4Topology("service", 1, ("east", MetaclusterClusterState.Active), ("west", MetaclusterClusterState.Active)));
        var locator = new RendezvousClusterLocator(provider);
        var grainId = GrainId.Create(LocatedGrainType, "remap");
        var winner = await locator.Locate(
            grainId,
            Phase4Context("service"),
            TestContext.Current.CancellationToken);
        var remaining = winner.ClusterId == "east" ? "west" : "east";
        provider.Current = Phase4Topology(
            "service",
            2,
            (winner.ClusterId, MetaclusterClusterState.Removed),
            (remaining, MetaclusterClusterState.Active));

        var firstRemap = await locator.Locate(
            grainId,
            Phase4Context("service"),
            TestContext.Current.CancellationToken);
        var secondRemap = await locator.Locate(
            grainId,
            Phase4Context("service"),
            TestContext.Current.CancellationToken);

        Assert.Equal(remaining, firstRemap.ClusterId);
        Assert.Equal(firstRemap, secondRemap);
        Assert.Equal(2, firstRemap.TopologyEpoch);
    }

    [Fact]
    public async Task RendezvousClusterLocator_Cancellation_IsObserved()
    {
        var provider = new Phase4MutableTopologyProvider(
            Phase4Topology("service", 1, ("active", MetaclusterClusterState.Active)));
        var locator = new RendezvousClusterLocator(provider);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => locator.Locate(GrainId.Create(LocatedGrainType, "cancel"), Phase4Context("service"), cancellation.Token).AsTask());

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(0, provider.SuccessfulCalls);
    }

    [Fact]
    public void CsCheck_RendezvousRouting_IsDeterministicForIdentityAndTopology()
    {
        CsCheck.Gen.Int.Sample(
            value =>
            {
                var grainId = GrainId.Create(LocatedGrainType, $"grain-{value}");
                var topology = Phase4Topology(
                    "service",
                    11,
                    ("alpha", MetaclusterClusterState.Active),
                    ("beta", MetaclusterClusterState.Active),
                    ("gamma", MetaclusterClusterState.Active));
                var locator = new RendezvousClusterLocator(new Phase4MutableTopologyProvider(topology));

                var first = locator.Locate(grainId, Phase4Context("service")).AsTask().GetAwaiter().GetResult();
                var second = locator.Locate(grainId, Phase4Context("service")).AsTask().GetAwaiter().GetResult();

                Assert.Equal(first, second);
                Assert.Contains(first.ClusterId, new[] { "alpha", "beta", "gamma" });
                Assert.Equal(11, first.Version);
            },
            seed: "0N0XIzNsQ0P4R1",
            iter: 100,
            threads: 1);
    }

    [Fact]
    public void CsCheck_RendezvousRouting_IsInvariantUnderTopologyOrder()
    {
        CsCheck.Gen.Int.Array[3].Sample(
            order =>
            {
                var clusters = new[] { "alpha", "beta", "gamma" };
                var firstTopology = Phase4Topology(
                    "service",
                    12,
                    clusters.Select(id => (id, MetaclusterClusterState.Active)).ToArray());
                var ordered = clusters
                    .Select((id, index) => (Id: id, Order: order[index]))
                    .OrderBy(item => item.Order)
                    .ThenBy(item => item.Id, StringComparer.Ordinal)
                    .Select(item => (item.Id, MetaclusterClusterState.Active))
                    .ToArray();
                var secondTopology = Phase4Topology("service", 12, ordered);
                var grainId = GrainId.Create(LocatedGrainType, $"ordered-{order[0]}-{order[1]}-{order[2]}");

                var first = new RendezvousClusterLocator(new Phase4MutableTopologyProvider(firstTopology))
                    .Locate(grainId, Phase4Context("service")).AsTask().GetAwaiter().GetResult();
                var second = new RendezvousClusterLocator(new Phase4MutableTopologyProvider(secondTopology))
                    .Locate(grainId, Phase4Context("service")).AsTask().GetAwaiter().GetResult();

                Assert.Equal(first, second);
                Assert.Equal(12, first.TopologyEpoch);
            },
            seed: "0N0XIzNsQ0P4R2",
            iter: 100,
            threads: 1);
    }

    [Fact]
    public void CsCheck_RendezvousRouting_MinimallyRemapsWhenNonWinnerChanges()
    {
        CsCheck.Gen.Int.Sample(
            value =>
            {
                var grainId = GrainId.Create(LocatedGrainType, $"stable-{value}");
                var provider = new Phase4MutableTopologyProvider(
                    Phase4Topology(
                        "service",
                        20,
                        ("alpha", MetaclusterClusterState.Active),
                        ("beta", MetaclusterClusterState.Active),
                        ("gamma", MetaclusterClusterState.Active)));
                var locator = new RendezvousClusterLocator(provider);
                var original = locator.Locate(grainId, Phase4Context("service")).AsTask().GetAwaiter().GetResult();
                var nonWinner = new[] { "alpha", "beta", "gamma" }.First(id => id != original.ClusterId);
                provider.Current = Phase4Topology(
                    "service",
                    21,
                    ("alpha", nonWinner == "alpha" ? MetaclusterClusterState.Removed : MetaclusterClusterState.Active),
                    ("beta", nonWinner == "beta" ? MetaclusterClusterState.Removed : MetaclusterClusterState.Active),
                    ("gamma", nonWinner == "gamma" ? MetaclusterClusterState.Removed : MetaclusterClusterState.Active));

                var after = locator.Locate(grainId, Phase4Context("service")).AsTask().GetAwaiter().GetResult();

                Assert.Equal(original.ClusterId, after.ClusterId);
                Assert.Equal(21, after.TopologyEpoch);
                Assert.NotEqual(nonWinner, after.ClusterId);
            },
            seed: "0N0XIzNsQ0P4R3",
            iter: 100,
            threads: 1);
    }

    [Fact]
    public void CsCheck_ResolverCaches_AreIsolatedByServiceClusterBindingAndNamedLocator()
    {
        CsCheck.Gen.Int.Sample(
            value =>
            {
                var eastLocator = new SequenceClusterLocator(new ClusterLocation("east", 1, 1, false));
                var westLocator = new SequenceClusterLocator(new ClusterLocation("west", 1, 1, false));
                var east = CreatePhase4ResolverFixture(
                    serviceId: "service-east",
                    localClusterId: "local-east",
                    locatorName: "east-locator",
                    topology: Phase4Topology("service-east", 1, ("east", MetaclusterClusterState.Active)),
                    locators: [("east-locator", eastLocator)]);
                var west = CreatePhase4ResolverFixture(
                    serviceId: "service-west",
                    localClusterId: "local-west",
                    locatorName: "west-locator",
                    topology: Phase4Topology("service-west", 1, ("west", MetaclusterClusterState.Active), ("bound", MetaclusterClusterState.Active)),
                    locators: [("west-locator", westLocator)]);
                var eastReference = VirtualReference("service-east", $"identity-{value}");
                var westReference = VirtualReference("service-west", $"identity-{value}");
                var boundReference = UniversalReference.CreateCluster(
                    GrainId.Create(LocatedGrainType, $"identity-{value}"),
                    default,
                    "service-west",
                    "bound");

                var eastResult = east.ReferenceResolver.Resolve(eastReference).AsTask().GetAwaiter().GetResult();
                var eastCached = east.ReferenceResolver.Resolve(eastReference).AsTask().GetAwaiter().GetResult();
                var westResult = west.ReferenceResolver.Resolve(westReference).AsTask().GetAwaiter().GetResult();
                var boundResult = west.ReferenceResolver.Resolve(boundReference).AsTask().GetAwaiter().GetResult();

                Assert.Equal(new ClusterIdentity("service-east", "east"), eastResult);
                Assert.Equal(eastResult, eastCached);
                Assert.Equal(new ClusterIdentity("service-west", "west"), westResult);
                Assert.Equal(new ClusterIdentity("service-west", "bound"), boundResult);
                Assert.Equal(1, eastLocator.CallCount);
                Assert.Equal(1, westLocator.CallCount);
            },
            seed: "0N0XIzNsQ0P4R4",
            iter: 60,
            threads: 1);
    }

    private static Phase4ResolverFixture CreatePhase4ResolverFixture(
        string serviceId = "service",
        string localClusterId = "local",
        string locatorName = "selected",
        TimeSpan? cacheDuration = null,
        bool enabled = true,
        MetaclusterTopology? topology = null,
        IMetaclusterTopologyProvider? topologyProvider = null,
        (string Name, IClusterLocator Locator)[]? locators = null)
    {
        var properties = new GrainProperties(
            ImmutableDictionary<string, string>.Empty
                .WithComparers(StringComparer.Ordinal, StringComparer.Ordinal)
                .Add(WellKnownGrainTypeProperties.ClusterLocator, locatorName));
        var manifest = new GrainManifest(
            ImmutableDictionary<GrainType, GrainProperties>.Empty.Add(LocatedGrainType, properties),
            ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty);
        var clusterManifest = new ClusterManifest(
            MajorMinorVersion.Zero,
            ImmutableDictionary<SiloAddress, GrainManifest>.Empty,
            [manifest]);
        var manifestProvider = Substitute.For<IClusterManifestProvider>();
        manifestProvider.Current.Returns(clusterManifest);
        manifestProvider.LocalGrainManifest.Returns(manifest);
        var grainPropertiesResolver = new GrainPropertiesResolver(manifestProvider);
        var services = new ServiceCollection();
        foreach (var registration in locators ?? [])
        {
            services.AddKeyedSingleton(registration.Name, registration.Locator);
        }

        var serviceProvider = services.BuildServiceProvider();
        var locatorResolver = new ClusterLocatorResolver(grainPropertiesResolver, serviceProvider);
        var effectiveTopologyProvider = topologyProvider
            ?? new Phase4MutableTopologyProvider(
                topology ?? Phase4Topology(serviceId, 1, (localClusterId, MetaclusterClusterState.Active), ("west", MetaclusterClusterState.Active)));
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(
            new DateTimeOffset(2040, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var clusterOptions = Options.Create(new ClusterOptions { ServiceId = serviceId, ClusterId = localClusterId });
        var metaclusterOptions = Options.Create(new MetaclusterOptions
        {
            Enabled = enabled,
            ClusterLocationCacheDuration = cacheDuration ?? TimeSpan.FromMinutes(5)
        });
        var resolver = new ClusterReferenceResolver(
            clusterOptions,
            metaclusterOptions,
            locatorResolver,
            grainPropertiesResolver,
            effectiveTopologyProvider,
            clock);
        return new(locatorResolver, resolver, serviceProvider, clock);
    }

    private static UniversalReference VirtualReference(string serviceId, string key)
        => UniversalReference.CreateVirtual(GrainId.Create(LocatedGrainType, key), default, serviceId);

    private static ClusterLocationContext Phase4Context(string serviceId)
        => new(
            serviceId,
            "local",
            new GrainProperties(ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal, StringComparer.Ordinal)));

    private static MetaclusterTopology Phase4Topology(
        string serviceId,
        long epoch,
        params (string Id, MetaclusterClusterState State)[] clusters)
        => new(
            serviceId,
            epoch,
            clusters.ToImmutableDictionary(
                cluster => cluster.Id,
                cluster => new MetaclusterCluster(cluster.Id, cluster.State, []),
                StringComparer.Ordinal));

    private sealed record Phase4ResolverFixture(
        ClusterLocatorResolver LocatorResolver,
        ClusterReferenceResolver ReferenceResolver,
        ServiceProvider Services,
        Microsoft.Extensions.Time.Testing.FakeTimeProvider Clock);

    private sealed class Phase4PlacementStrategy : ClusterPlacementStrategy
    {
        public Phase4PlacementStrategy? PopulatedBy { get; private set; }

        public override void PopulateGrainProperties(
            IServiceProvider services,
            Type grainClass,
            GrainType grainType,
            Dictionary<string, string> properties)
        {
            base.PopulateGrainProperties(services, grainClass, grainType, properties);
            PopulatedBy = this;
        }
    }

    private sealed class Phase4PlacementAttribute(ClusterPlacementStrategy strategy) : ClusterPlacementAttribute(strategy);

    private sealed class SequenceClusterLocator(params ClusterLocation[] locations) : IClusterLocator
    {
        public int CallCount { get; private set; }

        public GrainId LastGrainId { get; private set; }

        public ValueTask<ClusterLocation> Locate(
            GrainId grainId,
            ClusterLocationContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastGrainId = grainId;
            var index = Math.Min(CallCount, locations.Length - 1);
            CallCount++;
            return new(locations[index]);
        }
    }

    private sealed class OwnershipSequenceClusterLocator(params ClusterLocation[] locations)
        : IClusterLocator, IClusterOwnershipValidator
    {
        public int CallCount { get; private set; }

        public ValueTask<ClusterLocation> Locate(
            GrainId grainId,
            ClusterLocationContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = Math.Min(CallCount, locations.Length - 1);
            CallCount++;
            return new(locations[index]);
        }

        public ValueTask<ClusterDirectoryEntry> ValidateLocalOwnership(
            GrainId grainId,
            string localClusterId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class CancellationThenSuccessLocator : IClusterLocator
    {
        public TaskCompletionSource FirstCallStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount { get; private set; }

        public async ValueTask<ClusterLocation> Locate(
            GrainId grainId,
            ClusterLocationContext context,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (CallCount == 1)
            {
                FirstCallStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return new ClusterLocation("west", 1, 1, false);
        }
    }

    private sealed class Phase4MutableTopologyProvider(MetaclusterTopology current) : IMetaclusterTopologyProvider
    {
        public MetaclusterTopology Current { get; set; } = current;

        public int SuccessfulCalls { get; private set; }

        public ValueTask<MetaclusterTopology> GetTopology(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SuccessfulCalls++;
            return new(Current);
        }

        public async IAsyncEnumerable<MetaclusterTopology> Watch(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return Current;
            await Task.CompletedTask;
        }
    }

    private sealed class SequenceTopologyProvider(params MetaclusterTopology[] topologies) : IMetaclusterTopologyProvider
    {
        public int CallCount { get; private set; }

        public ValueTask<MetaclusterTopology> GetTopology(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = Math.Min(CallCount, topologies.Length - 1);
            CallCount++;
            return new(topologies[index]);
        }

        public async IAsyncEnumerable<MetaclusterTopology> Watch(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return topologies[0];
            await Task.CompletedTask;
        }
    }

    [Fact]
    [TestProvider("None")]
    public void CsCheck_RendezvousRouting_DeterminismMatchesOrderIndependentOracle()
    {
        CsCheck.Gen.Int.Array[6].Sample(
            values =>
            {
                var grainId = GrainId.Create(LocatedGrainType, $"deterministic-{values[0]}");
                var clusters = new[]
                {
                    ("alpha", MetaclusterClusterState.Active),
                    ("beta", MetaclusterClusterState.Active),
                    ("gamma", MetaclusterClusterState.Active),
                    ("draining", MetaclusterClusterState.Draining),
                    ("removed", MetaclusterClusterState.Removed)
                };
                var reordered = clusters
                    .Select((cluster, index) => (Cluster: cluster, Order: values[index + 1]))
                    .OrderBy(item => item.Order)
                    .ThenBy(item => item.Cluster.Item1, StringComparer.Ordinal)
                    .Select(item => item.Cluster)
                    .ToArray();
                var first = Locate(
                    new RendezvousClusterLocator(
                        new Phase4MutableTopologyProvider(Phase4Topology("service", 44, clusters))),
                    grainId);
                var second = Locate(
                    new RendezvousClusterLocator(
                        new Phase4MutableTopologyProvider(Phase4Topology("service", 44, reordered))),
                    grainId);
                var expected = ExpectedWinner(grainId, ["alpha", "beta", "gamma"]);
                var history = $"values=[{string.Join(",", values)}]; expected={expected}; first={first}; second={second}";

                Assert.True(first == second, history);
                Assert.True(first.ClusterId == expected, history);
                Assert.Equal(44, first.Version);
                Assert.Equal(44, first.TopologyEpoch);
                Assert.False(first.IsExistingOwner);
            },
            seed: "0N0XIzNsQ0P2R1",
            iter: 160,
            threads: 1,
            print: static values => $"grain-and-order=[{string.Join(",", values)}]");
    }

    [Fact]
    [TestProvider("None")]
    public void RendezvousClusterLocator_EqualScoresUseOrdinalClusterIdTieBreak()
    {
        var grainId = GrainId.Create(LocatedGrainType, "collision");
        var grainHash = grainId.GetUniformHashCode();
        var scores = new Dictionary<uint, string>();
        string? firstCluster = null;
        string? secondCluster = null;
        uint value = 0xC0FFEEu;
        for (var index = 0; index < 1_000_000 && firstCluster is null; index++)
        {
            value = unchecked((value * 1_664_525u) + 1_013_904_223u);
            var candidate = $"collision-{value:X8}";
            var score = StableHash.ComputeHash($"{grainHash:X8}:{candidate}");
            if (scores.TryGetValue(score, out var collision))
            {
                firstCluster = string.CompareOrdinal(collision, candidate) < 0 ? collision : candidate;
                secondCluster = string.CompareOrdinal(collision, candidate) < 0 ? candidate : collision;
            }
            else
            {
                scores.Add(score, candidate);
            }
        }

        Assert.NotNull(firstCluster);
        Assert.NotNull(secondCluster);
        var first = Locate(
            new RendezvousClusterLocator(
                new Phase4MutableTopologyProvider(
                    Phase4Topology(
                        "service",
                        45,
                        (firstCluster, MetaclusterClusterState.Active),
                        (secondCluster, MetaclusterClusterState.Active)))),
            grainId);
        var second = Locate(
            new RendezvousClusterLocator(
                new Phase4MutableTopologyProvider(
                    Phase4Topology(
                        "service",
                        45,
                        (secondCluster, MetaclusterClusterState.Active),
                        (firstCluster, MetaclusterClusterState.Active)))),
            grainId);

        Assert.Equal(firstCluster, first.ClusterId);
        Assert.Equal(first, second);
    }

    [Fact]
    [TestProvider("None")]
    public void CsCheck_RendezvousRouting_RemovalRemapsOnlyRemovedWinner()
    {
        CsCheck.Gen.Int.Sample(
            value =>
            {
                var clusterIds = new[] { "alpha", "beta", "gamma", "delta" };
                var beforeLocator = new RendezvousClusterLocator(
                    new Phase4MutableTopologyProvider(
                        Phase4Topology(
                            "service",
                            30,
                            clusterIds.Select(id => (id, MetaclusterClusterState.Active)).ToArray())));
                var anchor = GrainId.Create(LocatedGrainType, $"anchor-{value}");
                var removedCluster = Locate(beforeLocator, anchor).ClusterId;
                var afterLocator = new RendezvousClusterLocator(
                    new Phase4MutableTopologyProvider(
                        Phase4Topology(
                            "service",
                            31,
                            clusterIds
                                .Select(id => (
                                    id,
                                    id == removedCluster
                                        ? MetaclusterClusterState.Removed
                                        : MetaclusterClusterState.Active))
                                .ToArray())));
                var grains = Enumerable.Range(0, 64)
                    .Select(index => GrainId.Create(LocatedGrainType, $"remapping-{value}-{index}"))
                    .Prepend(anchor)
                    .ToArray();
                var ownedByRemoved = 0;
                var remapped = 0;
                var unchanged = 0;

                foreach (var grainId in grains)
                {
                    var before = Locate(beforeLocator, grainId);
                    var after = Locate(afterLocator, grainId);
                    var history = $"value={value}; removed={removedCluster}; grain={grainId}; before={before}; after={after}";

                    Assert.True(after.ClusterId != removedCluster, history);
                    Assert.True(clusterIds.Contains(after.ClusterId, StringComparer.Ordinal), history);
                    Assert.Equal(31, after.TopologyEpoch);
                    Assert.False(after.IsExistingOwner);

                    if (before.ClusterId == removedCluster)
                    {
                        ownedByRemoved++;
                        remapped++;
                        Assert.True(before.ClusterId != after.ClusterId, history);
                    }
                    else
                    {
                        unchanged++;
                        Assert.True(before.ClusterId == after.ClusterId, history);
                    }
                }

                Assert.True(ownedByRemoved > 0, $"value={value}; removed={removedCluster}; no owned grains");
                Assert.Equal(ownedByRemoved, remapped);
                Assert.Equal(grains.Length - ownedByRemoved, unchanged);
            },
            seed: "0N0XIzNsQ0P2R2",
            iter: 80,
            threads: 1,
            print: static value => $"grain-seed={value}");
    }

    [Fact]
    [TestProvider("None")]
    public void CsCheck_ResolverCaches_IsolateNamedLocatorsBindingsAndRequestContexts()
    {
        CsCheck.Gen.Int.Sample(
            value =>
            {
                using var fixture = CreateContextResolverFixture();
                var eastReference = UniversalReference.CreateVirtual(
                    GrainId.Create(fixture.EastType, $"shared-{value}"),
                    default,
                    "service");
                var westReference = UniversalReference.CreateVirtual(
                    GrainId.Create(fixture.WestType, $"shared-{value}"),
                    default,
                    "service");
                var boundReference = UniversalReference.CreateCluster(
                    eastReference.GrainId,
                    default,
                    "service",
                    "bound");
                var firstTenant = value % 2 == 0 ? "north" : "west";
                var secondTenant = firstTenant == "north" ? "west" : "north";

                var east = Resolve(fixture.Resolver, eastReference);
                var eastCached = Resolve(fixture.Resolver, eastReference);
                var west = Resolve(fixture.Resolver, westReference);
                var firstContextual = Resolve(
                    fixture.Resolver,
                    eastReference,
                    new Dictionary<string, object> { ["tenant"] = firstTenant });
                var secondContextual = Resolve(
                    fixture.Resolver,
                    eastReference,
                    new Dictionary<string, object> { ["tenant"] = secondTenant });
                var eastStillCached = Resolve(fixture.Resolver, eastReference);
                var bound = Resolve(fixture.Resolver, boundReference);
                var history = $"value={value}; contexts={firstTenant},{secondTenant}; "
                    + $"east={east}; west={west}; first={firstContextual}; second={secondContextual}; bound={bound}";

                Assert.True(east == new ClusterIdentity("service", "east"), history);
                Assert.True(eastCached == east && eastStillCached == east, history);
                Assert.True(west == new ClusterIdentity("service", "west"), history);
                Assert.True(firstContextual == new ClusterIdentity("service", firstTenant), history);
                Assert.True(secondContextual == new ClusterIdentity("service", secondTenant), history);
                Assert.True(bound == new ClusterIdentity("service", "bound"), history);
                Assert.Equal(new string?[] { null, firstTenant, secondTenant }, fixture.EastLocator.Tenants);
                Assert.Equal(new string?[] { null }, fixture.WestLocator.Tenants);
            },
            seed: "0N0XIzNsQ0P2R3",
            iter: 64,
            threads: 1,
            print: static value => $"reference-key={value}");
    }

    private static ClusterLocation Locate(RendezvousClusterLocator locator, GrainId grainId) =>
        locator.Locate(grainId, Phase4Context("service")).AsTask().GetAwaiter().GetResult();

    private static ClusterIdentity Resolve(
        ClusterReferenceResolver resolver,
        UniversalReference reference,
        IReadOnlyDictionary<string, object>? requestContext = null) =>
        resolver.Resolve(reference, requestContext).AsTask().GetAwaiter().GetResult();

    private static string ExpectedWinner(GrainId grainId, IEnumerable<string> clusterIds) =>
        clusterIds
            .Select(clusterId => (
                ClusterId: clusterId,
                Score: StableHash.ComputeHash($"{grainId.GetUniformHashCode():X8}:{clusterId}")))
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.ClusterId, StringComparer.Ordinal)
            .First()
            .ClusterId;

    private static ContextResolverFixture CreateContextResolverFixture()
    {
        var eastType = GrainType.Create("phase2.cache.east");
        var westType = GrainType.Create("phase2.cache.west");
        var eastProperties = new GrainProperties(
            ImmutableDictionary<string, string>.Empty
                .WithComparers(StringComparer.Ordinal, StringComparer.Ordinal)
                .Add(WellKnownGrainTypeProperties.ClusterLocator, "east-locator"));
        var westProperties = new GrainProperties(
            ImmutableDictionary<string, string>.Empty
                .WithComparers(StringComparer.Ordinal, StringComparer.Ordinal)
                .Add(WellKnownGrainTypeProperties.ClusterLocator, "west-locator"));
        var manifest = new GrainManifest(
            ImmutableDictionary<GrainType, GrainProperties>.Empty
                .Add(eastType, eastProperties)
                .Add(westType, westProperties),
            ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty);
        var clusterManifest = new ClusterManifest(
            MajorMinorVersion.Zero,
            ImmutableDictionary<SiloAddress, GrainManifest>.Empty,
            [manifest]);
        var manifestProvider = Substitute.For<IClusterManifestProvider>();
        manifestProvider.Current.Returns(clusterManifest);
        manifestProvider.LocalGrainManifest.Returns(manifest);
        var propertiesResolver = new GrainPropertiesResolver(manifestProvider);
        var eastLocator = new ContextSensitiveClusterLocator("east");
        var westLocator = new ContextSensitiveClusterLocator("west");
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IClusterLocator>("east-locator", eastLocator);
        services.AddKeyedSingleton<IClusterLocator>("west-locator", westLocator);
        var serviceProvider = services.BuildServiceProvider();
        var locatorResolver = new ClusterLocatorResolver(propertiesResolver, serviceProvider);
        var topology = Phase4Topology(
            "service",
            1,
            ("local", MetaclusterClusterState.Active),
            ("east", MetaclusterClusterState.Active),
            ("west", MetaclusterClusterState.Active),
            ("north", MetaclusterClusterState.Active),
            ("bound", MetaclusterClusterState.Active));
        var resolver = new ClusterReferenceResolver(
            Options.Create(new ClusterOptions { ServiceId = "service", ClusterId = "local" }),
            Options.Create(new MetaclusterOptions
            {
                Enabled = true,
                ClusterLocationCacheDuration = TimeSpan.FromMinutes(5)
            }),
            locatorResolver,
            propertiesResolver,
            new Phase4MutableTopologyProvider(topology),
            new Microsoft.Extensions.Time.Testing.FakeTimeProvider());
        return new(eastType, westType, resolver, eastLocator, westLocator, serviceProvider);
    }

    private sealed class ContextSensitiveClusterLocator(string defaultCluster) : IClusterLocator
    {
        public List<string?> Tenants { get; } = [];

        public ValueTask<ClusterLocation> Locate(
            GrainId grainId,
            ClusterLocationContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tenant = context.RequestContext is not null
                && context.RequestContext.TryGetValue("tenant", out var value)
                    ? value as string
                    : null;
            Tenants.Add(tenant);
            return new(new ClusterLocation(tenant ?? defaultCluster, 1, 1, false));
        }
    }

    private sealed record ContextResolverFixture(
        GrainType EastType,
        GrainType WestType,
        ClusterReferenceResolver Resolver,
        ContextSensitiveClusterLocator EastLocator,
        ContextSensitiveClusterLocator WestLocator,
        ServiceProvider Services) : IDisposable
    {
        public void Dispose() => Services.Dispose();
    }
}
