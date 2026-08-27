using Microsoft.Extensions.DependencyInjection;
using Orleans.GrainDirectory;
using TestExtensions;
using Xunit;

namespace Orleans.TestingHost.Tests;

public sealed class InProcessTestClusterDirectoryTests
{
    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public async Task DefaultUsesTestDirectory()
    {
        var builder = new InProcessTestClusterBuilder(1);

        await using var cluster = builder.Build();
        await cluster.DeployAsync(TestContext.Current.CancellationToken);

        Assert.Equal("InProcessGrainDirectory", GetDefaultDirectory(cluster).GetType().Name);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public async Task DistributedDirectoryCanBeEnabled()
    {
        var builder = new InProcessTestClusterBuilder(1);
#pragma warning disable ORLEANSEXP003
        builder.Options.UseDistributedGrainDirectory = true;
#pragma warning restore ORLEANSEXP003

        await using var cluster = builder.Build();
        await cluster.DeployAsync(TestContext.Current.CancellationToken);

        Assert.Equal("DistributedGrainDirectory", GetDefaultDirectory(cluster).GetType().Name);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public async Task DistributedDirectoryCanBeEnabledWhenNamedDirectoryExists()
    {
        var builder = new InProcessTestClusterBuilder(1);
#pragma warning disable ORLEANSEXP003
        builder.Options.UseDistributedGrainDirectory = true;
        builder.ConfigureSilo(static (_, siloBuilder) => siloBuilder.AddDistributedGrainDirectory("named"));
#pragma warning restore ORLEANSEXP003

        await using var cluster = builder.Build();
        await cluster.DeployAsync(TestContext.Current.CancellationToken);

        var defaultDirectory = GetDefaultDirectory(cluster);
        var namedDirectory = cluster.Silos[0].ServiceProvider.GetRequiredKeyedService<IGrainDirectory>("named");
        Assert.Same(namedDirectory, defaultDirectory);
        Assert.Single(
            cluster.Silos[0].ServiceProvider.GetServices<ILifecycleParticipant<ISiloLifecycle>>(),
            static participant => participant.GetType().Name == "DistributedGrainDirectory");
    }

    private static IGrainDirectory GetDefaultDirectory(InProcessTestCluster cluster) =>
        cluster.Silos[0].ServiceProvider.GetRequiredKeyedService<IGrainDirectory>(
            GrainDirectoryAttribute.DEFAULT_GRAIN_DIRECTORY);
}

public sealed class OrleansInProcessTestClusterDirectoryTests(
    OrleansInProcessTestClusterFixture fixture) : IClassFixture<OrleansInProcessTestClusterFixture>
{
    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void OrleansFixtureUsesDistributedDirectory()
    {
        var registeredDirectory = fixture.HostedCluster.Silos[0].ServiceProvider.GetRequiredKeyedService<IGrainDirectory>(
            GrainDirectoryAttribute.DEFAULT_GRAIN_DIRECTORY);
        Assert.Equal("DistributedGrainDirectory", registeredDirectory.GetType().Name);
    }
}

public sealed class OrleansInProcessTestClusterFixture : BaseInProcessTestClusterFixture;
