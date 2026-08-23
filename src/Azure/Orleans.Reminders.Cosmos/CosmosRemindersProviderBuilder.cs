using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Providers;
using Orleans.Reminders.Cosmos;

[assembly: RegisterProvider("AzureCosmosDB", "Reminders", "Silo", typeof(Orleans.Hosting.CosmosRemindersProviderBuilder))]

namespace Orleans.Hosting;

internal sealed class CosmosRemindersProviderBuilder : IProviderBuilder<ISiloBuilder>
{
    public void Configure(ISiloBuilder builder, string? name, IConfigurationSection configurationSection)
    {
        builder.UseCosmosReminderService((OptionsBuilder<CosmosReminderTableOptions> optionsBuilder) =>
        {
            optionsBuilder.Bind(configurationSection);
            optionsBuilder.Configure<IServiceProvider>((options, services) =>
            {
                var serviceKey = configurationSection["ServiceKey"];
                if (!string.IsNullOrEmpty(serviceKey))
                {
                    options.ConfigureCosmosClient(
                        provider => new ValueTask<CosmosClient>(provider.GetRequiredKeyedService<CosmosClient>(serviceKey)));
                    return;
                }

                var connectionName = configurationSection["ConnectionName"];
                var connectionString = configurationSection["ConnectionString"];
                if (!string.IsNullOrEmpty(connectionName) && string.IsNullOrEmpty(connectionString))
                {
                    connectionString = services.GetRequiredService<IConfiguration>().GetConnectionString(connectionName);
                    if (string.IsNullOrEmpty(connectionString))
                    {
                        throw new OrleansConfigurationException(
                            $"Cosmos reminder provider configuration '{configurationSection.Path}' references connection string '{connectionName}', but it was not found.");
                    }
                }

                if (!string.IsNullOrEmpty(connectionString))
                {
                    options.ConfigureCosmosClient(connectionString);
                }
            });
        });
    }
}
