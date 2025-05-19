using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration.Internal;
using Orleans.Runtime;
using Orleans.Journaling;
using Orleans.Journaling.Cosmos;

namespace Orleans.Hosting;

public static class CosmosLogStorageHostingExtensions
{
    public static ISiloBuilder AddCosmosLogStorage(this ISiloBuilder builder) => builder.AddCosmosLogStorage(null);

    public static ISiloBuilder AddCosmosLogStorage(this ISiloBuilder builder, Action<CosmosLogStorageOptions>? configure)
    {
        builder.AddStateMachineStorage();

        var options = builder.Services.AddOptions<CosmosLogStorageOptions>();
        if (configure != null)
        {
            options.Configure(configure);
        }

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
