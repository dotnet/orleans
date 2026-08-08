using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;

namespace Tests;

public static class ClusterConfiguration
{
    // <configure_cluster>
    public static InProcessTestCluster Create(SharedTestState sharedState)
    {
        var builder = new InProcessTestClusterBuilder(initialSilosCount: 2);

        builder.ConfigureHost(hostBuilder =>
        {
            hostBuilder.Services.AddSingleton(sharedState);
        });

        builder.ConfigureSilo((siloOptions, siloBuilder) =>
        {
            siloBuilder.AddMemoryGrainStorageAsDefault();
            siloBuilder.Services.AddSingleton(siloOptions);
        });

        builder.ConfigureClient(clientBuilder =>
        {
            clientBuilder.Services.AddSingleton<ClientTestService>();
        });

        return builder.Build();
    }
    // </configure_cluster>

    // <change_topology>
    public static async Task AddAndRemoveSiloAsync(
        InProcessTestCluster cluster)
    {
        var addedSilo = await cluster.StartAdditionalSiloAsync();
        await cluster.WaitForLivenessToStabilizeAsync();

        await cluster.StopSiloAsync(addedSilo);
        await cluster.WaitForLivenessToStabilizeAsync();
    }
    // </change_topology>
}

public sealed class SharedTestState;

public sealed class ClientTestService;
