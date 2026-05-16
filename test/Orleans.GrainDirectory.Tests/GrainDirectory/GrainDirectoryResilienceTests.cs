#nullable enable
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Runtime.Diagnostics;
using Orleans.Runtime.GrainDirectory;
using Orleans.Serialization;
using Orleans.Storage;
using Orleans.TestingHost;
using Orleans.TestingHost.Diagnostics;
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

[TestCategory("Stress"), TestCategory("Directory")]
public sealed class GrainDirectoryResilienceTests
{
    private static readonly TimeSpan DirectoryMigrationTimeout = TimeSpan.FromMinutes(1);

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

                        await CheckIntegrityAsync(testCluster, client);

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

    [Fact]
    public async Task JoiningSilo_DoesNotLeaveStaleEntriesOnPreviousOwner()
    {
        using var directoryEvents = new DiagnosticEventCollector(GrainDirectoryEvents.ListenerName);
        var testClusterBuilder = new TestClusterBuilder(1);
        testClusterBuilder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
        var testCluster = testClusterBuilder.Build();
        await testCluster.DeployAsync();
        var log = testCluster.ServiceProvider.GetRequiredService<ILogger<GrainDirectoryResilienceTests>>();
        var client = ((InProcessSiloHandle)testCluster.Primary).SiloHost.Services.GetRequiredService<IGrainFactory>();
        var previousDirectoryView = await WaitForDirectoryViewAsync(
            ((InProcessSiloHandle)testCluster.Primary).ServiceProvider.GetRequiredService<DirectoryMembershipService>(),
            view => view.Members.Contains(testCluster.Primary.SiloAddress),
            "initial directory membership view");
        const int CallsPerIteration = 100;
        var nextGrainId = 0L;

        try
        {
            for (var i = 0; i < 10; i++)
            {
                await RunPingBatchAsync(client, log, nextGrainId, CallsPerIteration);
                nextGrainId += CallsPerIteration;
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            var loadGrainId = nextGrainId;
            var loadTask = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    await RunPingBatchAsync(client, log, loadGrainId, CallsPerIteration);
                    loadGrainId += CallsPerIteration;
                }
            });

            try
            {
                log.LogInformation("Starting new silo.");
                var newSilo = await testCluster.StartAdditionalSiloAsync();
                log.LogInformation("Started '{Silo}'.", newSilo.SiloAddress);

                var currentDirectoryView = await WaitForDirectoryViewAsync(
                    ((InProcessSiloHandle)newSilo).ServiceProvider.GetRequiredService<DirectoryMembershipService>(),
                    view => view.Members.Contains(newSilo.SiloAddress),
                    $"directory membership view containing '{newSilo.SiloAddress}'");
                await WaitForDirectoryMigrationAsync(directoryEvents, previousDirectoryView, currentDirectoryView);
                await CheckIntegrityAsync(testCluster, client);
            }
            finally
            {
                cts.Cancel();
                await loadTask;
            }
        }
        finally
        {
            await testCluster.StopAllSilosAsync();
            await testCluster.DisposeAsync();
        }
    }

    private static async Task CheckIntegrityAsync(TestCluster testCluster, IGrainFactory client)
    {
        var integrityChecks = new List<Task>();
        var internalGrainFactory = (IInternalGrainFactory)client;
        foreach (var silo in testCluster.Silos)
        {
            var address = silo.SiloAddress;
            var partitionsPerSilo = ((InProcessSiloHandle)silo).ServiceProvider.GetRequiredService<DirectoryMembershipService>().PartitionsPerSilo;
            for (var partitionIndex = 0; partitionIndex < partitionsPerSilo; partitionIndex++)
            {
                var replica = internalGrainFactory.GetSystemTarget<IGrainDirectoryTestHooks>(GrainDirectoryPartition.CreateGrainId(address, partitionIndex).GrainId);
                integrityChecks.Add(replica.CheckIntegrityAsync().AsTask());
            }
        }

        await Task.WhenAll(integrityChecks);
    }

    private static async Task<DirectoryMembershipSnapshot> WaitForDirectoryViewAsync(
        DirectoryMembershipService directoryMembershipService,
        Func<DirectoryMembershipSnapshot, bool> predicate,
        string description)
    {
        using var cts = new CancellationTokenSource(DirectoryMigrationTimeout);
        try
        {
            await foreach (var view in directoryMembershipService.ViewUpdates.WithCancellation(cts.Token))
            {
                if (predicate(view))
                {
                    return view;
                }
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out waiting for {description} after {DirectoryMigrationTimeout}.");
        }

        throw new TimeoutException($"Timed out waiting for {description} after {DirectoryMigrationTimeout}.");
    }

    private static async Task WaitForDirectoryMigrationAsync(
        DiagnosticEventCollector directoryEvents,
        DirectoryMembershipSnapshot previousView,
        DirectoryMembershipSnapshot currentView)
    {
        var expectedOperations = GetExpectedRangeOperations(previousView, currentView).ToArray();
        Assert.NotEmpty(expectedOperations);

        await Task.WhenAll(expectedOperations.Select(operation => WaitForRangeOperationCompletedAsync(directoryEvents, operation)));
    }

    private static IEnumerable<ExpectedRangeOperation> GetExpectedRangeOperations(
        DirectoryMembershipSnapshot previousView,
        DirectoryMembershipSnapshot currentView)
    {
        var partitionCount = Math.Max(previousView.PartitionCount, currentView.PartitionCount);
        foreach (var member in previousView.Members.Concat(currentView.Members).Distinct())
        {
            for (var partitionIndex = 0; partitionIndex < partitionCount; partitionIndex++)
            {
                var previousRange = previousView.GetRange(member, partitionIndex);
                var currentRange = currentView.GetRange(member, partitionIndex);
                foreach (var removedRange in previousRange.Difference(currentRange))
                {
                    if (!removedRange.IsEmpty)
                    {
                        yield return new(
                            member,
                            partitionIndex,
                            currentView.Version,
                            removedRange,
                            GrainDirectoryEvents.ReleaseOperationName);
                    }
                }

                foreach (var addedRange in currentRange.Difference(previousRange))
                {
                    if (!addedRange.IsEmpty)
                    {
                        yield return new(
                            member,
                            partitionIndex,
                            currentView.Version,
                            addedRange,
                            GrainDirectoryEvents.AcquireOperationName);
                    }
                }
            }
        }
    }

    private static async Task WaitForRangeOperationCompletedAsync(
        DiagnosticEventCollector directoryEvents,
        ExpectedRangeOperation expectedOperation)
    {
        await directoryEvents.WaitForEventAsync(
            nameof(GrainDirectoryEvents.RangeOperationCompleted),
            evt => evt.Payload is GrainDirectoryEvents.RangeOperationCompleted completed
                && !completed.Canceled
                && completed.SiloAddress.Equals(expectedOperation.SiloAddress)
                && completed.PartitionIndex == expectedOperation.PartitionIndex
                && completed.Version == expectedOperation.Version
                && completed.Range.Equals(expectedOperation.Range)
                && string.Equals(completed.OperationName, expectedOperation.OperationName, StringComparison.Ordinal),
            DirectoryMigrationTimeout);
    }

    private static async Task RunPingBatchAsync(IGrainFactory client, ILogger log, long idBase, int callsPerIteration)
    {
        var tasks = Enumerable.Range(0, callsPerIteration).Select(i => client.GetGrain<IMyDirectoryTestGrain>(idBase + i).Ping().AsTask()).ToList();
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

    private readonly record struct ExpectedRangeOperation(
        SiloAddress SiloAddress,
        int PartitionIndex,
        MembershipVersion Version,
        RingRange Range,
        string OperationName);
}

