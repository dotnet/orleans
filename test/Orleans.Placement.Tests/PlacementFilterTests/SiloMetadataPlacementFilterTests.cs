using Microsoft.Extensions.DependencyInjection;
using Orleans.Placement;
using Orleans.Runtime.Diagnostics;
using Orleans.Runtime.MembershipService.SiloMetadata;
using Orleans.Runtime.Placement;
using Orleans.Runtime.Placement.Filtering;
using Orleans.TestingHost;
using Orleans.TestingHost.Diagnostics;
using UnitTests.SiloMetadataTests;
using Xunit;

namespace UnitTests.PlacementFilterTests;

[TestSuite("Functional")]
[TestProvider("None")]
[TestArea("Placement")]
[TestCategory("Placement"), TestCategory("Filters"), TestCategory("SiloMetadata")]
public class SiloMetadataPlacementFilterTests(SiloMetadataPlacementFilterTests.Fixture fixture) : IClassFixture<SiloMetadataPlacementFilterTests.Fixture>
{
    public class Fixture : IAsyncLifetime
    {
        private readonly DiagnosticEventCollector _metadataEvents = new(SiloMetadataEvents.ListenerName);

        public InProcessTestCluster Cluster { get; private set; } = null!;
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (Cluster is { } cluster)
                {
                    await cluster.DisposeAsync();
                }
            }
            finally
            {
                _metadataEvents.Dispose();
            }
        }

        public async ValueTask InitializeAsync()
        {
            var builder = new InProcessTestClusterBuilder(3);
            builder.ConfigureSilo((options, siloBuilder) => siloBuilder.UseSiloMetadata(new Dictionary<string, string>
            {
                {"first", "1"},
                {"second", "2"},
                {"third", "3"},
                {"unique", Guid.NewGuid().ToString()}
            }));

            Cluster = builder.Build();
            await Cluster.DeployAsync();
            await Cluster.WaitForLivenessToStabilizeAsync();
            await Cluster.WaitForClusterManifestToStabilizeAsync();
            await Cluster.WaitForSiloMetadataConvergenceAsync(
                _metadataEvents,
                ["first", "second", "third", "unique"],
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken);
        }
    }

    [Fact, TestCategory("Functional")]
    public void PlacementFilter_CandidatesMatchCallingSilo()
    {
        var uniqueValues = new HashSet<string>();
        foreach (var silo in fixture.Cluster.Silos)
        {
            var services = fixture.Cluster.GetSiloServiceProvider(silo.SiloAddress);
            var cache = services.GetRequiredService<ISiloMetadataCache>();
            Assert.True(uniqueValues.Add(cache.GetSiloMetadata(silo.SiloAddress).Metadata["unique"]));

            var client = services.GetRequiredService<IClusterClient>();
            var placement = services.GetRequiredService<PlacementService>();
            var requiredGrain = client.GetGrain<IUniqueRequiredMatchFilteredGrain>(0);
            var preferredGrain = client.GetGrain<IPreferredMatchFilteredGrain>(0);
            foreach (var grainId in new[] { requiredGrain.GetGrainId(), preferredGrain.GetGrainId() })
            {
                var target = new PlacementTarget(grainId, new Dictionary<string, object>(), default, 0);
                Assert.Equal(silo.SiloAddress, Assert.Single(placement.GetCompatibleSilos(target)));
            }
        }
    }

    [Fact, TestCategory("Functional")]
    public async Task PlacementFilter_GrainWithoutFilterCanBeCalled()
    {
        var managementGrain = fixture.Cluster.Client!.GetGrain<IManagementGrain>(0);
        var silos = await managementGrain.GetHosts(true, TestContext.Current.CancellationToken);
        Assert.NotNull(silos);
    }

    /// <summary>
    /// Unique silo metadata is set up to be different for each silo, so this will require that placement happens on the calling silo.
    /// </summary>
    /// <returns></returns>
    [Fact, TestCategory("Functional")]
    public async Task PlacementFilter_RequiredFilterCanBeCalled()
    {
        var id = 0;
        foreach (var hostedClusterSilo in fixture.Cluster.Silos)
        {
            for (var i = 0; i < 50; i++)
            {
                ++id;
                var firstSp = fixture.Cluster.GetSiloServiceProvider(hostedClusterSilo.SiloAddress);
                var firstSiloMetadataCache = firstSp.GetRequiredService<IClusterClient>();
                var managementGrain = firstSiloMetadataCache.GetGrain<IUniqueRequiredMatchFilteredGrain>(id);
                var hostingSilo = await managementGrain.GetHostingSilo();
                Assert.NotNull(hostingSilo);
                Assert.Equal(hostedClusterSilo.SiloAddress, hostingSilo);
            }
        }
    }

    /// <summary>
    /// Unique silo metadata is set up to be different for each silo, so this will require that placement happens on the calling silo because it is the only one that matches.
    /// </summary>
    /// <returns></returns>
    [Fact, TestCategory("Functional")]
    public async Task PlacementFilter_PreferredFilterCanBeCalled()
    {
        var id = 0;
        foreach (var hostedClusterSilo in fixture.Cluster.Silos)
        {
            for (var i = 0; i < 50; i++)
            {
                ++id;
                var firstSp = fixture.Cluster.GetSiloServiceProvider(hostedClusterSilo.SiloAddress);
                var firstSiloMetadataCache = firstSp.GetRequiredService<IClusterClient>();
                var managementGrain = firstSiloMetadataCache.GetGrain<IPreferredMatchFilteredGrain>(id);
                var hostingSilo = await managementGrain.GetHostingSilo();
                Assert.NotNull(hostingSilo);
                Assert.Equal(hostedClusterSilo.SiloAddress, hostingSilo);
            }
        }
    }

    /// <summary>
    /// Unique silo metadata is set up to be different for each silo, so this will still place on any of the two silos since just the matching silos (just the one) is not enough to make the minimum desired candidates.
    /// </summary>
    /// <returns></returns>
    [Fact, TestCategory("Functional")]
    public async Task PlacementFilter_PreferredMin2FilterCanBeCalled()
    {
        var id = 0;
        foreach (var hostedClusterSilo in fixture.Cluster.Silos)
        {
            var dict = new Dictionary<SiloAddress, int>();
            foreach (var clusterSilo in fixture.Cluster.Silos)
            {
                dict[clusterSilo.SiloAddress] = 0;
            }

            for (var i = 0; i < 50; i++)
            {
                ++id;
                var firstSp = fixture.Cluster.GetSiloServiceProvider(hostedClusterSilo.SiloAddress);
                var firstSiloMetadataCache = firstSp.GetRequiredService<IClusterClient>();
                var managementGrain = firstSiloMetadataCache.GetGrain<IPreferredMatchMin2FilteredGrain>(id);
                var hostingSilo = await managementGrain.GetHostingSilo();
                Assert.NotNull(hostingSilo);
                dict[hostingSilo] = dict.TryGetValue(hostingSilo, out var count) ? count + 1 : 1;
            }

            foreach (var kv in dict)
            {
                Assert.True(kv.Value >= 1, $"Silo {kv.Key} did not host at least 1 grain");
            }
        }
    }

    /// <summary>
    /// Unique silo metadata is set up to be different for each silo, so this will still place on any of the two silos since just the matching silos (just the one) is not enough to make the minimum desired candidates.
    /// </summary>
    /// <returns></returns>
    [Fact, TestCategory("Functional")]
    public async Task PlacementFilter_PreferredMultipleFilterCanBeCalled()
    {
        var id = 0;
        foreach (var hostedClusterSilo in fixture.Cluster.Silos)
        {
            var dict = new Dictionary<SiloAddress, int>();
            foreach (var clusterSilo in fixture.Cluster.Silos)
            {
                dict[clusterSilo.SiloAddress] = 0;
            }

            for (var i = 0; i < 50; i++)
            {
                ++id;
                var firstSp = fixture.Cluster.GetSiloServiceProvider(hostedClusterSilo.SiloAddress);
                var firstSiloMetadataCache = firstSp.GetRequiredService<IClusterClient>();
                var managementGrain = firstSiloMetadataCache.GetGrain<IPreferredMatchMultipleFilteredGrain>(id);
                var hostingSilo = await managementGrain.GetHostingSilo();
                Assert.NotNull(hostingSilo);
                dict[hostingSilo] = dict.TryGetValue(hostingSilo, out var count) ? count + 1 : 1;
            }

            foreach (var kv in dict)
            {
                Assert.True(kv.Value >= 1, $"Silo {kv.Key} did not host at least 1 grain");
            }
        }
    }

    /// <summary>
    /// Unique silo metadata is set up to be different for each silo, so this will still place on any of the two silos since just the matching silos (just the one) is not enough to make the minimum desired candidates.
    /// </summary>
    /// <returns></returns>
    [Fact, TestCategory("Functional")]
    public async Task PlacementFilter_PreferredMin2FilterCanBeCalledWithLargerCluster()
    {
        var id = 0;
        foreach (var hostedClusterSilo in fixture.Cluster.Silos)
        {
            var dict = new Dictionary<SiloAddress, int>();
            foreach (var clusterSilo in fixture.Cluster.Silos)
            {
                dict[clusterSilo.SiloAddress] = 0;
            }
            for (var i = 0; i < 50; i++)
            {
                ++id;
                var firstSp = fixture.Cluster.GetSiloServiceProvider(hostedClusterSilo.SiloAddress);
                var firstSiloMetadataCache = firstSp.GetRequiredService<IClusterClient>();
                var managementGrain = firstSiloMetadataCache.GetGrain<IPreferredMatchMin2FilteredGrain>(id);
                var hostingSilo = await managementGrain.GetHostingSilo();
                Assert.NotNull(hostingSilo);
                dict[hostingSilo] = dict.TryGetValue(hostingSilo, out var count) ? count + 1 : 1;
            }

            foreach (var kv in dict)
            {
                Assert.True(kv.Value >= 1, $"Silo {kv.Key} did not host at least 1 grain");
            }
        }
    }

    /// <summary>
    /// If no metadata key is defined then it should fall back to matching all silos
    /// </summary>
    /// <returns></returns>
    [Fact, TestCategory("Functional")]
    public async Task PlacementFilter_PreferredNoMetadataFilterCanBeCalled()
    {
        var id = 0;
        foreach (var hostedClusterSilo in fixture.Cluster.Silos)
        {
            var dict = new Dictionary<SiloAddress, int>();
            foreach (var clusterSilo in fixture.Cluster.Silos)
            {
                dict[clusterSilo.SiloAddress] = 0;
            }
            for (var i = 0; i < 50; i++)
            {
                ++id;
                var firstSp = fixture.Cluster.GetSiloServiceProvider(hostedClusterSilo.SiloAddress);
                var firstSiloMetadataCache = firstSp.GetRequiredService<IClusterClient>();
                var managementGrain = firstSiloMetadataCache.GetGrain<IPreferredMatchNoMetadataFilteredGrain>(id);
                var hostingSilo = await managementGrain.GetHostingSilo();
                Assert.NotNull(hostingSilo);
                dict[hostingSilo] = dict.TryGetValue(hostingSilo, out var count) ? count + 1 : 1;
            }

            foreach (var kv in dict)
            {
                Assert.True(kv.Value >= 1, $"Silo {kv.Key} did not host at least 1 grain");
            }
        }
    }
}

public interface IUniqueRequiredMatchFilteredGrain : IGrainWithIntegerKey
{
    Task<SiloAddress> GetHostingSilo();
}

#pragma warning disable ORLEANSEXP004
[RequiredMatchSiloMetadataPlacementFilter(["unique"]), RandomPlacement]
#pragma warning restore ORLEANSEXP004
public class UniqueRequiredMatchFilteredGrain(ILocalSiloDetails localSiloDetails) : Grain, IUniqueRequiredMatchFilteredGrain
{
    public Task<SiloAddress> GetHostingSilo() => Task.FromResult(localSiloDetails.SiloAddress);
}
public interface IPreferredMatchFilteredGrain : IGrainWithIntegerKey
{
    Task<SiloAddress> GetHostingSilo();
}

#pragma warning disable ORLEANSEXP004
[PreferredMatchSiloMetadataPlacementFilter(["unique"], 1), RandomPlacement]
#pragma warning restore ORLEANSEXP004
public class PreferredMatchFilteredGrain(ILocalSiloDetails localSiloDetails) : Grain, IPreferredMatchFilteredGrain
{
    public Task<SiloAddress> GetHostingSilo() => Task.FromResult(localSiloDetails.SiloAddress);
}


public interface IPreferredMatchMin2FilteredGrain : IGrainWithIntegerKey
{
    Task<SiloAddress> GetHostingSilo();
}

#pragma warning disable ORLEANSEXP004
[PreferredMatchSiloMetadataPlacementFilter(["unique"]), RandomPlacement]
#pragma warning restore ORLEANSEXP004
public class PreferredMatchMinTwoFilteredGrain(ILocalSiloDetails localSiloDetails) : Grain, IPreferredMatchMin2FilteredGrain
{
    public Task<SiloAddress> GetHostingSilo() => Task.FromResult(localSiloDetails.SiloAddress);
}

public interface IPreferredMatchMultipleFilteredGrain : IGrainWithIntegerKey
{
    Task<SiloAddress> GetHostingSilo();
}

#pragma warning disable ORLEANSEXP004
[PreferredMatchSiloMetadataPlacementFilter(["unique", "other"], 2), RandomPlacement]
#pragma warning restore ORLEANSEXP004
public class PreferredMatchMultipleFilteredGrain(ILocalSiloDetails localSiloDetails) : Grain, IPreferredMatchMultipleFilteredGrain
{
    public Task<SiloAddress> GetHostingSilo() => Task.FromResult(localSiloDetails.SiloAddress);
}

#pragma warning disable ORLEANSEXP004
[PreferredMatchSiloMetadataPlacementFilter(["not.there"]), RandomPlacement]
#pragma warning restore ORLEANSEXP004
public class PreferredMatchNoMetadataFilteredGrain(ILocalSiloDetails localSiloDetails) : Grain, IPreferredMatchNoMetadataFilteredGrain
{
    public Task<SiloAddress> GetHostingSilo() => Task.FromResult(localSiloDetails.SiloAddress);
}

public interface IPreferredMatchNoMetadataFilteredGrain : IGrainWithIntegerKey
{
    Task<SiloAddress> GetHostingSilo();
}
