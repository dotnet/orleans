#nullable enable
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Runtime.GrainDirectory;
using Orleans.Serialization;
using Orleans.Storage;
using Orleans.TestingHost;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.GrainDirectory;

internal interface IMyDirectoryTestGrain : IGrainWithIntegerKey
{
    ValueTask Ping();
}

[CollectionAgeLimit(Minutes = 1.01)]
internal class MyDirectoryTestGrain : Grain, IMyDirectoryTestGrain
{
    public ValueTask Ping() => default;
}

[TestCategory("SlowBVT"), TestCategory("Directory")]
public sealed class GrainDirectoryResilienceTests
{
    /// <summary>
    /// Cluster chaos test: tests directory functionality & integrity while starting/stopping/killing silos frequently.
    /// </summary>
    /// <returns></returns>
    [Fact]
    public async Task ElasticChaos()
    {
        var testClusterBuilder = new TestClusterBuilder(1);
        testClusterBuilder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
        var testCluster = testClusterBuilder.Build();
        await testCluster.DeployAsync();
        var log = testCluster.ServiceProvider.GetRequiredService<ILogger<GrainDirectoryResilienceTests>>();
        log.LogInformation("ServiceId: '{ServiceId}'", testCluster.Options.ServiceId);
        log.LogInformation("ClusterId: '{ClusterId}'.", testCluster.Options.ClusterId);

        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var reconfigurationTimer = CoarseStopwatch.StartNew();
        var upperLimit = 10;
        var lowerLimit = 1; // Membership is kept on the primary, so we can't go below 1
        var target = upperLimit;
        var idBase = 0L;
        var client = ((InProcessSiloHandle)testCluster.Primary).SiloHost.Services.GetRequiredService<IGrainFactory>();
        const int CallsPerIteration = 100;
        var loadTask = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                var time = Stopwatch.StartNew();
                var tasks = Enumerable.Range(0, CallsPerIteration).Select(i => client.GetGrain<IMyDirectoryTestGrain>(idBase + i).Ping().AsTask()).ToList();
                var workTask = Task.WhenAll(tasks);
                
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

                        // Check integrity
                        var integrityChecks = new List<Task>();
                        foreach (var silo in testCluster.Silos)
                        {
                            var address = silo.SiloAddress;
                            for (var partitionIndex = 0; partitionIndex < DirectoryMembershipSnapshot.PartitionsPerSilo; partitionIndex++)
                            {
                                var replica = ((IInternalGrainFactory)client).GetSystemTarget<IGrainDirectoryTestHooks>(GrainDirectoryPartition.CreateGrainId(address, partitionIndex).GrainId);
                                integrityChecks.Add(replica.CheckIntegrityAsync().AsTask());
                            }
                        }

                        await Task.WhenAll(integrityChecks);
                        foreach (var task in integrityChecks)
                        {
                            await task;
                        }

                        clusterOperation = Task.Run(async () =>
                        {
                            var currentCount = testCluster.Silos.Count;

                            if (currentCount > target)
                            {
                                // Stop or kill a random silo, but not the primary (since that hosts cluster membership)
                                var victim = testCluster.SecondarySilos[Random.Shared.Next(testCluster.SecondarySilos.Count)];
                                if (currentCount % 2 == 0)
                                {
                                    log.LogInformation("Stopping '{Silo}'.", victim.SiloAddress);
                                    await testCluster.StopSiloAsync(victim);
                                    log.LogInformation("Stopped '{Silo}'.", victim.SiloAddress);
                                }
                                else
                                {
                                    log.LogInformation("Killing '{Silo}'.", victim.SiloAddress);
                                    await testCluster.KillSiloAsync(victim);
                                    log.LogInformation("Killed '{Silo}'.", victim.SiloAddress);
                                }
                            }
                            else if (currentCount < target)
                            {
                                log.LogInformation("Starting new silo.");
                                var result = await testCluster.StartAdditionalSiloAsync();
                                log.LogInformation("Started '{Silo}'.", result.SiloAddress);
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
    }

    private class SiloBuilderConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder.Configure<SiloMessagingOptions>(o => o.ResponseTimeout = o.SystemResponseTimeout = TimeSpan.FromMinutes(2));
#pragma warning disable ORLEANSEXP003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
            siloBuilder.AddDistributedGrainDirectory();
#pragma warning restore ORLEANSEXP003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        }
    }
}

