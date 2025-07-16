using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;

var builder = Host.CreateApplicationBuilder(args);
builder.AddKeyedRedisClient("orleans-redis");
builder.Logging.AddFilter("Orleans.Runtime.Placement.Rebalancing", LogLevel.Trace);
#pragma warning disable ORLEANSEXP002
builder.UseOrleans(builder => builder
    .Configure<GrainCollectionOptions>(o =>
    {
        o.CollectionQuantum = TimeSpan.FromSeconds(15);
    })
    .Configure<ResourceOptimizedPlacementOptions>(o =>
    {
        o.LocalSiloPreferenceMargin = 0;
    })
    .Configure<ActivationRebalancerOptions>(o =>
    {
        o.RebalancerDueTime = TimeSpan.FromSeconds(5);
        o.SessionCyclePeriod = TimeSpan.FromSeconds(5);
        // uncomment these below, if you want higher migration rate
        //o.CycleNumberWeight = 1;
        //o.SiloNumberWeight = 0; 
    })
    .AddActivationRebalancer());
#pragma warning restore ORLEANSEXP002

builder.Services.AddHostedService<LoadDriverBackgroundService>();
var app = builder.Build();

await app.RunAsync();

internal class LoadDriverBackgroundService(IGrainFactory client) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            for (var i = 0; i < 5 * Random.Shared.Next(1, 1000); i++)
            {
                await client.GetGrain<IRebalancingTestGrain>(Guid.NewGuid()).Ping();
            }

            await Task.Delay(Random.Shared.Next(500, 1_000), stoppingToken);
        }
    }
}

public interface IRebalancingTestGrain : IGrainWithGuidKey
{
    Task Ping();
}

[CollectionAgeLimit(Minutes = 0.5)]
public class RebalancingTestGrain : Grain, IRebalancingTestGrain
{
    public Task Ping() => Task.CompletedTask;
}
