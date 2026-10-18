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
                options.IsResourceCreationEnabled = configurationSection.GetValue(nameof(options.IsResourceCreationEnabled), options.IsResourceCreationEnabled);
                if (configurationSection.GetValue<int?>(nameof(options.DatabaseThroughput)) is { } databaseThroughput)
                {
                    options.DatabaseThroughput = databaseThroughput;
                }
                options.CleanResourcesOnInitialization = configurationSection.GetValue(nameof(options.CleanResourcesOnInitialization), options.CleanResourcesOnInitialization);

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
                options.IsResourceCreationEnabled = configurationSection.GetValue(nameof(options.IsResourceCreationEnabled), options.IsResourceCreationEnabled);
                if (configurationSection.GetValue<int?>(nameof(options.DatabaseThroughput)) is { } databaseThroughput)
                {
                    options.DatabaseThroughput = databaseThroughput;
                }
                options.CleanResourcesOnInitialization = configurationSection.GetValue(nameof(options.CleanResourcesOnInitialization), options.CleanResourcesOnInitialization);

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
