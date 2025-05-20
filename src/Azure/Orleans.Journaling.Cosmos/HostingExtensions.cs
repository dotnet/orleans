using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration.Internal;
using Orleans.Runtime;
using Orleans.Journaling;
using Orleans.Journaling.Cosmos;

namespace Orleans.Hosting;

public static class HostingExtensions
{
    public static ISiloBuilder AddCosmosLogStorage(this ISiloBuilder builder, Action<CosmosLogStorageOptions>? configure = null)
        => builder.AddCosmosLogStorage<DefaultDocumentIdProvider>(configure);

    public static ISiloBuilder AddCosmosLogStorage<T>(this ISiloBuilder builder, Action<CosmosLogStorageOptions>? configure = null)
        where T : class, IDocumentIdProvider
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

        builder.Services.AddSingleton<T>();
        builder.Services.AddFromExisting<IDocumentIdProvider, T>();
        builder.Services.AddSingleton<CosmosLogStorageProvider>();
        builder.Services.AddFromExisting<IStateMachineStorageProvider, CosmosLogStorageProvider>();
        builder.Services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, CosmosLogStorageProvider>();

        return builder;
    }
}
