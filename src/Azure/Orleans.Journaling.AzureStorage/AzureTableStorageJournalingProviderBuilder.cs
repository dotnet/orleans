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
            // TableClientOptions has no bindable constructor, so the binder updates an existing instance.
            if (configurationSection.GetSection(nameof(options.ClientOptions)).Exists())
            {
                options.ClientOptions ??= new();
            }

            var tableName = options.TableName;
            configurationSection.Bind(options);
            if (string.IsNullOrEmpty(options.TableName))
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
