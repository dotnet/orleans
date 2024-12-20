using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime.MembershipService.SiloMetadata;
using Orleans.Runtime.Placement.Filtering;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace UnitTests.PlacementFilterTests;

[TestCategory("Placement"), TestCategory("Filters"), TestCategory("SiloMetadata")]
public class SiloMetadataPlacementFilterTests : TestClusterPerTest
{
    protected override void ConfigureTestCluster(TestClusterBuilder builder)
    {
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();
    }

    private class SiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder hostBuilder)
        {
            hostBuilder.UseSiloMetadata(new Dictionary<string, string>
            {
                {"first", "1"},
                {"second", "2"},
                {"third", "3"},
                {"unique", Guid.NewGuid().ToString()}
            });
        }
    }

    [Fact, TestCategory("Functional")]
    public async Task PlacementFilter_GrainWithoutFilterCanBeCalled()
    {
        await HostedCluster.WaitForLivenessToStabilizeAsync();
        var managementGrain = Client.GetGrain<IManagementGrain>(0);
        var silos = await managementGrain.GetHosts(true);
        Assert.NotNull(silos);
    }

    /// <summary>
    /// Unique silo metadata is set up to be different for each silo, so this will require that placement happens on the calling silo.
    /// </summary>
    /// <returns></returns>
    [Fact, TestCategory("Functional")]
    public async Task PlacementFilter_RequiredFilterCanBeCalled()
    {
        await HostedCluster.WaitForLivenessToStabilizeAsync();
        var id = 0;
        foreach (var hostedClusterSilo in HostedCluster.Silos)
        {
            for (var i = 0; i < 50; i++)
            {
                ++id;
                var firstSp = HostedCluster.GetSiloServiceProvider(hostedClusterSilo.SiloAddress);
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
        await HostedCluster.WaitForLivenessToStabilizeAsync();
        var id = 0;
        foreach (var hostedClusterSilo in HostedCluster.Silos)
        {
            for (var i = 0; i < 50; i++)
            {
                ++id;
                var firstSp = HostedCluster.GetSiloServiceProvider(hostedClusterSilo.SiloAddress);
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
        await HostedCluster.WaitForLivenessToStabilizeAsync();
        var id = 0;
        foreach (var hostedClusterSilo in HostedCluster.Silos)
        {
            var dict = new Dictionary<SiloAddress, int>();
            foreach (var clusterSilo in HostedCluster.Silos)
            {
                dict[clusterSilo.SiloAddress] = 0;
            }
            for (var i = 0; i < 50; i++)
            {
                ++id;
                var firstSp = HostedCluster.GetSiloServiceProvider(hostedClusterSilo.SiloAddress);
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
    public async Task PlacementFilter_PreferredMin2FilterCanBeCalledWithLargerCluster()
    {
        await HostedCluster.WaitForLivenessToStabilizeAsync();
        await HostedCluster.StartAdditionalSiloAsync();
        await HostedCluster.WaitForLivenessToStabilizeAsync();
        var id = 0;
        foreach (var hostedClusterSilo in HostedCluster.Silos)
        {
            var dict = new Dictionary<SiloAddress, int>();
            foreach (var clusterSilo in HostedCluster.Silos)
            {
                dict[clusterSilo.SiloAddress] = 0;
            }
            for (var i = 0; i < 50; i++)
            {
                ++id;
                var firstSp = HostedCluster.GetSiloServiceProvider(hostedClusterSilo.SiloAddress);
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
        await HostedCluster.WaitForLivenessToStabilizeAsync();
        await HostedCluster.StartAdditionalSiloAsync();
        await HostedCluster.WaitForLivenessToStabilizeAsync();
        var id = 0;
        foreach (var hostedClusterSilo in HostedCluster.Silos)
        {
            var dict = new Dictionary<SiloAddress, int>();
            foreach (var clusterSilo in HostedCluster.Silos)
            {
                dict[clusterSilo.SiloAddress] = 0;
            }
            for (var i = 0; i < 50; i++)
            {
                ++id;
                var firstSp = HostedCluster.GetSiloServiceProvider(hostedClusterSilo.SiloAddress);
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
[RequiredMatchSiloMetadataPlacementFilter(["unique"])]
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
[PreferredMatchSiloMetadataPlacementFilter(["unique"], 1)]
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
[PreferredMatchSiloMetadataPlacementFilter(["unique"])]
#pragma warning restore ORLEANSEXP004
public class PreferredMatchMinTwoFilteredGrain(ILocalSiloDetails localSiloDetails) : Grain, IPreferredMatchMin2FilteredGrain
{
    public Task<SiloAddress> GetHostingSilo() => Task.FromResult(localSiloDetails.SiloAddress);
}

#pragma warning disable ORLEANSEXP004
[PreferredMatchSiloMetadataPlacementFilter(["not.there"])]
#pragma warning restore ORLEANSEXP004
public class PreferredMatchNoMetadataFilteredGrain(ILocalSiloDetails localSiloDetails) : Grain, IPreferredMatchNoMetadataFilteredGrain
{
    public Task<SiloAddress> GetHostingSilo() => Task.FromResult(localSiloDetails.SiloAddress);
}

public interface IPreferredMatchNoMetadataFilteredGrain : IGrainWithIntegerKey
{
    Task<SiloAddress> GetHostingSilo();
}