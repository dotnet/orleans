#nullable enable
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
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
    ValueTask Ping(CancellationToken cancellationToken = default);
}


[CollectionAgeLimit(Minutes = 1.01)]
internal class MyDirectoryTestGrain : Grain, IMyDirectoryTestGrain
{
    public ValueTask Ping(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}

[TestCategory("Stress"), TestCategory("Directory")]
[TestSuite("Stress")]
[TestProvider("None")]
[TestArea("GrainDirectory")]
public sealed class GrainDirectoryResilienceTests
{
    private static readonly TimeSpan DirectoryMigrationTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ChaosScenarioTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Cluster chaos test: tests directory functionality & integrity while starting/stopping/killing silos frequently.
    /// </summary>
    /// <returns></returns>
    [Fact]
    public async Task ElasticChaos()
    {
        var runnerCancellationToken = TestContext.Current.CancellationToken;
        using var deadline = new CancellationTokenSource(ChaosScenarioTimeout);
        using var scenario = CancellationTokenSource.CreateLinkedTokenSource(runnerCancellationToken, deadline.Token);
        var cancellationToken = scenario.Token;
        using var monitor = new DirectoryChaosMonitor();
        var testClusterBuilder = new TestClusterBuilder(1);
        testClusterBuilder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
        var testCluster = testClusterBuilder.Build();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var deploymentTask = Task.CompletedTask;
        var loadTask = Task.CompletedTask;
        var chaosTask = Task.CompletedTask;
        var failures = new List<Exception>();
        var phase = "deploying the initial silo";
        try
        {
            deploymentTask = testCluster.DeployAsync(cancellationToken);
            await deploymentTask.WaitAsync(cancellationToken);
            foreach (var silo in testCluster.Silos)
            {
                monitor.TrackSilo(silo.SiloAddress);
            }

            var log = testCluster.ServiceProvider.GetRequiredService<ILogger<GrainDirectoryResilienceTests>>();
            log.LogInformation("ServiceId: '{ServiceId}', ClusterId: '{ClusterId}'.",
                testCluster.Options.ServiceId, testCluster.Options.ClusterId);
            var client = ((InProcessSiloHandle)testCluster.Primary!).SiloHost.Services.GetRequiredService<IGrainFactory>();
            await EnsureDirectoryStableAsync(testCluster, client, cancellationToken).WaitAsync(cancellationToken);
            phase = "running the workload and topology changes";
            loadTask = RunChaosWorkloadAsync(client, monitor, cts.Token);
            chaosTask = RunChaosTopologyAsync(testCluster, client, log, monitor, cts.Token);

            var completed = await Task.WhenAny(loadTask, chaosTask, monitor.InvariantFailure).WaitAsync(cancellationToken);
            if (completed == monitor.InvariantFailure)
            {
                throw await monitor.InvariantFailure;
            }

            await completed;
            cancellationToken.ThrowIfCancellationRequested();
            if (completed != chaosTask)
            {
                throw new InvalidOperationException("The workload stopped before the topology scenario completed.");
            }

            cts.Cancel();
            await loadTask.WaitAsync(cancellationToken);
            phase = "establishing final progress and directory integrity";
            monitor.RecordPhase(phase);
            using var finalProbe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            finalProbe.CancelAfter(DirectoryMigrationTimeout);
            await EnsureDirectoryStableAsync(testCluster, client, finalProbe.Token).WaitAsync(finalProbe.Token);
            await CreatePingBatch(client, -100, 100, finalProbe.Token).WaitAsync(finalProbe.Token);
            await CheckIntegrityAsync(testCluster, client, finalProbe.Token).WaitAsync(finalProbe.Token);
            log.LogInformation("Chaos completed: {SuccessfulBatches} successful batches, {ExpectedDisruptions} expected disruptions.",
                monitor.SuccessfulBatches, monitor.ExpectedDisruptions);
        }
        catch (OperationCanceledException exception) when (deadline.IsCancellationRequested && !runnerCancellationToken.IsCancellationRequested)
        {
            failures.Add(monitor.InvariantFailure.IsCompletedSuccessfully
                ? await monitor.InvariantFailure
                : monitor.RuntimeFailure(phase, new TimeoutException(
                    $"ElasticChaos exceeded its {ChaosScenarioTimeout} scenario deadline during {phase}.", exception)));
        }
        catch (Exception exception)
        {
            failures.Add(monitor.InvariantFailure.IsCompletedSuccessfully
                ? await monitor.InvariantFailure
                : exception is DirectoryChaosFailure || exception is OperationCanceledException && cancellationToken.IsCancellationRequested
                    ? exception
                    : monitor.RuntimeFailure(phase, exception));
        }
        finally
        {
            await CaptureCleanupFailureAsync("canceling scenario workers", async _ => await cts.CancelAsync());
            await CaptureCleanupFailureAsync("joining scenario workers", async token =>
            {
                var workers = Task.WhenAll(deploymentTask, loadTask, chaosTask);
                try
                {
                    await workers.WaitAsync(token);
                }
                catch (Exception exception) when (!token.IsCancellationRequested
                    && DirectoryChaosMonitor.IsExpectedShutdown(workers.Exception ?? exception))
                {
                }
            });
            await CaptureCleanupFailureAsync("stopping the cluster", token => testCluster.StopAllSilosAsync(token));
            await CaptureCleanupFailureAsync("disposing the cluster", token => testCluster.DisposeAsync().AsTask().WaitAsync(token));
        }

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        if (failures.Count > 1)
        {
            throw new AggregateException("ElasticChaos encountered scenario and/or cleanup failures.", failures);
        }

        async Task CaptureCleanupFailureAsync(string cleanupPhase, Func<CancellationToken, Task> cleanup)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            try
            {
                await cleanup(timeout.Token).WaitAsync(timeout.Token);
            }
            catch (Exception exception)
            {
                if (!failures.Contains(exception))
                {
                    failures.Add(exception is DirectoryChaosFailure ? exception : monitor.RuntimeFailure(cleanupPhase, exception));
                }
            }
        }
    }

    private static async Task RunChaosWorkloadAsync(
        IGrainFactory client,
        DirectoryChaosMonitor monitor,
        CancellationToken cancellationToken)
    {
        const int BatchSize = 100;
        for (var idBase = 0L; !cancellationToken.IsCancellationRequested; idBase += BatchSize)
        {
            var batch = CreatePingBatch(client, idBase, BatchSize, cancellationToken);
            try
            {
                await batch;
                monitor.RecordSuccessfulBatch();
            }
            catch (Exception exception) when (cancellationToken.IsCancellationRequested
                && DirectoryChaosMonitor.IsExpectedShutdown(batch.Exception ?? exception))
            {
                break;
            }
            catch (Exception exception) when (DirectoryChaosMonitor.IsExpectedDisruption(batch.Exception ?? exception))
            {
                monitor.RecordExpectedDisruption(batch.Exception ?? exception);
            }
            catch (Exception exception)
            {
                throw monitor.RuntimeFailure($"workload batch starting at grain {idBase}", batch.Exception ?? exception);
            }
        }
    }

    private static async Task RunChaosTopologyAsync(
        TestCluster cluster,
        IGrainFactory client,
        ILogger log,
        DirectoryChaosMonitor monitor,
        CancellationToken cancellationToken)
    {
        const int Seed = 10969;
        const int UpperLimit = 10;
        const int LowerLimit = 1;
        var random = new Random(Seed);
        var target = UpperLimit;
        var duration = Stopwatch.StartNew();
        using var pacing = new PeriodicTimer(TimeSpan.FromSeconds(10));
        log.LogInformation("Chaos topology seed: {Seed}.", Seed);
        while (duration.Elapsed < TimeSpan.FromMinutes(5) && await pacing.WaitForNextTickAsync(cancellationToken))
        {
            var phase = "selecting the next topology change";
            try
            {
                var count = cluster.Silos.Count;
                if (count == UpperLimit)
                {
                    target = LowerLimit;
                }
                else if (count == LowerLimit)
                {
                    target = UpperLimit;
                }

                if (count < target)
                {
                    phase = $"starting silo with {count} existing silos";
                    monitor.RecordPhase(phase);
                    var started = Assert.Single(await cluster.StartAdditionalSilosAsync(
                        1, startAdditionalSiloOnNewPort: false, cancellationToken));
                    monitor.TrackSilo(started.SiloAddress);
                    log.LogInformation("Started '{Silo}'.", started.SiloAddress);
                }
                else
                {
                    var victim = cluster.SecondarySilos[random.Next(cluster.SecondarySilos.Count)];
                    phase = $"{(count % 2 == 0 ? "stopping" : "killing")} silo {victim.SiloAddress}";
                    monitor.RecordPhase(phase);
                    if (count % 2 == 0)
                    {
                        await cluster.StopSiloAsync(victim, cancellationToken);
                    }
                    else
                    {
                        await cluster.KillSiloAsync(victim, cancellationToken);
                    }
                }

                phase = $"converging and checking integrity after {phase}";
                monitor.RecordPhase(phase);
                await EnsureDirectoryStableAsync(cluster, client, cancellationToken);
                using var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                probe.CancelAfter(DirectoryMigrationTimeout);
                await CheckIntegrityAsync(cluster, client, probe.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw monitor.InvariantFailure.IsCompletedSuccessfully
                    ? await monitor.InvariantFailure
                    : monitor.RuntimeFailure(phase, exception);
            }
        }
    }

    private static async Task EnsureDirectoryStableAsync(
        TestCluster cluster,
        IGrainFactory grainFactory,
        CancellationToken cancellationToken)
    {
        var silos = cluster.Silos.Cast<InProcessSiloHandle>().ToArray();
        var expected = silos.Select(silo => silo.SiloAddress).ToHashSet();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DirectoryMigrationTimeout);
        var tasks = silos.Select(async silo =>
        {
            var membership = silo.ServiceProvider.GetRequiredService<DirectoryMembershipService>();
            var view = await WaitForDirectoryViewAsync(
                membership,
                candidate => candidate.Members.Length == expected.Count && candidate.Members.All(expected.Contains),
                $"directory membership on {silo.SiloAddress} with active silos [{string.Join(", ", expected)}]",
                timeout.Token);
            var waits = Enumerable.Range(0, view.PartitionCount).Select(index =>
                ((IInternalGrainFactory)grainFactory).GetSystemTarget<IGrainDirectoryTestHooks>(
                    GrainDirectoryPartition.CreateGrainId(silo.SiloAddress, index).GrainId)
                    .WaitForMembershipVersionAsync(view.Version, timeout.Token).AsTask()).ToArray();
            await Task.WhenAll(waits).WaitAsync(timeout.Token);
        }).ToArray();
        try
        {
            await Task.WhenAll(tasks).WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Directory failed to converge for [{string.Join(", ", expected)}] within {DirectoryMigrationTimeout}. "
                + string.Join("; ", silos.Select(silo =>
                    $"{silo.SiloAddress}: view {silo.ServiceProvider.GetRequiredService<DirectoryMembershipService>().CurrentView.Version}")),
                exception);
        }
    }

    private static Task CreatePingBatch(IGrainFactory client, long idBase, int count, CancellationToken cancellationToken) =>
        Task.WhenAll(Enumerable.Range(0, count)
            .Select(i => client.GetGrain<IMyDirectoryTestGrain>(idBase + i).Ping(cancellationToken).AsTask()));

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
        var workTask = CreatePingBatch(client, idBase, callsPerIteration, cancellationToken);

        try
        {
            await workTask.WaitAsync(cancellationToken);
        }
        catch (Exception exception) when (DirectoryChaosMonitor.IsExpectedDisruption(workTask.Exception ?? exception))
        {
            log.LogInformation(exception, "Expected transient directory-workload disruption.");
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
