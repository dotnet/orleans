using System;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;

namespace Orleans.TestingHost.UnixSocketTransport;

public static class UnixSocketConnectionExtensions
{
    public static ISiloBuilder UseUnixSocketConnection(this ISiloBuilder siloBuilder)
    {
        siloBuilder.ConfigureServices(services =>
        {
            services.AddKeyedSingleton<object, IConnectionFactory>(SiloConnectionFactory.ServicesKey, CreateUnixSocketConnectionFactory());
            services.AddKeyedSingleton<object, IConnectionListenerFactory>(SiloConnectionListener.ServicesKey, CreateUnixSocketConnectionListenerFactory());
            services.AddKeyedSingleton<object, IConnectionListenerFactory>(GatewayConnectionListener.ServicesKey, CreateUnixSocketConnectionListenerFactory());
        });

        return siloBuilder;
    }

    public static IClientBuilder UseUnixSocketConnection(this IClientBuilder clientBuilder)
    {
        clientBuilder.ConfigureServices(services =>
        {
            services.AddKeyedSingleton<object, IConnectionFactory>(ClientOutboundConnectionFactory.ServicesKey, CreateUnixSocketConnectionFactory());
        });

        return clientBuilder;
    }

    private static Func<IServiceProvider, object, IConnectionFactory> CreateUnixSocketConnectionFactory()
    {
        return (IServiceProvider sp, object key) => ActivatorUtilities.CreateInstance<UnixSocketConnectionFactory>(sp);
    }

    private static Func<IServiceProvider, object, IConnectionListenerFactory> CreateUnixSocketConnectionListenerFactory()
    {
        return (IServiceProvider sp, object key) => ActivatorUtilities.CreateInstance<UnixSocketConnectionListenerFactory>(sp);
    }
}
