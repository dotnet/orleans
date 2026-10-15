using System.Globalization;
using Azure.Data.Tables;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling;
using Orleans.Providers;

[assembly: RegisterProvider("AzureTableStorage", "Journaling", "Silo", typeof(AzureTableStorageJournalingProviderBuilder))]
namespace Orleans.Hosting;

internal sealed class AzureTableStorageJournalingProviderBuilder : IProviderBuilder<ISiloBuilder>
{
    public void Configure(ISiloBuilder builder, string? name, IConfigurationSection configurationSection)
    {
        name ??= ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME;
        builder.AddAzureTableJournalStorage(name, configure: null);
        var optionsBuilder = builder.Services.AddJournalStorageOptions<AzureTableJournalStorageOptions>(name);
        optionsBuilder.Configure<IServiceProvider>((options, services) =>
        {
            var tableName = configurationSection[nameof(options.TableName)];
            if (!string.IsNullOrEmpty(tableName))
            {
                options.TableName = tableName;
            }

            if (bool.TryParse(configurationSection[nameof(options.DeleteOldGenerations)], out var deleteOldGenerations))
            {
                options.DeleteOldGenerations = deleteOldGenerations;
            }

            if (long.TryParse(configurationSection[nameof(options.CompactionRowCountThreshold)], NumberStyles.Integer, CultureInfo.InvariantCulture, out var compactionRowCountThreshold))
            {
                options.CompactionRowCountThreshold = compactionRowCountThreshold;
            }

            if (long.TryParse(configurationSection[nameof(options.CompactionSizeThreshold)], NumberStyles.Integer, CultureInfo.InvariantCulture, out var compactionSizeThreshold))
            {
                options.CompactionSizeThreshold = compactionSizeThreshold;
            }

            if (int.TryParse(configurationSection[nameof(options.MaxMetadataOnlyConflictRetries)], NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxMetadataOnlyConflictRetries))
            {
                options.MaxMetadataOnlyConflictRetries = maxMetadataOnlyConflictRetries;
            }

            if (TimeSpan.TryParse(configurationSection[nameof(options.MetadataOnlyConflictInitialBackoff)], CultureInfo.InvariantCulture, out var metadataOnlyConflictInitialBackoff))
            {
                options.MetadataOnlyConflictInitialBackoff = metadataOnlyConflictInitialBackoff;
            }

            if (TimeSpan.TryParse(configurationSection[nameof(options.MetadataOnlyConflictMaxBackoff)], CultureInfo.InvariantCulture, out var metadataOnlyConflictMaxBackoff))
            {
                options.MetadataOnlyConflictMaxBackoff = metadataOnlyConflictMaxBackoff;
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
