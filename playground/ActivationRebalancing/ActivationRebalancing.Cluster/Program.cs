using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Runtime.Placement;

#nullable enable

// Ledjon: The silos will run in the same process so they will have the same memory usage.
// I previously had 4 console apps to run the example, but didn't want to add so many proj into the solution.
// I am sure with something like Aspire that would be easier, but for now I'll leave them like this.
// You (the reader) feel free to run this in different processes for a more realistic example.

var host0 = await StartSiloHost(0);
var host1 = await StartSiloHost(1);
var host2 = await StartSiloHost(2);
var host3 = await StartSiloHost(3);
IHost? host5 = null;

Console.WriteLine("All silos have started.");

var grainFactory = host0.Services.GetRequiredService<IGrainFactory>();
var mgmtGrain = grainFactory.GetGrain<IManagementGrain>(0);

var silos = await mgmtGrain.GetHosts(onlyActive: true);
Debug.Assert(silos.Count == 4);
var addresses = silos.Select(x => x.Key).ToArray();

var tasks = new List<Task>();
RequestContext.Set(IPlacementDirector.PlacementHintKey, addresses[0]);
for (var i = 0; i < 300; i++)
{
    tasks.Add(grainFactory.GetGrain<IRebalancingTestGrain>(Guid.NewGuid()).Ping());
}

RequestContext.Set(IPlacementDirector.PlacementHintKey, addresses[1]);
for (var i = 0; i < 30; i++)
{
    tasks.Add(grainFactory.GetGrain<IRebalancingTestGrain>(Guid.NewGuid()).Ping());
}

RequestContext.Set(IPlacementDirector.PlacementHintKey, addresses[2]);
for (var i = 0; i < 410; i++)
{
    tasks.Add(grainFactory.GetGrain<IRebalancingTestGrain>(Guid.NewGuid()).Ping());
}

RequestContext.Set(IPlacementDirector.PlacementHintKey, addresses[3]);
for (var i = 0; i < 120; i++)
{
    tasks.Add(grainFactory.GetGrain<IRebalancingTestGrain>(Guid.NewGuid()).Ping());
}

var sessionCount = 0;
while (true)
{
    if (sessionCount == 25)
    {
        RequestContext.Set(IPlacementDirector.PlacementHintKey, addresses[0]);
        for (var i = 0; i < 50; i++)
        {
            tasks.Add(grainFactory.GetGrain<IRebalancingTestGrain>(Guid.NewGuid()).Ping());
        }

        RequestContext.Set(IPlacementDirector.PlacementHintKey, addresses[1]);
        for (var i = 0; i < 50; i++)
        {
            tasks.Add(grainFactory.GetGrain<IRebalancingTestGrain>(Guid.NewGuid()).Ping());
        }
    }

    if (sessionCount == 35)
    {
        RequestContext.Set(IPlacementDirector.PlacementHintKey, addresses[1]);
        for (var i = 0; i < 50; i++)
        {
            tasks.Add(grainFactory.GetGrain<IRebalancingTestGrain>(Guid.NewGuid()).Ping());
        }

        RequestContext.Set(IPlacementDirector.PlacementHintKey, addresses[2]);
        for (var i = 0; i < 50; i++)
        {
            tasks.Add(grainFactory.GetGrain<IRebalancingTestGrain>(Guid.NewGuid()).Ping());
        }
    }

    if (sessionCount == 40)
    {
        host5 = await StartSiloHost(4);
    }

    if (sessionCount == 45)
    {
        RequestContext.Set(IPlacementDirector.PlacementHintKey, addresses[2]);
        for (var i = 0; i < 50; i++)
        {
            tasks.Add(grainFactory.GetGrain<IRebalancingTestGrain>(Guid.NewGuid()).Ping());
        }

        RequestContext.Set(IPlacementDirector.PlacementHintKey, addresses[3]);
        for (var i = 0; i < 50; i++)
        {
            tasks.Add(grainFactory.GetGrain<IRebalancingTestGrain>(Guid.NewGuid()).Ping());
        }
    }

    await Task.Delay(5000); // session duration
    sessionCount++;

    if (sessionCount > 55)
    {
        break;
    }
}

Console.WriteLine("Simulation has finished. Press Enter to terminate...");
Console.ReadLine();

await host0.StopAsync();
await host1.StopAsync();
await host2.StopAsync();
await host3.StopAsync();

if (host5 != null)
{
    await host5.StopAsync();
}

static async Task<IHost> StartSiloHost(int num)
{
    #pragma warning disable ORLEANSEXP002
        var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(builder => builder
                .AddFilter("", LogLevel.Error)
                .AddFilter("Orleans.Runtime.Placement.Rebalancing", LogLevel.Trace)
                .AddConsole())
            .UseOrleans(builder => builder
                .Configure<ActivationRebalancerOptions>(o =>
                {
                    o.RebalancerDueTime = TimeSpan.FromSeconds(5);
                    o.SessionCyclePeriod = TimeSpan.FromSeconds(5);
                    // uncomment these below, if you want higher migration rate
                    //o.CycleNumberWeight = 1;
                    //o.SiloNumberWeight = 0; 
                })
                .UseLocalhostClustering(
                    siloPort: EndpointOptions.DEFAULT_SILO_PORT + num,
                    gatewayPort: EndpointOptions.DEFAULT_GATEWAY_PORT + num,
                    primarySiloEndpoint: new IPEndPoint(IPAddress.Loopback, EndpointOptions.DEFAULT_SILO_PORT))
                .AddActivationRebalancer())
            .Build();
    #pragma warning restore ORLEANSEXP002

    await host.StartAsync();
    Console.WriteLine($"Silo{num} started.");

    return host;
}

public interface IRebalancingTestGrain : IGrainWithGuidKey
{
    Task Ping();
}

public class RebalancingTestGrain : Grain, IRebalancingTestGrain
{
    public Task Ping() => Task.CompletedTask;
}