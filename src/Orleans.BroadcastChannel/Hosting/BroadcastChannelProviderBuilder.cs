using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Hosting;
using Orleans.Providers;

[assembly: RegisterProvider("Default", "BroadcastChannel", "Client", typeof(BroadcastChannelProviderBuilder))]
[assembly: RegisterProvider("Default", "BroadcastChannel", "Silo", typeof(BroadcastChannelProviderBuilder))]

namespace Orleans.Providers;

internal sealed class BroadcastChannelProviderBuilder : IProviderBuilder<ISiloBuilder>, IProviderBuilder<IClientBuilder>
{
    public void Configure(ISiloBuilder builder, string name, IConfigurationSection configurationSection)
    {
        builder.AddBroadcastChannel(name, options => options.Bind(configurationSection));
    }

    public void Configure(IClientBuilder builder, string name, IConfigurationSection configurationSection)
    {
        builder.AddBroadcastChannel(name, options => options.Bind(configurationSection));
    }
}

