using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TestExtensions;
using Xunit;

namespace Orleans.TestingHost.Tests;

public class ClientLifecycleTests
{
    [TestSuite("Functional")]
    [TestProvider("None")]
    [Fact, TestCategory("Functional")]
    public async Task TestCluster_ClientProperties_AreAvailableOnlyWhileDeployed()
    {
        var builder = new TestClusterBuilder(1);
        builder.Options.ServiceId = Guid.NewGuid().ToString();
        builder.ConfigureHostConfiguration(TestDefaultConfiguration.ConfigureHostConfiguration);
        await using var cluster = builder.Build();

        AssertClientUnavailable(() => cluster.Client);
        AssertClientUnavailable(() => cluster.GrainFactory);

        await cluster.DeployAsync(TestContext.Current.CancellationToken);

        Assert.Same(cluster.Client, cluster.GrainFactory);

        await cluster.StopClusterClientAsync(TestContext.Current.CancellationToken);

        AssertClientUnavailable(() => cluster.Client);
        AssertClientUnavailable(() => cluster.GrainFactory);
    }

    [TestSuite("Functional")]
    [TestProvider("None")]
    [Fact, TestCategory("Functional")]
    public async Task InProcessTestCluster_Client_IsAvailableOnlyWhileDeployed()
    {
        var builder = new InProcessTestClusterBuilder(1);
        builder.ConfigureHost(hostBuilder => TestDefaultConfiguration.ConfigureHostConfiguration(hostBuilder.Configuration));
        await using var cluster = builder.Build();

        AssertClientUnavailable(() => cluster.Client);

        await cluster.DeployAsync(TestContext.Current.CancellationToken);

        var client = cluster.Client;
        Assert.Same(client, cluster.Client);

        await cluster.StopClusterClientAsync(TestContext.Current.CancellationToken);

        AssertClientUnavailable(() => cluster.Client);
    }

    [TestSuite("Functional")]
    [TestProvider("None")]
    [Fact, TestCategory("Functional")]
    public async Task TestCluster_ClientProperties_RemainUnavailableWhenStartupFails()
    {
        var builder = new TestClusterBuilder(1);
        builder.Options.ServiceId = Guid.NewGuid().ToString();
        builder.ConfigureHostConfiguration(TestDefaultConfiguration.ConfigureHostConfiguration);
        builder.AddClientBuilderConfigurator<FailingClientConfigurator>();
        await using var cluster = builder.Build();

        await Assert.ThrowsAsync<TimeoutException>(() => cluster.DeployAsync(TestContext.Current.CancellationToken));

        AssertClientUnavailable(() => cluster.Client);
        AssertClientUnavailable(() => cluster.GrainFactory);
    }

    [TestSuite("Functional")]
    [TestProvider("None")]
    [Fact, TestCategory("Functional")]
    public async Task InProcessTestCluster_Client_RemainsUnavailableWhenStartupFails()
    {
        var builder = new InProcessTestClusterBuilder(1);
        builder.ConfigureHost(hostBuilder => TestDefaultConfiguration.ConfigureHostConfiguration(hostBuilder.Configuration));
        builder.ConfigureClientHost(hostBuilder => hostBuilder.Services.AddSingleton<IHostedService, FailingHostedService>());
        await using var cluster = builder.Build();

        await Assert.ThrowsAsync<TimeoutException>(() => cluster.DeployAsync(TestContext.Current.CancellationToken));

        AssertClientUnavailable(() => cluster.Client);
    }

    private static void AssertClientUnavailable(Func<object> accessor)
    {
        var exception = Assert.Throws<InvalidOperationException>(accessor);
        Assert.Contains("has not been deployed or has been stopped", exception.Message);
    }

    private sealed class FailingClientConfigurator : IClientBuilderConfigurator
    {
        public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            => clientBuilder.Services.AddSingleton<IHostedService, FailingHostedService>();
    }

    private sealed class FailingHostedService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.FromException(new TimeoutException("Expected client startup failure."));

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
