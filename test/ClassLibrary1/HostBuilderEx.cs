using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;

namespace ClassLibrary1;

public static class HostBuilderEx
{
    public const int RebalancerDueTimeSeconds = 10;
    public const int SessionCyclePeriodSeconds = 5; 

    public static IHost CreateHost(this IHostBuilder hostBuilder, int siloNum)
    {
#pragma warning disable ORLEANSEXP002
        var host = hostBuilder
            .ConfigureLogging(builder => builder
                .AddConsole()
                    .AddFilter("Microsoft", LogLevel.Error)
                    .AddFilter("Orleans", LogLevel.Error)
                    .AddFilter("Orleans.Runtime", LogLevel.Warning)
                    .AddFilter("Orleans.Runtime.Placement.Rebalancing", LogLevel.Trace))
            .UseOrleans(builder => builder
                .Configure<SiloMessagingOptions>(o =>
                {
                    o.AssumeHomogenousSilosForTesting = true;
                    o.ClientGatewayShutdownNotificationTimeout = default;
                })
                .Configure<ActivationRebalancerOptions>(o =>
                {
                    o.RebalancerDueTime = TimeSpan.FromSeconds(10);
                    o.SessionCyclePeriod = TimeSpan.FromSeconds(SessionCyclePeriodSeconds);
                })
                .UseLocalhostClustering(
                    siloPort: EndpointOptions.DEFAULT_SILO_PORT + siloNum,
                    gatewayPort: EndpointOptions.DEFAULT_GATEWAY_PORT + siloNum,
                    primarySiloEndpoint: new IPEndPoint(IPAddress.Loopback, EndpointOptions.DEFAULT_SILO_PORT))
                .AddActivationRebalancer())
            .Build();
#pragma warning restore ORLEANSEXP002

        return host;
    }

    public static async Task<List<SiloAddress>> WaitTillClusterIsUp(this IHost host)
    {
        var grainFactory = host.Services.GetRequiredService<IGrainFactory>();
        var mgmtGrain = grainFactory.GetGrain<IManagementGrain>(0);

        Dictionary<SiloAddress, SiloStatus> silos = [];

        while (silos.Count < 4)
        {
            silos = await mgmtGrain.GetHosts(onlyActive: true);
            await Task.Delay(100);
        }

        return silos.Keys.ToList();
    }
}
