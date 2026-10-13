using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Providers;
using Orleans.Persistence.MongoDB;

[assembly: RegisterProvider("MongoDB", "GrainStorage", "Silo", typeof(Orleans.Hosting.MongoDBGrainStorageProviderBuilder))]
[assembly: RegisterProvider("MongoDBDatabase", "GrainStorage", "Silo", typeof(Orleans.Hosting.MongoDBGrainStorageProviderBuilder))]

namespace Orleans.Hosting;

internal sealed class MongoDBGrainStorageProviderBuilder : IProviderBuilder<ISiloBuilder>
{
    public void Configure(ISiloBuilder builder, string? name, IConfigurationSection configurationSection)
    {
        builder.AddMongoDBGrainStorage(name ?? ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME,
            (OptionsBuilder<MongoDBGrainStorageOptions> options) => options.Configure<IServiceProvider>(
                (value, services) => value.ConfigureMongoDBOptions(services, configurationSection)));
    }
}
