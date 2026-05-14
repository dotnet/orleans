#nullable enable
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Connections.Transport;
using Orleans.Hosting;

namespace Orleans.TestingHost.InMemoryTransport;

internal static class InMemoryTransportHostingExtensions
{
    public static IClientBuilder UseInMemoryTransport(this IClientBuilder clientBuilder, InMemoryTransportConnectionHub hub)
    {
        clientBuilder.Services.RemoveAll<MessageTransportConnector>();
        clientBuilder.Services.AddSingleton<MessageTransportConnector>(sp => new InMemoryTransportConnector(hub, sp.GetRequiredService<ILoggerFactory>()));
        return clientBuilder;
    }

    public static ISiloBuilder UseInMemoryTransport(this ISiloBuilder siloBuilder, InMemoryTransportConnectionHub hub)
    {
        siloBuilder.Services.RemoveAll<MessageTransportConnector>();
        siloBuilder.Services.RemoveAll<MessageTransportListener>();
        siloBuilder.Services.AddSingleton<MessageTransportConnector>(sp => new InMemoryTransportConnector(hub, sp.GetRequiredService<ILoggerFactory>()));
        siloBuilder.Services.AddSingleton<MessageTransportListener>(sp => new InMemoryTransportListener(
            "gateway",
            sp.GetRequiredService<IOptions<EndpointOptions>>().Value.GetListeningProxyEndpoint().ToString(),
            hub));
        siloBuilder.Services.AddSingleton<MessageTransportListener>(sp => new InMemoryTransportListener(
            "silo",
            sp.GetRequiredService<IOptions<EndpointOptions>>().Value.GetListeningSiloEndpoint().ToString(),
            hub));
        return siloBuilder;
    }
}
