using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.TestingHost;
using Xunit;

namespace UnitTests.Manifest;

public sealed class ClusterManifestStabilizationTests
{
    [Fact, TestCategory("Functional")]
    public async Task WaitForClusterManifestToStabilizeAsync_WaitsForAllActiveSiloManifests()
    {
        var builder = new InProcessTestClusterBuilder(2);
        await using var cluster = builder.Build();
        await cluster.DeployAsync();
        await cluster.WaitForClusterManifestToStabilizeAsync();

        await cluster.StartAdditionalSiloAsync();
        await cluster.WaitForClusterManifestToStabilizeAsync();

        var expectedSilos = cluster.GetActiveSilos().Select(static silo => silo.SiloAddress).ToHashSet();
        foreach (var silo in cluster.GetActiveSilos())
        {
            var manifestProvider = cluster.GetSiloServiceProvider(silo.SiloAddress).GetRequiredService<IClusterManifestProvider>();
            Assert.True(expectedSilos.SetEquals(manifestProvider.Current.Silos.Keys));
        }
    }
}
