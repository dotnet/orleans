using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Hosting;
using Orleans.Providers;

#nullable disable
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
