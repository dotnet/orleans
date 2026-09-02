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
        builder.Options.UseDistributedGrainDirectory = true;

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
        builder.Options.UseDistributedGrainDirectory = true;
        builder.ConfigureSilo(static (_, siloBuilder) => siloBuilder.AddDistributedGrainDirectory("named"));

        await using var cluster = builder.Build();
        await cluster.DeployAsync(TestContext.Current.CancellationToken);

        var defaultDirectory = GetDefaultDirectory(cluster);
        var namedDirectory = cluster.Silos[0].ServiceProvider.GetRequiredKeyedService<IGrainDirectory>("named");
        Assert.Same(namedDirectory, defaultDirectory);
        Assert.Single(
            cluster.Silos[0].ServiceProvider.GetServices<ILifecycleParticipant<ISiloLifecycle>>(),
            static participant => participant.GetType().Name == "DistributedGrainDirectory");
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public async Task DistributedDirectoryCanBeRegisteredWithMultipleNames()
    {
        var builder = new InProcessTestClusterBuilder(1);
        builder.ConfigureSilo(static (_, siloBuilder) =>
        {
            siloBuilder.AddDistributedGrainDirectory("first");
            siloBuilder.AddDistributedGrainDirectory("second");
        });

        await using var cluster = builder.Build();
        await cluster.DeployAsync(TestContext.Current.CancellationToken);

        var services = cluster.Silos[0].ServiceProvider;
        var first = services.GetRequiredKeyedService<IGrainDirectory>("first");
        var second = services.GetRequiredKeyedService<IGrainDirectory>("second");
        Assert.Same(first, second);
        Assert.Equal("InProcessGrainDirectory", GetDefaultDirectory(cluster).GetType().Name);
        Assert.Single(
            services.GetServices<ILifecycleParticipant<ISiloLifecycle>>(),
            static participant => participant.GetType().Name == "DistributedGrainDirectory");
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public async Task DistributedDirectoryCanBeRegisteredAsDefaultBeforeNamed()
    {
        var builder = new InProcessTestClusterBuilder(1);
        builder.Options.UseDistributedGrainDirectory = true;
        builder.ConfigureSilo(static (_, siloBuilder) =>
        {
            siloBuilder.AddDistributedGrainDirectory();
            siloBuilder.AddDistributedGrainDirectory("named");
        });

        await using var cluster = builder.Build();
        await cluster.DeployAsync(TestContext.Current.CancellationToken);

        var services = cluster.Silos[0].ServiceProvider;
        var defaultDirectory = GetDefaultDirectory(cluster);
        var namedDirectory = services.GetRequiredKeyedService<IGrainDirectory>("named");
        Assert.Same(defaultDirectory, namedDirectory);
        Assert.Single(
            services.GetServices<ILifecycleParticipant<ISiloLifecycle>>(),
            static participant => participant.GetType().Name == "DistributedGrainDirectory");
    }

    private static IGrainDirectory GetDefaultDirectory(InProcessTestCluster cluster) =>
        cluster.Silos[0].ServiceProvider.GetRequiredKeyedService<IGrainDirectory>(
            GrainDirectoryAttribute.DEFAULT_GRAIN_DIRECTORY);

    [TestSuite("Functional")]
    [TestProvider("None")]
    [Fact, TestCategory("Functional")]
    public async Task GrainDirectoryObserver_CanObserve_TestAndDistributedDirectories_ReturnsExpectedSupport()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        {
            var builder = new InProcessTestClusterBuilder(1);
            builder.Options.ConfigureFileLogging = false;
            builder.Options.InitializeClientOnDeploy = false;

            await using var cluster = builder.Build();
            await cluster.DeployAsync(cancellationToken);
            var handle = Assert.Single(cluster.Silos);

            Assert.Equal("InProcessGrainDirectory", GetDefaultDirectory(cluster).GetType().Name);
            Assert.False(GrainDirectoryObserver.CanObserve(cluster.Silos));

            await cluster.DisposeAsync();
            Assert.False(handle.IsActive);
            Assert.Same(handle, Assert.Single(cluster.Silos));
        }

        {
            var builder = new InProcessTestClusterBuilder(1);
            builder.Options.ConfigureFileLogging = false;
            builder.Options.InitializeClientOnDeploy = false;
#pragma warning disable ORLEANSEXP003
            builder.Options.UseDistributedGrainDirectory = true;
#pragma warning restore ORLEANSEXP003

            await using var cluster = builder.Build();
            await cluster.DeployAsync(cancellationToken);
            var handle = Assert.Single(cluster.Silos);

            Assert.Equal("DistributedGrainDirectory", GetDefaultDirectory(cluster).GetType().Name);
            Assert.True(GrainDirectoryObserver.CanObserve(cluster.Silos));

            await cluster.DisposeAsync();
            Assert.False(handle.IsActive);
            Assert.Same(handle, Assert.Single(cluster.Silos));
        }
    }

    [TestSuite("Functional")]
    [TestProvider("None")]
    [Fact, TestCategory("Functional")]
    public async Task WaitForTopologyToConvergeAsync_WithNonObservableCustomDirectory_ThrowsContextualInvalidOperationException()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var builder = new InProcessTestClusterBuilder(1);
        builder.Options.ConfigureFileLogging = false;
        builder.Options.UseTestClusterGrainDirectory = false;
        builder.ConfigureSilo(static (_, siloBuilder) =>
            Orleans.Runtime.Hosting.DirectorySiloBuilderExtensions.AddGrainDirectory(
                siloBuilder,
                GrainDirectoryAttribute.DEFAULT_GRAIN_DIRECTORY,
                static (_, _) => new NonObservableGrainDirectory()));

        await using var cluster = builder.Build();
        await cluster.DeployAsync(cancellationToken);
        var handle = Assert.Single(cluster.Silos);
        var expectedSilos = handle.SiloAddress.ToString();

        Assert.IsType<NonObservableGrainDirectory>(GetDefaultDirectory(cluster));
        Assert.False(GrainDirectoryObserver.CanObserve(cluster.Silos));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => cluster.WaitForTopologyToConvergeAsync(cancellationToken));

        Assert.Equal(
            $"The grain directory cannot report convergence for the expected topology: {expectedSilos}.",
            exception.Message);
        Assert.True(handle.IsActive);
        Assert.Same(handle, Assert.Single(cluster.Silos));
        Assert.Same(handle, Assert.Single(cluster.GetActiveSilos()));

        await cluster.DisposeAsync();
        Assert.False(handle.IsActive);
        Assert.Same(handle, Assert.Single(cluster.Silos));
        Assert.Empty(cluster.GetActiveSilos());
    }

    [TestSuite("Functional")]
    [TestProvider("None")]
    [Fact, TestCategory("Functional")]
    public async Task GrainDirectoryObserver_WaitForConvergenceAsync_WhenObserverErrors_ThrowsContextualException()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var expectedError = new InvalidOperationException("Controlled grain-directory observer failure.");
        var builder = new InProcessTestClusterBuilder(1);
        builder.Options.ConfigureFileLogging = false;
        builder.Options.InitializeClientOnDeploy = false;
#pragma warning disable ORLEANSEXP003
        builder.Options.UseDistributedGrainDirectory = true;
#pragma warning restore ORLEANSEXP003

        await using var cluster = builder.Build();
        await cluster.DeployAsync(cancellationToken);
        var handle = Assert.Single(cluster.Silos);
        using var observer = new GrainDirectoryObserver();

        observer.OnError(expectedError);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => observer.WaitForConvergenceAsync(cluster.Silos, TimeSpan.Zero));

        Assert.Equal("An error occurred while observing grain directory events.", exception.Message);
        Assert.Same(expectedError, exception.InnerException);
        Assert.True(handle.IsActive);
        Assert.Same(handle, Assert.Single(cluster.GetActiveSilos()));

        await cluster.DisposeAsync();
        Assert.False(handle.IsActive);
        Assert.Empty(cluster.GetActiveSilos());
    }

    private sealed class NonObservableGrainDirectory : IGrainDirectory
    {
        public Task<Orleans.Runtime.GrainAddress?> Register(Orleans.Runtime.GrainAddress address) =>
            Task.FromResult<Orleans.Runtime.GrainAddress?>(address);

        public Task Unregister(Orleans.Runtime.GrainAddress address) => Task.CompletedTask;

        public Task<Orleans.Runtime.GrainAddress?> Lookup(Orleans.Runtime.GrainId grainId) =>
            Task.FromResult<Orleans.Runtime.GrainAddress?>(null);

        public Task UnregisterSilos(List<Orleans.Runtime.SiloAddress> siloAddresses) => Task.CompletedTask;
    }
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
