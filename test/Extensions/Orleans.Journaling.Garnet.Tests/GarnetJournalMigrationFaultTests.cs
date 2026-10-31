using Orleans.Journaling.Ranges;
using Xunit.Sdk;

namespace Orleans.Journaling.Garnet.Tests;

/// <summary>
/// Moves buckets continuously while clients, including duplicate activations, read and write journals with replies
/// dropped at random, and kills partition processes at each step of a migration; then checks that every journal's
/// recorded history is linearizable, that no acknowledged write was lost, and that every migration completed.
/// </summary>
[TestSuite("SlowBVT")]
[TestProvider("None")]
[TestCategory("SlowBVT")]
public sealed class GarnetJournalMigrationFaultTests
{
    // Every wait is bounded, and fails with the state of the map and the partitions.
    private static readonly TimeSpan StepTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(90);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ContinuousMigration_WithDuplicateActivationsAndDroppedReplies_IsLinearizable()
    {
        const int BucketCount = 16;
        await using var partitions = await GarnetTestPartitions.StartAsync(3, BucketCount, owners: 2, CancellationToken);
        var workload = new JournalWorkload(new LinearizabilityHistory());
        var journals = Enumerable.Range(0, 8).Select(index => JournalId.Create("continuous", index.ToString())).ToArray();
        var random = new Random(17);
        GarnetJournalRouter CreateRouter(int seed)
        {
            var router = partitions.CreateRouter(TimeSpan.FromSeconds(20));
            var faults = new Random(seed);
            router.DropReply = _ => { lock (faults) { return faults.Next(10) == 0; } };
            return router;
        }

        using var stop = new CancellationTokenSource();
        var clients = journals
            .SelectMany((journal, index) => Enumerable.Range(0, 3).Select(client => Task.Run(() => workload.RunClientAsync(CreateRouter(index * 10 + client), journal, index * 10 + client, stop.Token))))
            .ToList();

        // Two coordinators drive every migration concurrently; a third commits moves.
        var coordinators = Enumerable.Range(0, 2).Select(index => new GarnetJournalMigrationCoordinator(CreateRouter(100 + index))).ToArray();
        var drivers = coordinators.Select(coordinator => Task.Run(() => DriveAsync(coordinator, stop.Token))).ToList();
        var mover = new GarnetJournalMigrationCoordinator(partitions.CreateRouter());

        // At most one partition holds a bucket open at any time.
        var violations = new List<string>();
        var monitor = Task.Run(async () =>
        {
            var observer = partitions.CreateRouter();
            while (!stop.IsCancellationRequested)
            {
                for (var bucket = 0; bucket < BucketCount; bucket++)
                {
                    // Partitions are read in turn; a partition seen open before and after another was seen open was
                    // open at the same time as it.
                    var states = new List<BucketState>();
                    foreach (var partition in partitions.Partitions)
                    {
                        states.Add(await observer.Migrator.GetBucketAsync(partition, bucket, CancellationToken));
                    }

                    var open = Enumerable.Range(0, states.Count).Where(index => states[index].State == "open").ToList();
                    if (open.Count > 1)
                    {
                        var again = await observer.Migrator.GetBucketAsync(partitions.Partitions[open[0]], bucket, CancellationToken);
                        if (again.State == "open" && again.Version == states[open[0]].Version)
                        {
                            violations.Add($"bucket {bucket}: {string.Join(", ", states.Select((state, index) => $"{partitions.Partitions[index]}={state}"))}; map {(await partitions.ReadViewAsync()).Version}");
                        }
                    }
                }
            }
        }, CancellationToken.None);
        var committed = 0;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(12);
        while (DateTime.UtcNow < deadline)
        {
            var buckets = Enumerable.Range(0, random.Next(1, 4)).Select(_ => random.Next(BucketCount)).ToArray();
            var result = await mover.TryMoveAsync(buckets, partitions.Partitions[random.Next(partitions.Partitions.Count)], CancellationToken);
            committed += result.Status == MoveStatus.Committed ? 1 : 0;
            await Task.Delay(random.Next(20, 120), CancellationToken);
        }

        stop.Cancel();
        await BoundedAsync(Task.WhenAll(clients.Concat(drivers)), "Clients and coordinators stopping", partitions);
        await CompleteMigrationsAsync(mover, partitions);
        await BoundedAsync(monitor, "The single-open monitor stopping", partitions);
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations.Take(20)));

        await AssertConvergedAsync(partitions, workload, journals, committed, minimumMoves: 10);
    }

    [Fact]
    public async Task PartitionKills_AtEveryMigrationStep_PreserveAcknowledgedWrites()
    {
        const int BucketCount = 4;
        await using var partitions = await GarnetTestPartitions.StartAsync(3, BucketCount, owners: 2, CancellationToken, useProcesses: true);
        var router = partitions.CreateRouter(TimeSpan.FromSeconds(30));
        var coordinator = new GarnetJournalMigrationCoordinator(router);

        // Enough journals in each bucket that consolidation and reclamation span several round trips.
        var preloaded = new Dictionary<JournalId, JournalState>();
        await BoundedAsync(
            Parallel.ForEachAsync(Enumerable.Range(0, 400), CancellationToken, async (index, token) =>
            {
                var id = JournalId.Create("preloaded", index.ToString());
                var storage = new GarnetJournalStorage(router, id, "json", compactionThresholdBytes: 0);
                await storage.AppendAsync(new System.Buffers.ReadOnlySequence<byte>(BitConverter.GetBytes(index)), token);
                lock (preloaded)
                {
                    preloaded[id] = JournalState.Absent.Append(index);
                }
            }),
            "Preloading journals",
            partitions);

        var workload = new JournalWorkload(new LinearizabilityHistory());
        var journals = Enumerable.Range(0, 6).Select(index => JournalId.Create("killed", index.ToString())).ToArray();
        using var stop = new CancellationTokenSource();
        var clients = journals
            .SelectMany((journal, index) => Enumerable.Range(0, 2).Select(client => Task.Run(() => workload.RunClientAsync(partitions.CreateRouter(TimeSpan.FromSeconds(30)), journal, index * 10 + client, stop.Token))))
            .ToList();

        var moves = 0;
        var random = new Random(23);
        var reached = new List<KillPoint>();
        foreach (var target in Enum.GetValues<KillPoint>())
        {
            for (var repeat = 0; repeat < 2; repeat++)
            {
                var view = (await partitions.ReadViewAsync()).Map;
                var bucket = random.Next(BucketCount);
                var source = view.GetOwner(bucket);
                var others = partitions.Partitions.Where(partition => partition != source).ToList();
                var destination = others[random.Next(others.Count)];
                var move = await coordinator.TryMoveAsync([bucket], destination, CancellationToken);
                Assert.Equal(MoveStatus.Committed, move.Status);
                moves++;
                await RunWithKillAsync(partitions, coordinator, router, bucket, source, destination, target, preloaded.Keys, random);
                reached.Add(target);
            }
        }

        // Each kill point was reached twice, with the operation it interrupts in flight.
        Assert.Equal(Enum.GetValues<KillPoint>().SelectMany(point => new[] { point, point }), reached);

        stop.Cancel();
        await BoundedAsync(Task.WhenAll(clients), "Clients stopping", partitions);
        await CompleteMigrationsAsync(coordinator, partitions);

        var reader = partitions.CreateRouter();
        foreach (var (id, expected) in preloaded)
        {
            Assert.Equal(expected, await BoundedAsync(JournalWorkload.ReadUnrecordedAsync(reader, id), $"Reading preloaded journal {id}", partitions));
        }

        await AssertConvergedAsync(partitions, workload, journals, moves, minimumMoves: 10);
    }

    private enum KillPoint
    {
        BeforeSeal,
        BeforeOpen,
        DuringPulls,
        DuringConsolidation,
        DuringReclamation,
    }

    /// <summary>
    /// Advances a migration to the kill point, kills a partition there, restarts it, and completes the migration. Kills
    /// during an operation hold the operation at a procedure boundary until the partition is dead, so the operation is
    /// in flight when the kill happens; the test fails if the operation never reaches that boundary.
    /// </summary>
    private static async Task RunWithKillAsync(
        GarnetTestPartitions partitions,
        GarnetJournalMigrationCoordinator coordinator,
        GarnetJournalRouter router,
        int bucket,
        PartitionId source,
        PartitionId destination,
        KillPoint point,
        IEnumerable<JournalId> preloaded,
        Random random)
    {
        async Task<MigrationStep?> LatestAsync() => new RangeMapBuckets((await partitions.ReadViewAsync()).Map).GetLatestMigration(bucket);

        async Task AdvanceToAsync(MigrationPhase phase)
        {
            var deadline = DateTime.UtcNow + StepTimeout;
            while ((await LatestAsync())?.Phase < phase)
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new XunitException(
                        $"The migration of bucket {bucket} from {source} to {destination} did not reach {phase} within {StepTimeout} ({point}). {await DescribeAsync(partitions)}");
                }

                await BoundedAsync(coordinator.AdvanceAsync(filter: null, CancellationToken), $"Advancing bucket {bucket} toward {phase} ({point})", partitions);
            }
        }

        async Task KillAndRestartAsync(PartitionId victim, Task background, SendGate? gate = null)
        {
            await partitions.KillHostAsync(victim);
            gate?.Release();

            // Steps which reach the killed partition retry until it restarts, at a new endpoint which its host publishes.
            var advance = coordinator.AdvanceAsync(filter: null, CancellationToken);
            await Task.Delay(random.Next(50, 200), CancellationToken);
            await partitions.RestartHostAsync(victim, CancellationToken);

            // Steps which overlapped the kill may fail only because the partition was unavailable.
            await BoundedAsync(
                Task.WhenAll(AcrossKillAsync(background), AcrossKillAsync(advance)),
                $"Migration steps overlapping the kill of {victim} ({point}, bucket {bucket})",
                partitions);
        }

        // Installs a gate which holds the first matching command at its send until released, then starts the operation,
        // and fails the test if the operation completes without sending one.
        async Task<(SendGate Gate, Task Operation)> HoldAsync(GarnetJournalRouter at, string command, Func<Task> start)
        {
            var gate = new SendGate(at, command);
            var operation = Task.Run(start);
            var first = await Task.WhenAny(gate.Reached, operation).WaitAsync(StepTimeout, CancellationToken);
            if (first != gate.Reached)
            {
                gate.Dispose();
                await operation;
                throw new XunitException($"{point} completed without sending {command} for bucket {bucket}. {await DescribeAsync(partitions)}");
            }

            return (gate, operation);
        }

        switch (point)
        {
            case KillPoint.BeforeSeal:
                Assert.Equal(MigrationPhase.Committed, (await LatestAsync())?.Phase);
                await KillAndRestartAsync(source, Task.CompletedTask);
                break;
            case KillPoint.BeforeOpen:
                {
                    // A handover continues to its open as soon as its seal is recorded, so the test seals and records
                    // the phase itself, stopping between them.
                    var latest = (await LatestAsync())!.Value;
                    Assert.Equal(MigrationPhase.Committed, latest.Phase);
                    Assert.True(await BoundedAsync(router.Migrator.TransitionAsync(source, bucket, GarnetJournalProtocol.BucketSealOperation, latest.StartedAtVersion, source: null, CancellationToken), $"Sealing bucket {bucket} ({point})", partitions));
                    await BoundedAsync(coordinator.RecordObservedAsync([bucket], CancellationToken), $"Recording the seal of bucket {bucket} ({point})", partitions);
                    Assert.Equal(MigrationPhase.Sealed, (await LatestAsync())?.Phase);
                }

                await KillAndRestartAsync(destination, Task.CompletedTask);
                break;
            case KillPoint.DuringPulls:
                {
                    // A client pulls the bucket's journals from the source, which is killed while a pull reads it.
                    await AdvanceToAsync(MigrationPhase.Opened);
                    var reader = partitions.CreateRouter(TimeSpan.FromSeconds(30));
                    var inBucket = preloaded.Where(id => GarnetJournalKeys.GetBucket(GarnetJournalKeys.EncodeJournalId(id.Value), partitions.BucketCount) == bucket).Take(40).ToList();
                    var (gate, pulls) = await HoldAsync(reader, GarnetJournalProtocol.PullRead, async () =>
                    {
                        foreach (var id in inBucket)
                        {
                            await JournalWorkload.ReadUnrecordedAsync(reader, id);
                        }
                    });
                    using var _ = gate;
                    await KillAndRestartAsync(source, Task.CompletedTask, gate);
                    await BoundedAsync(pulls, $"Pulls from {source} across its kill (bucket {bucket})", partitions);
                    break;
                }

            case KillPoint.DuringConsolidation:
                {
                    // Consolidation reads each journal from the source and installs it in the destination; either is
                    // killed while consolidation reads a journal.
                    await AdvanceToAsync(MigrationPhase.Opened);
                    var (gate, consolidation) = await HoldAsync(router, GarnetJournalProtocol.PullRead, () => coordinator.AdvanceAsync(filter: null, CancellationToken));
                    using var _ = gate;
                    await KillAndRestartAsync(random.Next(2) == 0 ? source : destination, consolidation, gate);
                    break;
                }

            case KillPoint.DuringReclamation:
                {
                    // The source is killed after it marked its copy reclaimed, as it purges the copy's journals.
                    await AdvanceToAsync(MigrationPhase.Complete);
                    var (gate, reclamation) = await HoldAsync(router, GarnetJournalProtocol.Purge, () => coordinator.AdvanceAsync(filter: null, CancellationToken));
                    using var _ = gate;
                    await KillAndRestartAsync(source, reclamation, gate);
                    break;
                }
        }

        await CompleteMigrationsAsync(coordinator, partitions);
    }

    /// <summary>Awaits a migration step which overlapped a kill, tolerating only failures caused by the killed partition's unavailability.</summary>
    private static async Task AcrossKillAsync(Task step)
    {
        try
        {
            await step;
        }
        catch (Exception exception) when (exception is TimeoutException || GarnetJournalRouter.IsTransient(exception))
        {
        }
    }

    /// <summary>Holds the first send of a command through a router until released.</summary>
    private sealed class SendGate : IDisposable
    {
        private readonly GarnetJournalRouter _router;
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed = 1;

        public SendGate(GarnetJournalRouter router, string command)
        {
            _router = router;
            router.BeforeSend = async (sent, cancellationToken) =>
            {
                if (sent == command && Interlocked.Exchange(ref _armed, 0) == 1)
                {
                    _reached.TrySetResult();
                    await _release.Task.WaitAsync(cancellationToken);
                }
            };
        }

        public Task Reached => _reached.Task;

        public void Release() => _release.TrySetResult();

        public void Dispose()
        {
            _router.BeforeSend = null;
            Release();
        }
    }

    private static async Task DriveAsync(GarnetJournalMigrationCoordinator coordinator, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (!await coordinator.AdvanceAsync(filter: null, stop))
                {
                    await Task.Delay(5, stop);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private static async Task CompleteMigrationsAsync(GarnetJournalMigrationCoordinator coordinator, GarnetTestPartitions partitions)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        timeout.CancelAfter(CompletionTimeout);
        while (true)
        {
            try
            {
                await coordinator.CompleteAllAsync(timeout.Token);
                return;
            }
            catch (TimeoutException) when (!timeout.IsCancellationRequested)
            {
                // A partition was unavailable for a whole operation timeout; continue from the recorded phases.
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !CancellationToken.IsCancellationRequested)
            {
                throw new XunitException($"Migrations did not complete within {CompletionTimeout}. {await DescribeAsync(partitions)}");
            }
        }
    }

    /// <summary>Awaits <paramref name="task"/>, failing with the state of the map and the partitions if it takes longer than <see cref="StepTimeout"/>.</summary>
    private static async Task BoundedAsync(Task task, string what, GarnetTestPartitions partitions)
    {
        try
        {
            await task.WaitAsync(StepTimeout, CancellationToken);
        }
        catch (TimeoutException exception) when (!task.IsCompleted)
        {
            throw new XunitException($"{what} did not complete within {StepTimeout}. {await DescribeAsync(partitions)}", exception);
        }
    }

    /// <inheritdoc cref="BoundedAsync(Task, string, GarnetTestPartitions)"/>
    private static async Task<T> BoundedAsync<T>(Task<T> task, string what, GarnetTestPartitions partitions)
    {
        await BoundedAsync((Task)task, what, partitions);
        return await task;
    }

    /// <summary>Describes the range map's migrations and every partition's bucket states, for failure messages.</summary>
    private static async Task<string> DescribeAsync(GarnetTestPartitions partitions)
    {
        try
        {
            var view = await partitions.ReadViewAsync();
            var observer = partitions.CreateRouter(TimeSpan.FromSeconds(2));
            var states = new List<string>();
            for (var bucket = 0; bucket < partitions.BucketCount; bucket++)
            {
                foreach (var partition in partitions.Partitions)
                {
                    var state = await observer.Migrator.TryGetBucketAsync(partition, bucket, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
                    states.Add($"{partition}/{bucket}={state?.ToString() ?? "unavailable"}");
                }
            }

            return $"Map version {view.Version}: owners [{string.Join(", ", Enumerable.Range(0, partitions.BucketCount).Select(bucket => $"{bucket}->{view.Map.GetOwner(bucket)}"))}]; "
                + $"migrations [{string.Join("; ", view.Map.Migrations)}]; buckets [{string.Join("; ", states)}].";
        }
        catch (Exception exception)
        {
            return $"(Describing the partitions failed: {exception.Message})";
        }
    }

    private static async Task AssertConvergedAsync(GarnetTestPartitions partitions, JournalWorkload workload, JournalId[] journals, int moves, int minimumMoves)
    {
        // Each client's last write is visible to a final read, which must succeed so the check covers the final state.
        var router = partitions.CreateRouter();
        foreach (var journal in journals)
        {
            var state = await BoundedAsync(
                workload.ReadAsync(new GarnetJournalStorage(router, journal, "json", compactionThresholdBytes: 0), client: 0, journal),
                $"The final read of {journal}",
                partitions);
            Assert.True(state is not null, $"The final read of {journal} failed. {await DescribeAsync(partitions)}");
        }

        var start = DateTime.UtcNow;
        var failures = workload.History.Check();
        TestContext.Current.SendDiagnosticMessage(
            $"Checked {workload.History.Count} operations ({workload.History.UnknownCount} with unknown outcomes) on {journals.Length} journals in {(DateTime.UtcNow - start).TotalSeconds:F1} s.");
        if (failures.Count > 0)
        {
            File.WriteAllLines(Path.Combine(Path.GetTempPath(), "garnet-linearizability.txt"), failures);
            throw new XunitException($"{failures.Count} journal histories failed the linearizability check:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
        }

        Assert.True(moves >= minimumMoves, $"Only {moves} moves were committed.");
        Assert.True(workload.History.Count > 100, $"Only {workload.History.Count} operations were recorded.");

        // Every migration completed, and every partition holds only the buckets it owns.
        var map = (await partitions.ReadViewAsync()).Map;
        Assert.Empty(map.Migrations);
        for (var bucket = 0; bucket < partitions.BucketCount; bucket++)
        {
            foreach (var partition in partitions.Partitions)
            {
                var state = await BoundedAsync(router.Migrator.GetBucketAsync(partition, bucket, CancellationToken), $"Reading bucket {bucket} at {partition}", partitions);
                if (partition == map.GetOwner(bucket))
                {
                    Assert.Equal(("open", true), (state.State, state.Done));
                }
                else
                {
                    Assert.True(state.State == "absent" || state.Reclaimed && state.IndexCount == 0 && state.MarkerCount == 0, $"Partition {partition} holds an unreclaimed copy of bucket {bucket}: {state}.");
                }
            }
        }
    }
}
