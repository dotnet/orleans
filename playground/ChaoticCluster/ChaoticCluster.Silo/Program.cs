using System.Diagnostics;
using ChaoticCluster.Silo;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.TestingHost;

var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults(); // Configure OTel
using var app = builder.Build();
await app.StartAsync();

var testClusterBuilder = new InProcessTestClusterBuilder(1);
testClusterBuilder.ConfigureSilo((options, siloBuilder) => new SiloBuilderConfigurator().Configure(siloBuilder));
testClusterBuilder.ConfigureSiloHost((options, hostBuilder) =>
{
    foreach (var provider in app.Services.GetServices<ILoggerProvider>())
    {
        hostBuilder.Logging.AddProvider(provider);
    }
});

testClusterBuilder.ConfigureClientHost(hostBuilder =>
{
    foreach (var provider in app.Services.GetServices<ILoggerProvider>())
    {
        hostBuilder.Logging.AddProvider(provider);
    }
});

var testCluster = testClusterBuilder.Build();
await testCluster.DeployAsync();
var log = testCluster.Client.ServiceProvider.GetRequiredService<ILogger<Program>>();
log.LogInformation($"ServiceId: {testCluster.Options.ServiceId}");
log.LogInformation($"ClusterId: {testCluster.Options.ClusterId}");

var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
var reconfigurationTimer = Stopwatch.StartNew();
var upperLimit = 10;
var lowerLimit = 1; // Membership is kept on the primary, so we can't go below 1
var target = upperLimit;
var idBase = 0L;
var client = testCluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>();
const int CallsPerIteration = 100;
const int MaxGrains = 524_288; // 2**19;

var loadTask = Task.Run(async () =>
{
    while (!cts.IsCancellationRequested)
    {
        var time = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, CallsPerIteration).Select(i => client.GetGrain<IMyTestGrain>((idBase + i) % MaxGrains).Ping().AsTask()).ToList();
        var workTask = Task.WhenAll(tasks);
        using var delayCancellation = new CancellationTokenSource();
        var delay = TimeSpan.FromMilliseconds(90_000);
        var delayTask = Task.Delay(delay, delayCancellation.Token);
        await Task.WhenAny(workTask, delayTask);

        try
        {
            await workTask;
        }
        catch (SiloUnavailableException sue)
        {
            log.LogInformation(sue, "Swallowed transient exception.");
        }
        catch (OrleansMessageRejectionException omre)
        {
           log.LogInformation(omre, "Swallowed rejection.");
        }
        catch (Exception exception)
        {
            log.LogError(exception, "Unhandled exception.");
            throw;
        }

        delayCancellation.Cancel();
        idBase += CallsPerIteration;
    }
});

var chaosTask = Task.Run(async () =>
{
    var clusterOperation = Task.CompletedTask;
    while (!cts.IsCancellationRequested)
    {
        try
        {
            var remaining = TimeSpan.FromSeconds(10) - reconfigurationTimer.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                reconfigurationTimer.Restart();
                await clusterOperation;

                clusterOperation = Task.Run(async () =>
                {
                    var currentCount = testCluster.Silos.Count;

                    if (currentCount > target)
                    {
                        // Stop or kill a random silo, but not the primary (since that hosts cluster membership)
                        var victim = testCluster.Silos[Random.Shared.Next(1, testCluster.Silos.Count - 1)];
                        if (currentCount % 2 == 0)
                        {
                            log.LogInformation($"Stopping '{victim.SiloAddress}'.");
                            await testCluster.StopSiloAsync(victim);
                            log.LogInformation($"Stopped '{victim.SiloAddress}'.");
                        }
                        else
                        {
                            log.LogInformation($"Killing '{victim.SiloAddress}'.");
                            await testCluster.KillSiloAsync(victim);
                            log.LogInformation($"Killed '{victim.SiloAddress}'.");
                        }
                    }
                    else if (currentCount < target)
                    {
                        log.LogInformation("Starting new silo.");
                        var result = await testCluster.StartAdditionalSiloAsync();
                        log.LogInformation($"Started '{result.SiloAddress}'.");
                    }

                    if (currentCount <= lowerLimit)
                    {
                        target = upperLimit;
                    }
                    else if (currentCount >= upperLimit)
                    {
                        target = lowerLimit;
                    }
                });
            }
            else
            {
                await Task.Delay(remaining);
            }
        }
        catch (Exception exception)
        {
            log.LogInformation(exception, "Ignoring chaos exception.");
        }
    }
});

await await Task.WhenAny(loadTask, chaosTask);
cts.Cancel();
await Task.WhenAll(loadTask, chaosTask);
await testCluster.StopAllSilosAsync();
await testCluster.DisposeAsync();

await app.StopAsync();