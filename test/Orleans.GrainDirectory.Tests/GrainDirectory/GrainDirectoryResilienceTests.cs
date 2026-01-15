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
[TestSuite("Stress")]
[TestProvider("None")]
[TestArea("GrainDirectory")]
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
        var cancellationToken = TestContext.Current.CancellationToken;
        var testClusterBuilder = new TestClusterBuilder(1);
        testClusterBuilder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
        var testCluster = testClusterBuilder.Build();
        await testCluster.DeployAsync(cancellationToken);
        var log = testCluster.ServiceProvider.GetRequiredService<ILogger<GrainDirectoryResilienceTests>>();
        log.LogInformation("ServiceId: '{ServiceId}'", testCluster.Options.ServiceId);
        log.LogInformation("ClusterId: '{ClusterId}'.", testCluster.Options.ClusterId);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(5));
        var reconfigurationTimer = CoarseStopwatch.StartNew();
        var upperLimit = 10;
        var lowerLimit = 1; // Membership is kept on the primary, so we can't go below 1
        var target = upperLimit;
        var idBase = 0L;
        var client = ((InProcessSiloHandle)testCluster.Primary!).SiloHost.Services.GetRequiredService<IGrainFactory>();
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
                    await workTask.WaitAsync(cts.Token);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    break;
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
        }, cts.Token);

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
                        await clusterOperation.WaitAsync(cts.Token);

                        await CheckIntegrityAsync(testCluster, client, cts.Token);

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
                                    await testCluster.StopSiloAsync(victim, cts.Token);
                                    log.LogInformation("Stopped '{Silo}'.", victim.SiloAddress);
                                }
                                else
                                {
                                    log.LogInformation("Killing '{Silo}'.", victim.SiloAddress);
                                    await testCluster.KillSiloAsync(victim).WaitAsync(cts.Token);
                                    log.LogInformation("Killed '{Silo}'.", victim.SiloAddress);
                                }
                            }
                            else if (currentCount < target)
                            {
                                log.LogInformation("Starting new silo.");
                                var result = await testCluster.StartAdditionalSiloAsync().WaitAsync(cts.Token);
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
                        }, cts.Token);
                    }
                    else
                    {
                        await Task.Delay(remaining, cts.Token);
                    }
                }
                catch (Exception exception)
                {
                    log.LogInformation(exception, "Ignoring chaos exception.");
                }
            }
        }, cts.Token);

        try
        {
            await await Task.WhenAny(loadTask, chaosTask).WaitAsync(cancellationToken);
        }
        finally
        {
            cts.Cancel();
            using var joinCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            try
            {
                await Task.WhenAll(loadTask, chaosTask).WaitAsync(joinCancellation.Token);
            }
            catch (OperationCanceledException) when (
                cts.IsCancellationRequested
                && !joinCancellation.IsCancellationRequested)
            {
            }

            try
            {
                using var stopCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(1));
                await testCluster.StopAllSilosAsync(stopCancellation.Token);
            }
            finally
            {
                using var disposeCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(1));
                await testCluster.DisposeAsync().AsTask().WaitAsync(disposeCancellation.Token);
            }
        }
    }

    [Fact]
    public async Task JoiningSilo_DoesNotLeaveStaleEntriesOnPreviousOwner()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directoryEvents = new DiagnosticEventCollector(GrainDirectoryEvents.ListenerName);
        var testClusterBuilder = new TestClusterBuilder(1);
        testClusterBuilder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
        var testCluster = testClusterBuilder.Build();
        await testCluster.DeployAsync(cancellationToken);
        var log = testCluster.ServiceProvider.GetRequiredService<ILogger<GrainDirectoryResilienceTests>>();
        var client = ((InProcessSiloHandle)testCluster.Primary!).SiloHost.Services.GetRequiredService<IGrainFactory>();
        var previousDirectoryView = await WaitForDirectoryViewAsync(
            ((InProcessSiloHandle)testCluster.Primary).ServiceProvider.GetRequiredService<DirectoryMembershipService>(),
            view => view.Members.Contains(testCluster.Primary.SiloAddress),
            "initial directory membership view",
            cancellationToken);
        const int CallsPerIteration = 100;
        var nextGrainId = 0L;

        try
        {
            for (var i = 0; i < 10; i++)
            {
                await RunPingBatchAsync(client, log, nextGrainId, CallsPerIteration, cancellationToken);
                nextGrainId += CallsPerIteration;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMinutes(1));
            var loadGrainId = nextGrainId;
            var loadTask = Task.Run(async () =>
            {
                try
                {
                    while (!cts.IsCancellationRequested)
                    {
                        await RunPingBatchAsync(client, log, loadGrainId, CallsPerIteration, cts.Token);
                        loadGrainId += CallsPerIteration;
                    }
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                }
            }, cts.Token);

            try
            {
                log.LogInformation("Starting new silo.");
                var newSilo = await testCluster.StartAdditionalSiloAsync().WaitAsync(cancellationToken);
                log.LogInformation("Started '{Silo}'.", newSilo.SiloAddress);

                var currentDirectoryView = await WaitForDirectoryViewAsync(
                    ((InProcessSiloHandle)newSilo).ServiceProvider.GetRequiredService<DirectoryMembershipService>(),
                    view => view.Members.Contains(newSilo.SiloAddress),
                    $"directory membership view containing '{newSilo.SiloAddress}'",
                    cancellationToken);
                await WaitForDirectoryMigrationAsync(
                    directoryEvents,
                    previousDirectoryView,
                    currentDirectoryView,
                    cancellationToken);
                await CheckIntegrityAsync(testCluster, client, cancellationToken);
            }
            finally
            {
                cts.Cancel();
                using var joinCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(1));
                try
                {
                    await loadTask.WaitAsync(joinCancellation.Token);
                }
                catch (OperationCanceledException) when (
                    cts.IsCancellationRequested
                    && !joinCancellation.IsCancellationRequested)
                {
                }
            }
        }
        finally
        {
            try
            {
                using var stopCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(1));
                await testCluster.StopAllSilosAsync(stopCancellation.Token);
            }
            finally
            {
                using var disposeCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(1));
                await testCluster.DisposeAsync().AsTask().WaitAsync(disposeCancellation.Token);
            }
        }
    }

    private static async Task CheckIntegrityAsync(
        TestCluster testCluster,
        IGrainFactory client,
        CancellationToken cancellationToken)
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
                integrityChecks.Add(replica.CheckIntegrityAsync(cancellationToken).AsTask());
            }
        }

        await Task.WhenAll(integrityChecks).WaitAsync(cancellationToken);
    }

    private static async Task<DirectoryMembershipSnapshot> WaitForDirectoryViewAsync(
        DirectoryMembershipService directoryMembershipService,
        Func<DirectoryMembershipSnapshot, bool> predicate,
        string description,
        CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(DirectoryMigrationTimeout);
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
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && cts.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out waiting for {description} after {DirectoryMigrationTimeout}.");
        }

        throw new TimeoutException($"Timed out waiting for {description} after {DirectoryMigrationTimeout}.");
    }

    private static async Task WaitForDirectoryMigrationAsync(
        DiagnosticEventCollector directoryEvents,
        DirectoryMembershipSnapshot previousView,
        DirectoryMembershipSnapshot currentView,
        CancellationToken cancellationToken)
    {
        var expectedOperations = GetExpectedRangeOperations(previousView, currentView).ToArray();
        Assert.NotEmpty(expectedOperations);

        await Task.WhenAll(expectedOperations.Select(
            operation => WaitForRangeOperationCompletedAsync(directoryEvents, operation, cancellationToken)))
            .WaitAsync(cancellationToken);
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
        ExpectedRangeOperation expectedOperation,
        CancellationToken cancellationToken)
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
            DirectoryMigrationTimeout,
            cancellationToken);
    }

    private static async Task RunPingBatchAsync(
        IGrainFactory client,
        ILogger log,
        long idBase,
        int callsPerIteration,
        CancellationToken cancellationToken)
    {
        var tasks = Enumerable.Range(0, callsPerIteration).Select(i => client.GetGrain<IMyDirectoryTestGrain>(idBase + i).Ping().AsTask()).ToList();
        var workTask = Task.WhenAll(tasks);

        try
        {
            await workTask.WaitAsync(cancellationToken);
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
            siloBuilder.AddDistributedGrainDirectory();
        }
    }

    private readonly record struct ExpectedRangeOperation(
        SiloAddress SiloAddress,
        int PartitionIndex,
        MembershipVersion Version,
        RingRange Range,
        string OperationName);
}
