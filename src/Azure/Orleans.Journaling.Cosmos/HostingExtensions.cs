using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration.Internal;
using Orleans.Journaling;
using Orleans.Journaling.Cosmos;

namespace Orleans.Hosting;

/// <summary>
/// Extension methods for configuring Azure Cosmos DB log-based storage provider.
/// </summary>
public static class HostingExtensions
{
    /// <summary>
    /// Configure the Azure Cosmos DB log-based storage provider in the silo.
    /// </summary>
    public static ISiloBuilder AddCosmosLogStorage(this ISiloBuilder builder) => builder.AddCosmosLogStorage(null);

    /// <summary>
    /// Configure the Azure Cosmos DB log-based storage provider in the silo.
    /// </summary>
    /// <param name="configure">Configure the <see cref="CosmosLogStorageOptions"/></param>
    public static ISiloBuilder AddCosmosLogStorage(this ISiloBuilder builder, Action<CosmosLogStorageOptions>? configure)
    {
        builder.AddStateMachineStorage();

        var optionsBuilder = builder.Services.AddOptions<CosmosLogStorageOptions>();
        if (configure != null)
        {
            optionsBuilder.Configure(configure);
        }

        builder.Services.AddTransient<IConfigurationValidator>(sp => new CosmosLogStorageOptionsValidator(
            sp.GetRequiredService<IOptionsMonitor<CosmosLogStorageOptions>>().CurrentValue));

        if (builder.Services.Any(service => service.ServiceType.Equals(typeof(CosmosLogStorageProvider))))
        {
            return builder;
        }

        builder.Services.AddSingleton<CosmosLogStorageProvider>();
        builder.Services.AddFromExisting<IStateMachineStorageProvider, CosmosLogStorageProvider>();
        builder.Services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, CosmosLogStorageProvider>();

        return builder;
    }
}
