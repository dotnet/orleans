using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Runtime.Placement;
using TestExtensions;
using Xunit;

namespace UnitTests.Placement;

[Collection(TestEnvironmentFixture.DefaultCollection)]
[TestArea("Placement")]
[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[Trait("Phase", "4")]
[Trait("FullyQualifiedName", "UnitTests.Placement.ClusterPlacementTests")]
public sealed class ClusterPlacementTests(TestEnvironmentFixture environment)
{
    private static readonly GrainType GrainType = GrainType.Create("phase4.placement");
    private static readonly GrainId GrainId = new(GrainType, IdSpan.Create("grain"));

    [Fact]
    public void ClusterPlacementStrategy_PropertiesRoundTripThroughSerialization()
    {
        var strategy = new TestStrategy();
        var values = new Dictionary<string, string> { ["unrelated"] = "preserved" };
        strategy.PopulateGrainProperties(
            Substitute.For<IServiceProvider>(),
            typeof(ClusterPlacementTests),
            GrainType,
            values);
        var expected = new GrainProperties(values.ToImmutableDictionary(StringComparer.Ordinal));

        var payload = environment.Serializer.SerializeToArray(expected);
        var actual = environment.Serializer.Deserialize<GrainProperties>(payload);

        Assert.NotNull(actual);
        Assert.Equal(nameof(TestStrategy), actual.Properties[WellKnownGrainTypeProperties.ClusterPlacementStrategy]);
        Assert.Equal("preserved", actual.Properties["unrelated"]);
        Assert.NotEmpty(payload);
    }

    [Fact]
    public async Task ClusterPlacementResult_RequiresCandidateFromActiveSet()
    {
        var fixture = CreateFixture(
            new FixedDirector(["removed", "active"]),
            Topology(
                3,
                ("removed", MetaclusterClusterState.Removed),
                ("active", MetaclusterClusterState.Active)));

        var result = await fixture.Locator.Locate(
            GrainId,
            Context(),
            TestContext.Current.CancellationToken);
        var entry = await fixture.Directory.Lookup(GrainId, TestContext.Current.CancellationToken);

        Assert.Equal("active", result.ClusterId);
        Assert.NotNull(entry);
        Assert.Equal("active", entry.ClusterId);
        Assert.Throws<ArgumentException>(() => new ClusterPlacementResult(["active", " "]));
    }

    [Fact]
    public void ClusterPlacementStrategyResolver_ReturnsKeyedStrategy()
    {
        var strategy = new TestStrategy();
        using var fixture = CreateResolverFixture((nameof(TestStrategy), strategy));

        var actual = fixture.StrategyResolver.Resolve(GrainType);

        Assert.Same(strategy, actual);
        Assert.Equal(1, strategy.InitializeCount);
        Assert.Equal(nameof(TestStrategy), strategy.InitializedProperties.Properties[WellKnownGrainTypeProperties.ClusterPlacementStrategy]);
    }

    [Fact]
    public void ClusterPlacementStrategyResolver_MissingKey_FailsClearly()
    {
        using var fixture = CreateResolverFixture();

        var exception = Assert.Throws<KeyNotFoundException>(() => fixture.StrategyResolver.Resolve(GrainType));

        Assert.Contains(nameof(TestStrategy), exception.Message, StringComparison.Ordinal);
        Assert.Contains(GrainType.ToString(), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ClusterPlacementDirectorResolver_InitializesDirectorOncePerStrategy()
    {
        var strategy = new TestStrategy();
        var director = new FixedDirector(["active"]);
        using var fixture = CreateResolverFixture(
            (nameof(TestStrategy), strategy),
            (typeof(TestStrategy), director));

        var firstStrategy = fixture.StrategyResolver.Resolve(GrainType);
        var secondStrategy = fixture.StrategyResolver.Resolve(GrainType);
        var firstDirector = fixture.DirectorResolver.Resolve(firstStrategy!);
        var secondDirector = fixture.DirectorResolver.Resolve(secondStrategy!);

        Assert.Same(firstStrategy, secondStrategy);
        Assert.Same(director, firstDirector);
        Assert.Same(firstDirector, secondDirector);
        Assert.Equal(1, strategy.InitializeCount);
    }

    [Fact]
    public void ClusterPlacementDirectorResolver_CachesDifferentStrategiesSeparately()
    {
        var first = new TestStrategy();
        var second = new SecondStrategy();
        var firstDirector = new FixedDirector(["first"]);
        var secondDirector = new FixedDirector(["second"]);
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IClusterPlacementDirector>(typeof(TestStrategy), firstDirector);
        services.AddKeyedSingleton<IClusterPlacementDirector>(typeof(SecondStrategy), secondDirector);
        using var provider = services.BuildServiceProvider();
        var resolver = new ClusterPlacementDirectorResolver(provider);

        var resolvedFirst = resolver.Resolve(first);
        var resolvedSecond = resolver.Resolve(second);

        Assert.Same(firstDirector, resolvedFirst);
        Assert.Same(secondDirector, resolvedSecond);
        Assert.NotSame(resolvedFirst, resolvedSecond);
    }

    [Fact]
    public void ClusterPlacementDirectorResolver_MissingDirector_FailsClearly()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var resolver = new ClusterPlacementDirectorResolver(provider);

        var exception = Assert.Throws<InvalidOperationException>(() => resolver.Resolve(new TestStrategy()));

        Assert.Contains(nameof(IClusterPlacementDirector), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClusterPlacementDirector_ReceivesImmutableActiveCandidateSnapshot()
    {
        var source = new List<string> { "active", "backup" };
        var director = new SnapshotDirector(source);
        var fixture = CreateFixture(
            director,
            Topology(
                4,
                ("active", MetaclusterClusterState.Active),
                ("backup", MetaclusterClusterState.Active),
                ("mutated", MetaclusterClusterState.Removed)));

        var result = await fixture.Locator.Locate(
            GrainId,
            Context(),
            TestContext.Current.CancellationToken);

        Assert.Equal("active", result.ClusterId);
        Assert.Equal("mutated", source[0]);
        Assert.Equal(["active", "backup"], director.ReturnedResult!.CandidateClusters);
    }

    [Fact]
    public async Task ClusterPlacementDirector_ReturningUnknownDrainingOrRemovedCluster_IsRejected()
    {
        var fixture = CreateFixture(
            new FixedDirector(["unknown", "draining", "removed"]),
            Topology(
                5,
                ("draining", MetaclusterClusterState.Draining),
                ("removed", MetaclusterClusterState.Removed)));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Locator.Locate(
                GrainId,
                Context(),
                TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("No active cluster", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'5'", exception.Message, StringComparison.Ordinal);
        Assert.Null(await fixture.Directory.Lookup(GrainId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ClusterPlacementDirector_Cancellation_PropagatesWithoutCachingResult()
    {
        var director = new CancellationThenSuccessDirector();
        var fixture = CreateFixture(
            director,
            Topology(6, ("active", MetaclusterClusterState.Active)));
        using var cancellation = new CancellationTokenSource();

        var pending = fixture.Locator.Locate(GrainId, Context(), cancellation.Token).AsTask();
        await director.FirstCallStarted.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        var successful = await fixture.Locator.Locate(
            GrainId,
            Context(),
            TestContext.Current.CancellationToken);
        Assert.Equal("active", successful.ClusterId);
        Assert.Equal(2, director.CallCount);
        Assert.NotNull(await fixture.Directory.Lookup(GrainId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void CsCheck_PlacementCandidateOrder_IsInvariantWhereDirectorDeclaresOrderIndependence()
    {
        CsCheck.Gen.Int.Array[3].Sample(
            order =>
            {
                var candidates = new[] { "gamma", "alpha", "beta" }
                    .Select((id, index) => (Id: id, Order: order[index]))
                    .OrderBy(item => item.Order)
                    .ThenBy(item => item.Id, StringComparer.Ordinal)
                    .Select(item => item.Id)
                    .ToArray();
                var director = new OrderIndependentDirector(candidates);
                var fixture = CreateFixture(
                    director,
                    Topology(
                        7,
                        ("alpha", MetaclusterClusterState.Active),
                        ("beta", MetaclusterClusterState.Active),
                        ("gamma", MetaclusterClusterState.Active)));

                var result = fixture.Locator.Locate(GrainId, Context()).AsTask().GetAwaiter().GetResult();

                Assert.Equal("alpha", result.ClusterId);
                Assert.Equal(["alpha", "beta", "gamma"], director.LastResult!.CandidateClusters);
                Assert.Equal(7, result.TopologyEpoch);
            },
            seed: "0N0XIzNsQ0P4P1",
            iter: 100,
            threads: 1);
    }

    private static PlacementFixture CreateFixture(
        IClusterPlacementDirector director,
        MetaclusterTopology topology)
    {
        var resolverFixture = CreateResolverFixture(
            (nameof(TestStrategy), new TestStrategy()),
            (typeof(TestStrategy), director));
        var directory = new InMemoryClusterDirectory(
            new Microsoft.Extensions.Time.Testing.FakeTimeProvider(
                new DateTimeOffset(2040, 2, 3, 4, 5, 6, TimeSpan.Zero)));
        var locator = new DirectoryClusterLocator(
            directory,
            new FixedTopologyProvider(topology),
            resolverFixture.StrategyResolver,
            resolverFixture.DirectorResolver,
            Options.Create(new MetaclusterOptions
            {
                Enabled = true,
                ClusterOwnershipLeaseDuration = TimeSpan.FromMinutes(5),
                ClusterOwnershipLeaseRenewalWindow = TimeSpan.FromMinutes(1)
            }));
        return new(directory, locator, resolverFixture);
    }

    private static ResolverFixture CreateResolverFixture(
        (string Name, ClusterPlacementStrategy Strategy)? strategy = null,
        (Type Key, IClusterPlacementDirector Director)? director = null)
    {
        var properties = new GrainProperties(
            ImmutableDictionary<string, string>.Empty
                .WithComparers(StringComparer.Ordinal, StringComparer.Ordinal)
                .Add(WellKnownGrainTypeProperties.ClusterPlacementStrategy, nameof(TestStrategy)));
        var manifest = new GrainManifest(
            ImmutableDictionary<GrainType, GrainProperties>.Empty.Add(GrainType, properties),
            ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty);
        var clusterManifest = new ClusterManifest(
            MajorMinorVersion.Zero,
            ImmutableDictionary<SiloAddress, GrainManifest>.Empty,
            [manifest]);
        var manifestProvider = Substitute.For<IClusterManifestProvider>();
        manifestProvider.Current.Returns(clusterManifest);
        manifestProvider.LocalGrainManifest.Returns(manifest);
        var services = new ServiceCollection();
        if (strategy is { } strategyRegistration)
        {
            services.AddKeyedSingleton<ClusterPlacementStrategy>(strategyRegistration.Name, strategyRegistration.Strategy);
        }

        if (director is { } directorRegistration)
        {
            services.AddKeyedSingleton<IClusterPlacementDirector>(
                directorRegistration.Key,
                directorRegistration.Director);
        }

        var provider = services.BuildServiceProvider();
        var propertiesResolver = new GrainPropertiesResolver(manifestProvider);
        return new(
            new ClusterPlacementStrategyResolver(propertiesResolver, provider),
            new ClusterPlacementDirectorResolver(provider),
            provider);
    }

    private static ClusterLocationContext Context()
        => new(
            "service",
            "local",
            new GrainProperties(ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal, StringComparer.Ordinal)));

    private static MetaclusterTopology Topology(
        long epoch,
        params (string Id, MetaclusterClusterState State)[] clusters)
        => new(
            "service",
            epoch,
            clusters.ToImmutableDictionary(
                cluster => cluster.Id,
                cluster => new MetaclusterCluster(cluster.Id, cluster.State, []),
                StringComparer.Ordinal));

    private sealed record PlacementFixture(
        InMemoryClusterDirectory Directory,
        DirectoryClusterLocator Locator,
        ResolverFixture Resolvers);

    private sealed record ResolverFixture(
        ClusterPlacementStrategyResolver StrategyResolver,
        ClusterPlacementDirectorResolver DirectorResolver,
        ServiceProvider Services) : IDisposable
    {
        public void Dispose() => Services.Dispose();
    }

    private sealed class TestStrategy : ClusterPlacementStrategy
    {
        public int InitializeCount { get; private set; }

        public GrainProperties InitializedProperties { get; private set; } = null!;

        public override void Initialize(GrainProperties properties)
        {
            InitializeCount++;
            InitializedProperties = properties;
        }
    }

    private sealed class SecondStrategy : ClusterPlacementStrategy;

    private sealed class FixedDirector(IReadOnlyList<string> candidates) : IClusterPlacementDirector
    {
        public ValueTask<ClusterPlacementResult> SelectClusters(
            ClusterPlacementStrategy strategy,
            GrainId grainId,
            ClusterLocationContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(new ClusterPlacementResult(candidates));
        }
    }

    private sealed class SnapshotDirector(List<string> source) : IClusterPlacementDirector
    {
        public ClusterPlacementResult? ReturnedResult { get; private set; }

        public ValueTask<ClusterPlacementResult> SelectClusters(
            ClusterPlacementStrategy strategy,
            GrainId grainId,
            ClusterLocationContext context,
            CancellationToken cancellationToken = default)
        {
            ReturnedResult = new ClusterPlacementResult(source);
            source[0] = "mutated";
            return new(ReturnedResult);
        }
    }

    private sealed class CancellationThenSuccessDirector : IClusterPlacementDirector
    {
        public TaskCompletionSource FirstCallStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount { get; private set; }

        public async ValueTask<ClusterPlacementResult> SelectClusters(
            ClusterPlacementStrategy strategy,
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

            return new ClusterPlacementResult(["active"]);
        }
    }

    private sealed class OrderIndependentDirector(IEnumerable<string> candidates) : IClusterPlacementDirector
    {
        public ClusterPlacementResult? LastResult { get; private set; }

        public ValueTask<ClusterPlacementResult> SelectClusters(
            ClusterPlacementStrategy strategy,
            GrainId grainId,
            ClusterLocationContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastResult = new ClusterPlacementResult(candidates.Order(StringComparer.Ordinal));
            return new(LastResult);
        }
    }

    private sealed class FixedTopologyProvider(MetaclusterTopology topology) : IMetaclusterTopologyProvider
    {
        public ValueTask<MetaclusterTopology> GetTopology(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(topology);
        }

        public async IAsyncEnumerable<MetaclusterTopology> Watch(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return topology;
            await Task.CompletedTask;
        }
    }
}
