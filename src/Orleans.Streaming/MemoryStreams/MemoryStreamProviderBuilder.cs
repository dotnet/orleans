using Microsoft.Extensions.Configuration;
using Orleans;
using Orleans.Hosting;
using Orleans.Providers;

[assembly: RegisterProvider("Memory", "Streaming", "Client", typeof(MemoryStreamProviderBuilder))]
[assembly: RegisterProvider("Memory", "Streaming", "Silo", typeof(MemoryStreamProviderBuilder))]
namespace Orleans.Providers;

internal sealed class MemoryStreamProviderBuilder : IProviderBuilder<ISiloBuilder>, IProviderBuilder<IClientBuilder>
{
    public void Configure(ISiloBuilder builder, string name, IConfigurationSection configurationSection)
    {
        builder.AddMemoryStreams(name);
    }

    public void Configure(IClientBuilder builder, string name, IConfigurationSection configurationSection)
    {
        builder.AddMemoryStreams(name);
    }
}
