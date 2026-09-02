using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;

namespace Orleans.Connections.Security.Tests;

internal sealed class RecordingClientBuilder : IClientBuilder
{
    public RecordingClientBuilder()
    {
        Services = new ServiceCollection();
        Configuration = new ConfigurationManager();
    }

    public IServiceCollection Services { get; }

    public IConfiguration Configuration { get; }

    public int ClientConnectionOptionsConfigurationCount =>
        Services.Count(service => service.ServiceType == typeof(IConfigureOptions<ClientConnectionOptions>));

    public ServiceProvider BuildServiceProvider() => Services.BuildServiceProvider();
}

internal sealed class RecordingConnectionBuilder : IConnectionBuilder
{
    private readonly List<Func<ConnectionDelegate, ConnectionDelegate>> _middleware = [];
    private readonly IList<string>? _callOrder;

    public RecordingConnectionBuilder(IServiceProvider applicationServices, IList<string>? callOrder = null)
    {
        ApplicationServices = applicationServices;
        _callOrder = callOrder;
    }

    public IServiceProvider ApplicationServices { get; }

    public int MiddlewareRegistrationCount => _middleware.Count;

    public IConnectionBuilder Use(Func<ConnectionDelegate, ConnectionDelegate> middleware)
    {
        _middleware.Add(middleware);
        _callOrder?.Add("tls");
        return this;
    }

    public ConnectionDelegate Build()
    {
        ConnectionDelegate application = static _ => Task.CompletedTask;
        for (var index = _middleware.Count - 1; index >= 0; index--)
        {
            application = _middleware[index](application);
        }

        return application;
    }
}

internal static class ClientConnectionOptionsTestExtensions
{
    public static void ApplyTo(this ClientConnectionOptions options, IConnectionBuilder builder) =>
        ConfigureConnectionBuilder(options, builder);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ConfigureConnectionBuilder")]
    private static extern void ConfigureConnectionBuilder(ClientConnectionOptions options, IConnectionBuilder builder);
}

internal sealed class RecordingSiloBuilder : ISiloBuilder
{
    public RecordingSiloBuilder()
    {
        Services = new ServiceCollection();
        Configuration = new ConfigurationManager();
    }

    public IServiceCollection Services { get; }

    public IConfiguration Configuration { get; }

    public int SiloConnectionOptionsConfigurationCount =>
        Services.Count(service => service.ServiceType == typeof(IConfigureOptions<SiloConnectionOptions>));

    public ServiceProvider BuildServiceProvider() => Services.BuildServiceProvider();
}

internal static class SiloConnectionOptionsTestExtensions
{
    public static void ApplyGatewayInboundTo(this SiloConnectionOptions options, IConnectionBuilder builder) =>
        ((SiloConnectionOptions.ISiloConnectionBuilderOptions)options).ConfigureGatewayInboundBuilder(builder);

    public static void ApplySiloInboundTo(this SiloConnectionOptions options, IConnectionBuilder builder) =>
        ((SiloConnectionOptions.ISiloConnectionBuilderOptions)options).ConfigureSiloInboundBuilder(builder);

    public static void ApplySiloOutboundTo(this SiloConnectionOptions options, IConnectionBuilder builder) =>
        ((SiloConnectionOptions.ISiloConnectionBuilderOptions)options).ConfigureSiloOutboundBuilder(builder);
}
