using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Base class for journaling tests with common setup using InProcessTestCluster
/// </summary>
public class IntegrationTestFixture : IAsyncLifetime
{
    public InProcessTestCluster Cluster { get; }
    public IClusterClient Client => Cluster.Client;

    public IntegrationTestFixture()
    {
        var builder = new InProcessTestClusterBuilder();
        var storageProvider = new VolatileStateMachineStorageProvider();
        builder.ConfigureSilo((options, siloBuilder) =>
        {
            siloBuilder.AddStateMachineStorage();
            siloBuilder.Services.AddSingleton<IStateMachineStorageProvider>(storageProvider);
        });
        ConfigureTestCluster(builder);
        Cluster = builder.Build();
    }

    protected virtual void ConfigureTestCluster(InProcessTestClusterBuilder builder)
    {
    }

    public virtual async Task InitializeAsync()
    {
        await Cluster.DeployAsync();
    }

    public virtual async Task DisposeAsync()
    {
        if (Cluster != null)
        {
            await Cluster.DisposeAsync();
        }
    }
}
