using Azure.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling;
using Orleans.Journaling.Cosmos;
using Orleans.Providers;

namespace Orleans.Hosting;

internal sealed class CosmosGrainJournalingProviderBuilder : IProviderBuilder<ISiloBuilder>
{
    public void Configure(ISiloBuilder builder, string name, IConfigurationSection configurationSection)
    {
        builder.AddCosmosLogStorage();

        var optionsBuilder = builder.Services.AddOptions<CosmosLogStorageOptions>();

        optionsBuilder.Configure<IServiceProvider>((options, services) =>
        {
            var dbName = configurationSection["DatabaseName"];
            if (!string.IsNullOrEmpty(dbName))
            {
                options.DatabaseName = dbName;
            }

            var containerName = configurationSection["ContainerName"];
            if (!string.IsNullOrEmpty(containerName))
            {
                options.ContainerName = containerName;
            }

            var isResourceCreationEnabled = configurationSection.GetValue("IsResourceCreationEnabled", false);
            options.IsResourceCreationEnabled = isResourceCreationEnabled;

            var cleanResources = configurationSection.GetValue("CleanResourcesOnInitialization", false);
            options.CleanResourcesOnInitialization = cleanResources;

            var connectionString = configurationSection["ConnectionString"];
            var endpoint = configurationSection["AccountEndpoint"];
            var key = configurationSection["AuthKey"];
            var useAzureCredential = configurationSection.GetValue("UseAzureCredential", false);

            if (!string.IsNullOrEmpty(connectionString))
            {
                options.ConfigureCosmosClient(connectionString);
            }
            else if (!string.IsNullOrEmpty(endpoint) && !string.IsNullOrEmpty(key))
            {
                options.ConfigureCosmosClient(endpoint, key);
            }
            else if (!string.IsNullOrEmpty(endpoint) && useAzureCredential)
            {
                var credential = services.GetRequiredService<TokenCredential>();
                options.ConfigureCosmosClient(endpoint, credential);
            }
            else
            {
                throw new InvalidOperationException("Cosmos DB configuration is missing. " +
                    "Provide either ConnectionString or (AccountEndpoint + AuthKey).");
            }

            var throughput = configurationSection["DatabaseThroughput"];
            if (int.TryParse(throughput, out var t))
            {
                options.DatabaseThroughput = t;
            }
        });
    }
}
