using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Connections.Transport;
using Orleans.Hosting;
using Orleans.Runtime.Messaging;

namespace Orleans.TestingHost.UnixSocketTransport;

/// <summary>
/// Extension methods for configuring the Orleans Unix domain socket transport.
/// </summary>
public static class UnixSocketConnectionExtensions
{
    /// <summary>
    /// Configures silo and gateway connections to use Unix domain sockets.
    /// </summary>
    /// <param name="siloBuilder">The silo builder.</param>
    /// <returns>The provided silo builder.</returns>
    public static ISiloBuilder UseUnixSocketConnection(this ISiloBuilder siloBuilder)
    {
        var services = siloBuilder.Services;
        services.RemoveAll<MessageTransportConnector>();
        services.RemoveAll<MessageTransportListener>();
        services.AddSingleton<MessageTransportConnector>(sp => new UnixDomainSocketMessageTransportConnector(
            sp.GetRequiredService<IOptions<UnixSocketConnectionOptions>>(),
            sp.GetRequiredService<ILoggerFactory>()));
        AddListener(services, GatewayConnectionListener.DefaultListenerName, static options => options.GetListeningProxyEndpoint());
        AddListener(services, SiloConnectionListener.DefaultListenerName, static options => options.GetListeningSiloEndpoint());
        return siloBuilder;
    }

    /// <summary>
    /// Configures client connections to use Unix domain sockets.
    /// </summary>
    /// <param name="clientBuilder">The client builder.</param>
    /// <returns>The provided client builder.</returns>
    public static IClientBuilder UseUnixSocketConnection(this IClientBuilder clientBuilder)
    {
        clientBuilder.Services.RemoveAll<MessageTransportConnector>();
        clientBuilder.Services.AddSingleton<MessageTransportConnector>(sp => new UnixDomainSocketMessageTransportConnector(
            sp.GetRequiredService<IOptions<UnixSocketConnectionOptions>>(),
            sp.GetRequiredService<ILoggerFactory>()));
        return clientBuilder;
    }

    private static void AddListener(
        IServiceCollection services,
        string listenerName,
        Func<EndpointOptions, System.Net.IPEndPoint?> getEndpoint)
    {
        services.AddSingleton<MessageTransportListener>(sp => new UnixDomainSocketMessageTransportListener(
            listenerName,
            sp.GetRequiredService<IOptionsMonitor<UnixDomainSocketMessageTransportListenerOptions>>(),
            sp.GetRequiredService<ILoggerFactory>()));
        services.AddOptions<UnixDomainSocketMessageTransportListenerOptions>(listenerName)
            .Configure<IOptions<EndpointOptions>, IOptions<UnixSocketConnectionOptions>>((listenerOptions, endpointOptions, connectionOptions) =>
            {
                var endpoint = getEndpoint(endpointOptions.Value);
                listenerOptions.Enabled = endpoint is not null;
                if (endpoint is not null)
                {
                    listenerOptions.Path = connectionOptions.Value.ConvertEndpointToPath(endpoint);
                }
            });
    }
}
