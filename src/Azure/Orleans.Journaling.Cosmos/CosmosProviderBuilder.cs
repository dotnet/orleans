using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Hosting;
using Orleans.Journaling.Cosmos;
using Orleans.Providers;

[assembly: RegisterProvider("AzureCosmosDB", "GrainJournaling", "Silo", typeof(CosmosProviderBuilder))]

namespace Orleans.Hosting;

internal sealed class CosmosProviderBuilder : IProviderBuilder<ISiloBuilder>
{
    public void Configure(ISiloBuilder builder, string name, IConfigurationSection configurationSection)
    {
        builder.AddCosmosLogStorage();

        var optionsBuilder = builder.Services.AddOptions<CosmosLogStorageOptions>();

        optionsBuilder.Configure<IServiceProvider>((options, services) =>
        {
            var databaseName = configurationSection[nameof(options.DatabaseName)];
            if (!string.IsNullOrEmpty(databaseName))
            {
                options.DatabaseName = databaseName;
            }

            var containerName = configurationSection[nameof(options.ContainerName)];
            if (!string.IsNullOrEmpty(containerName))
            {
                options.ContainerName = containerName;
            }

            if (int.TryParse(configurationSection[nameof(options.CompactionThreshold)], out var ct))
            {
                options.CompactionThreshold = ct;
            }

            if (bool.TryParse(configurationSection[nameof(options.IsResourceCreationEnabled)], out var irce))
            {
                options.IsResourceCreationEnabled = irce;
            }

            if (bool.TryParse(configurationSection[nameof(options.CleanResourcesOnInitialization)], out var croi))
            {
                options.CleanResourcesOnInitialization = croi;
            }

            if (int.TryParse(configurationSection[nameof(options.DatabaseThroughput)], out var dt))
            {
                options.DatabaseThroughput = dt;
            }

            var serviceKey = configurationSection["ServiceKey"];
            if (!string.IsNullOrEmpty(serviceKey))
            {
                options.ConfigureCosmosClient(sp =>
                    new ValueTask<CosmosClient>(sp.GetRequiredKeyedService<CosmosClient>(serviceKey)));
            }
            else
            {
                var connectionName = configurationSection["ConnectionName"];
                var connectionString = configurationSection["ConnectionString"];
                if (!string.IsNullOrEmpty(connectionName) && string.IsNullOrEmpty(connectionString))
                {
                    var rootConfiguration = services.GetRequiredService<IConfiguration>();
                    connectionString = rootConfiguration.GetConnectionString(connectionName);
                }

                if (!string.IsNullOrEmpty(connectionString))
                {
                    options.ConfigureCosmosClient(connectionString);
                }
            }
        });
    }
}
