using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Persistence.Cosmos;
using Orleans.Providers;

[assembly: RegisterProvider("AzureCosmosDB", "GrainStorage", "Silo", typeof(CosmosGrainStorageProviderBuilder))]

namespace Orleans.Hosting;

internal sealed class CosmosGrainStorageProviderBuilder : IProviderBuilder<ISiloBuilder>
{
    public void Configure(ISiloBuilder builder, string? name, IConfigurationSection configurationSection)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var documentIdProviderKey = configurationSection["DocumentIdProviderKey"];
        if (!string.IsNullOrEmpty(documentIdProviderKey))
        {
            builder.Services.AddKeyedSingleton<IDocumentIdProvider>(
                name,
                (services, _) => services.GetRequiredKeyedService<IDocumentIdProvider>(documentIdProviderKey));
        }

        builder.AddCosmosGrainStorage(name, (OptionsBuilder<CosmosGrainStorageOptions> optionsBuilder) =>
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
                }

                if (!string.IsNullOrEmpty(connectionString))
                {
                    options.ConfigureCosmosClient(connectionString);
                }
            });
        });
    }
}
