using System;
using System.Threading.Tasks;
using Azure.Data.Tables;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Clustering.AzureStorage;
using Orleans.Providers;

namespace Orleans.Hosting;

internal sealed class AzureTableStorageClusteringProviderBuilder : IProviderBuilder<ISiloBuilder>, IProviderBuilder<IClientBuilder>
{
    public void Configure(ISiloBuilder builder, string name, IConfigurationSection configurationSection)
    {
        builder.UseAzureStorageClustering((OptionsBuilder<AzureStorageClusteringOptions> optionsBuilder) =>
            optionsBuilder.Configure<IServiceProvider>((options, services) =>
            {
                var serviceKey = configurationSection["ServiceKey"];
                if (!string.IsNullOrEmpty(serviceKey))
                {
                    // Get a client by name.
                    var client = services.GetKeyedService<TableServiceClient>(serviceKey);
                    options.ConfigureTableServiceClient(() => Task.FromResult(client));
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
                        options.ConfigureTableServiceClient(connectionString);
                    }
                }
            }));
    }

    public void Configure(IClientBuilder builder, string name, IConfigurationSection configurationSection) => throw new System.NotImplementedException();
}
