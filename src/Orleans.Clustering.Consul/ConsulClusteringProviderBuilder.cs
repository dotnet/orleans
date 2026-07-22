using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Hosting;
using Orleans.Providers;

#nullable disable
[assembly: RegisterProvider("Consul", "Clustering", "Client", typeof(ConsulClusteringProviderBuilder))]
[assembly: RegisterProvider("Consul", "Clustering", "Silo", typeof(ConsulClusteringProviderBuilder))]

namespace Orleans.Hosting;

internal sealed class ConsulClusteringProviderBuilder : IProviderBuilder<ISiloBuilder>, IProviderBuilder<IClientBuilder>
{
    public void Configure(ISiloBuilder builder, string name, IConfigurationSection configurationSection)
    {
        builder.UseConsulSiloClustering(options => options.Bind(configurationSection));
    }

    public void Configure(IClientBuilder builder, string name, IConfigurationSection configurationSection)
    {
        builder.UseConsulClientClustering(options => options.Bind(configurationSection));
    }
}
