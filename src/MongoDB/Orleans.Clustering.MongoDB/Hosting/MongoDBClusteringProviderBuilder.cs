using Microsoft.Extensions.Configuration;
using Orleans;
using Orleans.Providers;

[assembly: RegisterProvider("MongoDB", "Clustering", "Silo", typeof(Orleans.Hosting.MongoDBClusteringProviderBuilder))]
[assembly: RegisterProvider("MongoDB", "Clustering", "Client", typeof(Orleans.Hosting.MongoDBClusteringProviderBuilder))]
[assembly: RegisterProvider("MongoDBDatabase", "Clustering", "Silo", typeof(Orleans.Hosting.MongoDBClusteringProviderBuilder))]
[assembly: RegisterProvider("MongoDBDatabase", "Clustering", "Client", typeof(Orleans.Hosting.MongoDBClusteringProviderBuilder))]

namespace Orleans.Hosting;

internal sealed class MongoDBClusteringProviderBuilder : IProviderBuilder<ISiloBuilder>, IProviderBuilder<IClientBuilder>
{
    public void Configure(ISiloBuilder builder, string? name, IConfigurationSection configurationSection)
    {
        builder.UseMongoDBMembershipTable(configurationSection);
    }

    public void Configure(IClientBuilder builder, string? name, IConfigurationSection configurationSection)
    {
        builder.UseMongoDBClustering(configurationSection);
    }
}
