using Microsoft.Extensions.DependencyInjection;
using Orleans.GrainDirectory;
using TestExtensions;
using Xunit;

namespace Orleans.TestingHost.Tests;

public sealed class InProcessTestClusterDirectoryTests
{
    [Fact, TestCategory("BVT")]
    public async Task DefaultUsesTestDirectory()
    {
        var builder = new InProcessTestClusterBuilder(1);

        await using var cluster = builder.Build();
        await cluster.DeployAsync();

        Assert.Equal("InProcessGrainDirectory", GetDefaultDirectory(cluster).GetType().Name);
    }

    [Fact, TestCategory("BVT")]
    public async Task DistributedDirectoryCanBeEnabled()
    {
        var builder = new InProcessTestClusterBuilder(1);
        builder.Options.UseDistributedGrainDirectory = true;

        await using var cluster = builder.Build();
        await cluster.DeployAsync();

        Assert.Equal("DistributedGrainDirectory", GetDefaultDirectory(cluster).GetType().Name);
    }

    private static IGrainDirectory GetDefaultDirectory(InProcessTestCluster cluster) =>
        cluster.Silos[0].ServiceProvider.GetRequiredKeyedService<IGrainDirectory>(
            GrainDirectoryAttribute.DEFAULT_GRAIN_DIRECTORY);
}

public sealed class OrleansInProcessTestClusterDirectoryTests(
    OrleansInProcessTestClusterFixture fixture) : IClassFixture<OrleansInProcessTestClusterFixture>
{
    [Fact, TestCategory("BVT")]
    public void OrleansFixtureUsesDistributedDirectory()
    {
        var registeredDirectory = fixture.HostedCluster.Silos[0].ServiceProvider.GetRequiredKeyedService<IGrainDirectory>(
            GrainDirectoryAttribute.DEFAULT_GRAIN_DIRECTORY);
        Assert.Equal("DistributedGrainDirectory", registeredDirectory.GetType().Name);
    }
}

public sealed class OrleansInProcessTestClusterFixture : BaseInProcessTestClusterFixture;
