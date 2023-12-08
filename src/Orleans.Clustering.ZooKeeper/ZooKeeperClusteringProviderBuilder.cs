using System;
using Orleans.Providers;
using Microsoft.Extensions.Configuration;
using Orleans;
using Orleans.Hosting;
using Microsoft.Extensions.DependencyInjection;

[assembly: RegisterProvider("ZooKeeper", "Clustering", "Client", typeof(ZooKeeperClusteringProviderBuilder))]
[assembly: RegisterProvider("ZooKeeper", "Clustering", "Silo", typeof(ZooKeeperClusteringProviderBuilder))]

namespace Orleans.Hosting;

internal sealed class ZooKeeperClusteringProviderBuilder : IProviderBuilder<ISiloBuilder>, IProviderBuilder<IClientBuilder>
{
    public void Configure(ISiloBuilder builder, string name, IConfigurationSection configurationSection)
    {
        builder.UseZooKeeperClustering(options => options.Bind(configurationSection));
    }

    public void Configure(IClientBuilder builder, string name, IConfigurationSection configurationSection)
    {
        builder.UseZooKeeperClustering(options => options.Bind(configurationSection));
    }
}
