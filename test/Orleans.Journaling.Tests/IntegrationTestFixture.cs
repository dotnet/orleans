using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling.Json;
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
        var storageProvider = new VolatileJournalStorageProvider();
        builder.ConfigureSilo((options, siloBuilder) =>
        {
            siloBuilder.AddJournalStorage();
            siloBuilder.UseJsonJournalFormat(JournalingTestsJsonContext.Default);
            siloBuilder.Services.AddSingleton(storageProvider);
            siloBuilder.Services.AddScoped<IJournalStorage>(sp => storageProvider.Create(sp.GetRequiredService<IGrainContext>()));
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

[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(uint))]
[JsonSerializable(typeof(ulong))]
[JsonSerializable(typeof(TestDurableGrainState))]
[JsonSerializable(typeof(TestPerson))]
internal sealed partial class JournalingTestsJsonContext : JsonSerializerContext;
