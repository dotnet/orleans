using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Hosting;
using Orleans.Providers;

[assembly: RegisterProvider("Default", "BroadcastChannel", "Client", typeof(BroadcastChannelProviderBuilder))]
[assembly: RegisterProvider("Default", "BroadcastChannel", "Silo", typeof(BroadcastChannelProviderBuilder))]

namespace Orleans.Providers;

[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
    Justification = "RegisterProvider exposes this builder to client and silo hosting, which instantiate the registered type when binding provider configuration.")]
internal sealed class BroadcastChannelProviderBuilder : IProviderBuilder<ISiloBuilder>, IProviderBuilder<IClientBuilder>
{
    public void Configure(ISiloBuilder builder, string? name, IConfigurationSection configurationSection)
    {
        builder.AddBroadcastChannel(name!, options => options.Bind(configurationSection));
    }

    public void Configure(IClientBuilder builder, string? name, IConfigurationSection configurationSection)
    {
        builder.AddBroadcastChannel(name!, options => options.Bind(configurationSection));
    }
}
