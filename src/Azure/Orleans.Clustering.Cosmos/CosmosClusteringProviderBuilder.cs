using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Clustering.Cosmos;
using Orleans.Hosting;
using Orleans.Providers;

[assembly: RegisterProvider("AzureCosmosDB", "Clustering", "Silo", typeof(CosmosClusteringProviderBuilder))]
[assembly: RegisterProvider("AzureCosmosDB", "Clustering", "Client", typeof(CosmosClusteringProviderBuilder))]

namespace Orleans.Hosting;

internal sealed class CosmosClusteringProviderBuilder : IProviderBuilder<ISiloBuilder>, IProviderBuilder<IClientBuilder>
{
    public void Configure(ISiloBuilder builder, string? name, IConfigurationSection configurationSection) =>
        builder.UseCosmosClustering(optionsBuilder =>
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
                if (bool.TryParse(configurationSection[nameof(options.IsResourceCreationEnabled)], out var irce))
                {
                    options.IsResourceCreationEnabled = irce;
                }
                if(int.TryParse(configurationSection[nameof(options.DatabaseThroughput)], out var dt))
                {
                    options.DatabaseThroughput = dt;
                }
                if(bool.TryParse(configurationSection[nameof(options.CleanResourcesOnInitialization)], out var croi))
                {
                    options.CleanResourcesOnInitialization = croi;
                }

                var serviceKey = configurationSection["ServiceKey"];
                if(!string.IsNullOrEmpty(serviceKey))
                {
                    options.ConfigureCosmosClient(sp=>
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
            }));

    public void Configure(IClientBuilder builder, string? name, IConfigurationSection configurationSection) =>
        builder.UseCosmosGatewayListProvider(optionsBuilder =>
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
                if (bool.TryParse(configurationSection[nameof(options.IsResourceCreationEnabled)], out var irce))
                {
                    options.IsResourceCreationEnabled = irce;
                }
                if (int.TryParse(configurationSection[nameof(options.DatabaseThroughput)], out var dt))
                {
                    options.DatabaseThroughput = dt;
                }
                if (bool.TryParse(configurationSection[nameof(options.CleanResourcesOnInitialization)], out var croi))
                {
                    options.CleanResourcesOnInitialization = croi;
                }

                var serviceKey = configurationSection["ServiceKey"];
                if (!string.IsNullOrEmpty(serviceKey))
                {
                    options.ConfigureCosmosClient(sp =>
                        new ValueTask<CosmosClient>(sp.GetRequiredKeyedService<CosmosClient>(serviceKey)));
                }
                else
                {
                    // Construct a connection multiplexer from a connection string.
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
            }));
}
