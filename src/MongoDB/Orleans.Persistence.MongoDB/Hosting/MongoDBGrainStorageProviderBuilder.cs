using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Providers;
using Orleans.Providers.MongoDB.Configuration;
using Orleans.Providers.MongoDB.Utils;

[assembly: RegisterProvider("MongoDB", "GrainStorage", "Silo", typeof(Orleans.Hosting.MongoDBGrainStorageProviderBuilder))]

namespace Orleans.Hosting;

internal sealed class MongoDBGrainStorageProviderBuilder : IProviderBuilder<ISiloBuilder>
{
    public void Configure(ISiloBuilder builder, string? name, IConfigurationSection configurationSection)
    {
        builder.AddMongoDBGrainStorage(name ?? ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME,
            (OptionsBuilder<MongoDBGrainStorageOptions> options) => options.Bind(configurationSection));
        builder.Services.TryAddSingleton<IMongoClientFactory, DefaultMongoClientFactory>();
    }
}
