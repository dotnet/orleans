using System;
using System.Threading.Tasks;
using Azure.Data.Tables;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Clustering.AzureStorage;
using Orleans.Hosting;
using Orleans.Providers;

[assembly: RegisterProvider("AzureTableStorage", "Clustering", "Silo", typeof(AzureTableStorageClusteringProviderBuilder))]
[assembly: RegisterProvider("AzureTableStorage", "Clustering", "Client", typeof(AzureTableStorageClusteringProviderBuilder))]

namespace Orleans.Hosting;

internal sealed class AzureTableStorageClusteringProviderBuilder : IProviderBuilder<ISiloBuilder>, IProviderBuilder<IClientBuilder>
{
    public void Configure(ISiloBuilder builder, string name, IConfigurationSection configurationSection)
    {
        builder.UseAzureStorageClustering((OptionsBuilder<AzureStorageClusteringOptions> optionsBuilder) =>
            optionsBuilder.Configure<IServiceProvider>((options, services) =>
            {
                var tableName = configurationSection["TableName"];
                if (!string.IsNullOrEmpty(tableName))
                {
                    options.TableName = tableName; 
                }

                var serviceKey = configurationSection["ServiceKey"];
                if (!string.IsNullOrEmpty(serviceKey))
                {
                    // Get a client by name.
                    options.TableServiceClient = services.GetRequiredKeyedService<TableServiceClient>(serviceKey);
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
                        if (Uri.TryCreate(connectionString, UriKind.Absolute, out var uri))
                        {
                            options.TableServiceClient = new TableServiceClient(uri);
                        }
                        else
                        {
                            options.TableServiceClient = new TableServiceClient(connectionString);
                        }
                    }
                }
            }));
    }

    public void Configure(IClientBuilder builder, string name, IConfigurationSection configurationSection)
    {
        builder.UseAzureStorageClustering((OptionsBuilder<AzureStorageGatewayOptions> optionsBuilder) =>
            optionsBuilder.Configure<IServiceProvider>((options, services) =>
            {
                var tableName = configurationSection["TableName"];
                if (!string.IsNullOrEmpty(tableName))
                {
                    options.TableName = tableName; 
                }

                var serviceKey = configurationSection["ServiceKey"];
                if (!string.IsNullOrEmpty(serviceKey))
                {
                    // Get a client by name.
                    options.TableServiceClient = services.GetRequiredKeyedService<TableServiceClient>(serviceKey);
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
                        if (Uri.TryCreate(connectionString, UriKind.Absolute, out var uri))
                        {
                            options.TableServiceClient = new TableServiceClient(uri);
                        }
                        else
                        {
                            options.TableServiceClient = new TableServiceClient(connectionString);
                        }
                    }
                }
            }));
    }
}
