using Azure.Data.Tables;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling;
using Orleans.Providers;

[assembly: RegisterProvider("AzureTableStorage", "GrainJournaling", "Silo", typeof(AzureTableStorageGrainJournalingProviderBuilder))]
namespace Orleans.Hosting;

internal sealed class AzureTableStorageGrainJournalingProviderBuilder : IProviderBuilder<ISiloBuilder>
{
    public void Configure(ISiloBuilder builder, string? name, IConfigurationSection configurationSection)
    {
        builder.AddAzureTableJournalStorage();
        var optionsBuilder = builder.Services.AddOptions<AzureTableJournalStorageOptions>();
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
                // Construct a table service client from a connection string.
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
                    }
                    else
                    {
                        options.TableServiceClient = new(connectionString);
                    }
                }
            }
        });

        var journalFormatKey = configurationSection[nameof(JournaledStateManagerOptions.JournalFormatKey)];
        if (!string.IsNullOrWhiteSpace(journalFormatKey))
        {
            builder.Services.Configure<JournaledStateManagerOptions>(options => options.JournalFormatKey = journalFormatKey);
        }
    }
}
