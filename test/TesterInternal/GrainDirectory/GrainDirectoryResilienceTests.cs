#nullable enable
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime.GrainDirectory;
using Orleans.TestingHost;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.GrainDirectory;

internal interface IMyDirectoryTestGrain : IGrainWithIntegerKey
{
    ValueTask Ping();
}

internal class MyDirectoryTestGrain : Grain, IMyDirectoryTestGrain
{
    public ValueTask Ping() => default;
}

[TestCategory("SlowBVT"), TestCategory("Directory")]
public sealed class GrainDirectoryResilienceTests(ITestOutputHelper output)
{
    /// <summary>
    /// Cluster chaos test: tests directory functionality & integrity while starting/stopping/killing silos frequently.
    /// </summary>
    /// <returns></returns>
    [Fact]
    public async Task ElasticClusterWorkload()
    {
        var testClusterBuilder = new TestClusterBuilder(1);
        testClusterBuilder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
        var testCluster = testClusterBuilder.Build();
        await testCluster.DeployAsync();
        output.WriteLine($"ServiceId: {testCluster.Options.ServiceId}");
        output.WriteLine($"ClusterId: {testCluster.Options.ClusterId}");

        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var reconfigurationTimer = CoarseStopwatch.StartNew();
        var upperLimit = 5;
        var lowerLimit = 1;
        var target = upperLimit;
        var clusterOperation = Task.CompletedTask;
        var idBase = 0L;
        var client = ((InProcessSiloHandle)testCluster.Primary).SiloHost.Services.GetRequiredService<IGrainFactory>();
        const int CallsPerIteration = 100;
        try
        {
            var loadTask = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    var time = Stopwatch.StartNew();
                    var workTask = Parallel.ForAsync(0, CallsPerIteration, (i, ct) => client.GetGrain<IMyDirectoryTestGrain>(idBase + i).Ping());
                    using var delayCancellation = new CancellationTokenSource();
                    var delayTask = Task.Delay(TimeSpan.FromMilliseconds(15_000), delayCancellation.Token);
                    await Task.WhenAny(workTask, delayTask);
                    if (delayTask.IsCompleted)
                    {
                        DumpCapture.CreateMiniDump("delayed");
                    }

                    Assert.False(delayTask.IsCompleted);

                    try
                    {
                        await workTask;
                    }
                    catch (SiloUnavailableException sue)
                    {
                        output.WriteLine($"Caught & swallowed transient exception: {sue}");
                    }
                    catch (Exception exception)
                    {
                        output.WriteLine($"Caught exception: {exception}");
                        DumpCapture.CreateMiniDump("unexpected");
                        throw;
                    }

                    idBase += CallsPerIteration;
                }
            });

            var chaosTask = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        var remaining = TimeSpan.FromSeconds(2) - reconfigurationTimer.Elapsed;
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
                                    var replica = ((IInternalGrainFactory)client).GetSystemTarget<IGrainDirectoryTestHooks>(GrainDirectoryReplica.CreateGrainId(address, partitionIndex).GrainId);
                                    RequestContext.Set("gid", replica.GetGrainId());
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
                                        await testCluster.StopSiloAsync(victim);
                                    }
                                    else
                                    {
                                        await testCluster.KillSiloAsync(victim);
                                    }
                                }
                                else if (currentCount < target)
                                {
                                    await testCluster.StartAdditionalSiloAsync();
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
                        output.WriteLine($"Ignoring chaos exception: {exception}");
                    }
                }
            });

            await await Task.WhenAny(loadTask, chaosTask);
            cts.Cancel();
            await Task.WhenAll(loadTask, chaosTask);
        }
        finally
        {
            await testCluster.StopAllSilosAsync();
            await testCluster.DisposeAsync();
        }
    }

    private class SiloBuilderConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
#pragma warning disable ORLEANSEXP002 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
            //siloBuilder.AddDistributedGrainDirectory();
#pragma warning restore ORLEANSEXP002 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
            siloBuilder.ConfigureLogging(l => l.AddFilter("Orleans.Runtime.Messaging.MessageCenter", LogLevel.Debug));
            //siloBuilder.ConfigureLogging(l => l.AddFilter("Orleans.Runtime.GrainDirectory.GrainDirectoryReplica", LogLevel.Trace));
            //siloBuilder.ConfigureLogging(l => l.AddFilter("Orleans.Runtime.GrainDirectory.DistributedGrainDirectory", LogLevel.Information));
        }
    }
}

