using System;
using System.Threading.Tasks;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers;
using Orleans.AdvancedReminders.AzureStorage;

[assembly: RegisterProvider("AzureTableStorage", "AdvancedReminders", "Silo", typeof(AdvancedAzureTableStorageRemindersProviderBuilder))]

namespace Orleans.Hosting;

internal sealed class AdvancedAzureTableStorageRemindersProviderBuilder : IProviderBuilder<ISiloBuilder>
{
    public void Configure(ISiloBuilder builder, string? name, IConfigurationSection configurationSection)
    {
        builder.UseAzureTableAdvancedReminderService((OptionsBuilder<AzureTableReminderStorageOptions> optionsBuilder) =>
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
                    options.BlobServiceClient = services.GetRequiredKeyedService<BlobServiceClient>(serviceKey);
                }
                else
                {
                    // Construct clients from a connection string.
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
                            options.TableServiceClient = new(uri);
                            options.BlobServiceClient = new(CreateBlobServiceUri(uri));
                        }
                        else
                        {
                            options.TableServiceClient = new(connectionString);
                            options.BlobServiceClient = new(connectionString);
                        }
                    }
                }
            }));
    }

    private static Uri CreateBlobServiceUri(Uri serviceUri)
    {
        if (serviceUri.Host.Contains(".table.", StringComparison.OrdinalIgnoreCase))
        {
            var builder = new UriBuilder(serviceUri)
            {
                Host = serviceUri.Host.Replace(".table.", ".blob.", StringComparison.OrdinalIgnoreCase),
            };

            return builder.Uri;
        }

        return serviceUri;
    }
}
