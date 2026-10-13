using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans;
using Orleans.Providers;
using Orleans.Providers.MongoDB.Configuration;
using Orleans.Providers.MongoDB.Utils;

[assembly: RegisterProvider("MongoDB", "Clustering", "Silo", typeof(Orleans.Hosting.MongoDBClusteringProviderBuilder))]
[assembly: RegisterProvider("MongoDB", "Clustering", "Client", typeof(Orleans.Hosting.MongoDBClusteringProviderBuilder))]

namespace Orleans.Hosting;

internal sealed class MongoDBClusteringProviderBuilder : IProviderBuilder<ISiloBuilder>, IProviderBuilder<IClientBuilder>
{
    public void Configure(ISiloBuilder builder, string? name, IConfigurationSection configurationSection)
    {
        builder.UseMongoDBMembershipTable(configurationSection);
        builder.Services.TryAddSingleton<IMongoClientFactory, DefaultMongoClientFactory>();
    }

    public void Configure(IClientBuilder builder, string? name, IConfigurationSection configurationSection)
    {
        builder.UseMongoDBClustering(configurationSection);
        builder.Services.TryAddSingleton<IMongoClientFactory, DefaultMongoClientFactory>();
    }
}
