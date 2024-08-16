using ClassLibrary1;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Configuration;
using Orleans.Runtime.Placement;

var host = Host.CreateDefaultBuilder(args).CreateHost(1);
await host.StartAsync();

var silos = await host.WaitTillClusterIsUp();

var grainFactory = host.Services.GetRequiredService<IGrainFactory>();
var mgmtGrain = grainFactory.GetGrain<IManagementGrain>(0);

var primary = silos.First(x => x.Endpoint.Port == EndpointOptions.DEFAULT_SILO_PORT);
var seconday = silos.First(x => x.Endpoint.Port == EndpointOptions.DEFAULT_SILO_PORT + 1);
var tertiary = silos.First(x => x.Endpoint.Port == EndpointOptions.DEFAULT_SILO_PORT + 2);
var quaternary = silos.First(x => x.Endpoint.Port == EndpointOptions.DEFAULT_SILO_PORT + 3);

const int GrainCountPrimary = 1000;

var tasks = new List<Task>();

RequestContext.Set(IPlacementDirector.PlacementHintKey, primary);
for (var i = 0; i < GrainCountPrimary; i++)
{
    tasks.Add(grainFactory.GetGrain<ITestGrain>(Guid.NewGuid()).Ping());
}

const int GrainCountSecondary = 50;

RequestContext.Set(IPlacementDirector.PlacementHintKey, seconday);
for (var i = 0; i < GrainCountPrimary; i++)
{
    tasks.Add(grainFactory.GetGrain<ITestGrain>(Guid.NewGuid()).Ping());
}

const int GrainCountTertiary = 250;

RequestContext.Set(IPlacementDirector.PlacementHintKey, tertiary);
for (var i = 0; i < GrainCountTertiary; i++)
{
    tasks.Add(grainFactory.GetGrain<ITestGrain>(Guid.NewGuid()).Ping());
}

const int GrainCountQuaternary = 450;

RequestContext.Set(IPlacementDirector.PlacementHintKey, quaternary);
for (var i = 0; i < GrainCountQuaternary; i++)
{
    tasks.Add(grainFactory.GetGrain<ITestGrain>(Guid.NewGuid()).Ping());
}

await Task.WhenAll(tasks);

Console.WriteLine($"Activated {GrainCountPrimary} test grains on primary");
Console.WriteLine($"Activated {GrainCountSecondary} test grains on seconday");
Console.WriteLine($"Activated {GrainCountTertiary} test grains on tertiary");
Console.WriteLine($"Activated {GrainCountQuaternary} test grains on quaternary");
Console.WriteLine("---------------------------");

await Task.Delay(1000 * HostBuilderEx.RebalancerDueTimeSeconds);

while (true)
{
    await Task.Delay(1000 * (HostBuilderEx.SessionCyclePeriodSeconds + 3));
    var createMore = Random.Shared.Next(0, 4); // 25% chance

    if (createMore == 1)
    {
        var randomGrainsPrimary = 100;   //Random.Shared.Next(10, 51);
        var randomGrainsSecondary = 5;   //Random.Shared.Next(10, 51);
        var randomGrainsTertiary = 25;   //Random.Shared.Next(10, 51);
        var randomGrainsQuaternary = 45; //Random.Shared.Next(10, 51);

        tasks.Clear();

        RequestContext.Set(IPlacementDirector.PlacementHintKey, primary);
        for (var i = 0; i < randomGrainsPrimary; i++)
        {
            tasks.Add(grainFactory.GetGrain<ITestGrain>(Guid.NewGuid()).Ping());
        }

        RequestContext.Set(IPlacementDirector.PlacementHintKey, seconday);
        for (var i = 0; i < randomGrainsSecondary; i++)
        {
            tasks.Add(grainFactory.GetGrain<ITestGrain>(Guid.NewGuid()).Ping());
        }

        RequestContext.Set(IPlacementDirector.PlacementHintKey, tertiary);
        for (var i = 0; i < randomGrainsTertiary; i++)
        {
            tasks.Add(grainFactory.GetGrain<ITestGrain>(Guid.NewGuid()).Ping());
        }

        RequestContext.Set(IPlacementDirector.PlacementHintKey, quaternary);
        for (var i = 0; i < randomGrainsQuaternary; i++)
        {
            tasks.Add(grainFactory.GetGrain<ITestGrain>(Guid.NewGuid()).Ping());
        }

        await Task.WhenAll(tasks);

        Console.WriteLine($"Activated an extra {randomGrainsPrimary} grains on primary");
        Console.WriteLine($"Activated an extra {randomGrainsSecondary} grains on secondary");
        Console.WriteLine($"Activated an extra {randomGrainsTertiary} grains on tertiary");
        Console.WriteLine($"Activated an extra {randomGrainsQuaternary} grains on quaternary");
        Console.WriteLine("---------------------------");
    }

    var stats = await mgmtGrain.GetDetailedGrainStatistics();

    Console.WriteLine("Grain count on primary: " + stats.Where(x => x.SiloAddress == primary).Count());
    Console.WriteLine("Grain count on seconday: " + stats.Where(x => x.SiloAddress == seconday).Count());
    Console.WriteLine("Grain count on tertiary: " + stats.Where(x => x.SiloAddress == tertiary).Count());
    Console.WriteLine("Grain count on quaternary: " + stats.Where(x => x.SiloAddress == quaternary).Count());
    Console.WriteLine("---------------------------");
}
