using System.Buffers;
using CsCheck;
using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Runtime;
using Orleans.DurableTasks.Storage;
using Orleans.Journaling;
using Orleans.Serialization;
using Orleans.Storage;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.Tests;

/// <summary>
/// Verifies positional reads end to end: a body's pure reads record journal positions, and every replay after a recovery
/// observes what the first run observed, across interleaved writes, snapshots, deferred and forced resolutions, crashes,
/// and both journal formats.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class DurableTaskPositionalReadTests
{
    private static readonly TaskId Execution = TaskId.CreateRoot("positional-reads");
    private static readonly string[] Keys = ["a", "b", "c"];
    private static readonly string[] Values = ["x", "y", "z"];

    public static TheoryData<string> Formats => DurableTaskJournalHarness.Formats;

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task ReplayAfterRecoveryObservesWhatTheFirstRunObserved(string format)
    {
        var step = Gen.Frequency(
            (8, Gen.Select(Gen.Int[0, 8], Gen.Int[0, 2], static (operation, key) => new Step(StepKind.Read, operation, key, 0))),
            (4, Gen.Select(Gen.Int[0, 4], Gen.Int[0, 2], Gen.Int[0, 2], static (operation, key, value) => new Step(StepKind.Write, operation, key, value))),
            (4, Gen.Select(Gen.Int[0, 4], Gen.Int[0, 2], Gen.Int[0, 2], static (operation, key, value) => new Step(StepKind.Outside, operation, key, value))),
            (2, Gen.Const(new Step(StepKind.Flush, 0, 0, 0))),
            (2, Gen.Const(new Step(StepKind.WriteJournal, 0, 0, 0))),
            (1, Gen.Const(new Step(StepKind.Snapshot, 0, 0, 0))),
            (1, Gen.Const(new Step(StepKind.ForceResolution, 0, 0, 0))));
        var positionalRuns = 0;
        await Gen.Select(step.Array[1, 30], Gen.Int[0, 2]).SampleAsync(async scenario =>
        {
            var (steps, recovery) = scenario;
            await using var harness = new Harness(format);
            List<object?> observed;
            await using (var first = await harness.ActivateAsync())
            {
                observed = await first.RunAsync(steps, replaying: false);
                if (first.CountPositionalRecords() > 0)
                {
                    positionalRuns++;
                }
            }

            // A recovery which resolves positions in memory and crashes, or which journals the resolution, before the replay.
            if (recovery > 0)
            {
                await using var resolving = await harness.ActivateAsync();
                Assert.Equal(0, resolving.CountPositionalRecords());
                if (recovery == 2)
                {
                    await resolving.Manager.WriteStateAsync(Cancellation);
                }
            }

            await using var replay = await harness.ActivateAsync();
            Assert.Equal(observed, await replay.RunAsync(steps, replaying: true));
        }, iter: 60);

        Assert.True(positionalRuns > 0, "No run recorded a position.");
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task CrashBeforeTheForcedResolutionsSnapshotLeavesHistoryForRecovery(string format)
    {
        await using var harness = new Harness(format);
        List<object?> observed;
        await using (var first = await harness.ActivateAsync())
        {
            observed = await first.RunAsync([Read(1, 0), Write(1, 0, 1), Read(1, 0), Read(4, 0)], replaying: false);
            Assert.True(first.CountPositionalRecords() > 0);
            harness.Storage.RequestCompaction = true;
            for (var i = 0; i < JournalPositionTracker.MaxDeferredSnapshots; i++)
            {
                await first.Manager.WriteStateAsync(Cancellation);
            }

            harness.Storage.FailNext = Failure.ReplaceBefore;
            await Assert.ThrowsAnyAsync<Exception>(async () => await first.Manager.WriteStateAsync(Cancellation));
            harness.Storage.RequestCompaction = false;
        }

        // Recovery resolves the positions from the history which it read.
        harness.Storage.Reads = 0;
        await using var replay = await harness.ActivateAsync();
        Assert.Equal(1, harness.Storage.Reads);
        Assert.Equal(0, replay.CountPositionalRecords());
        Assert.Equal(observed, await replay.RunAsync([Read(1, 0), Write(1, 0, 1), Read(1, 0), Read(4, 0)], replaying: true));
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task CrashAfterTheForcedResolutionsSnapshotRecoversValues(string format)
    {
        await using var harness = new Harness(format);
        List<object?> observed;
        await using (var first = await harness.ActivateAsync())
        {
            observed = await first.RunAsync([Read(0, 0), Write(0, 0, 2), Read(0, 0), Read(6, 0)], replaying: false);
            harness.Storage.RequestCompaction = true;
            for (var i = 0; i < JournalPositionTracker.MaxDeferredSnapshots; i++)
            {
                await first.Manager.WriteStateAsync(Cancellation);
            }

            harness.Storage.FailNext = Failure.ReplaceAfter;
            await Assert.ThrowsAnyAsync<Exception>(async () => await first.Manager.WriteStateAsync(Cancellation));
            harness.Storage.RequestCompaction = false;
        }

        // The snapshot holds values and no barrier: recovery reads the journal once.
        harness.Storage.Reads = 0;
        await using var replay = await harness.ActivateAsync();
        Assert.Equal(1, harness.Storage.Reads);
        Assert.Equal(0, replay.CountPositionalRecords());
        Assert.Equal(observed, await replay.RunAsync([Read(0, 0), Write(0, 0, 2), Read(0, 0), Read(6, 0)], replaying: true));
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task CrashBeforeTheBarrierIsWrittenLeavesNoRecords(string format)
    {
        await using var harness = new Harness(format);
        await using (var first = await harness.ActivateAsync())
        {
            var log = first.CreateLog();
            first.Run(log, static activation => _ = activation.Dictionary.Count);
            log.Flush();
            Assert.True(first.Manager.Positions.HasBarriers);
            harness.Storage.FailNext = Failure.AppendBefore;
            await Assert.ThrowsAnyAsync<Exception>(async () => await first.Manager.WriteStateAsync(Cancellation));
        }

        harness.Storage.Reads = 0;
        await using var recovered = await harness.ActivateAsync();
        Assert.Equal(1, harness.Storage.Reads);
        Assert.False(recovered.Manager.Positions.HasBarriers);
        Assert.False(recovered.CreateLog().IsReplaying);
    }

    [Fact]
    public async Task MigrationResolvesPositionsAndRecordsValuesUntilItsSnapshot()
    {
        var journalId = JournalId.Create("positional-migration", Guid.NewGuid().ToString("N"));
        List<object?> observed;
        VolatileJournalStorageProvider storage;
        await using (var binary = new Harness(DurableTaskJournalHarness.OrleansBinary, journalId: journalId))
        {
            storage = binary.Inner.StorageProvider;
            await using var first = await binary.ActivateAsync();
            observed = await first.RunAsync([Read(1, 0), Write(1, 0, 0), Read(1, 0), Read(3, 1)], replaying: false);
            Assert.True(first.CountPositionalRecords() > 0);
        }

        await using var json = new Harness(DurableTaskJournalHarness.Json, storage, journalId);
        await using var migrating = await json.ActivateAsync();
        Assert.Equal(0, migrating.CountPositionalRecords());
        Assert.Equal(observed, await migrating.RunAsync([Read(1, 0), Write(1, 0, 0), Read(1, 0), Read(3, 1)], replaying: true));

        // Until the migration snapshot, reads record values.
        var other = TaskId.CreateRoot("other");
        var log = migrating.CreateLog(other);
        migrating.Run(log, static activation => _ = activation.Dictionary.Count);
        log.Flush();
        Assert.Null(DurableTaskAccessLog.FindPositionalRecords(migrating.Storage.GetAccessLog(other), []));
        await migrating.Manager.WriteStateAsync(Cancellation);
        Assert.True(migrating.Manager.Positions.TryGetPosition(out _));
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task ReadsWhichAttemptsMakeWhileHistoryIsReadResolveAsTheyWereRecorded(string format)
    {
        await using var harness = new Harness(format);
        var observed = new List<object?>();
        await using (var first = await harness.ActivateAsync())
        {
            first.Dictionary["a"] = "a-before";
            first.Dictionary["b"] = "b-before";
            await first.Manager.WriteStateAsync(Cancellation);
            var log = first.CreateLog();

            // A staged read names the state, so the pending read of "a", which forced resolution collects before it reads
            // history, and the later read of "b" have records of the same layout.
            first.Run(log, activation => observed.Add(activation.Dictionary["b"]));
            log.Flush();
            first.Run(log, activation => observed.Add(activation.Dictionary["a"]));
            harness.Storage.RequestCompaction = true;
            for (var i = 0; i < JournalPositionTracker.MaxDeferredSnapshots; i++)
            {
                await first.Manager.WriteStateAsync(Cancellation);
            }

            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            harness.Storage.ReadGate = gate;
            var snapshot = first.Manager.WriteStateAsync(Cancellation).AsTask();
            await harness.Storage.ReadStarted.Task.WaitAsync(Cancellation);

            // While history is read, the attempt stages that read and records another of the same size into the same buffer.
            log.Flush();
            first.Dictionary["a"] = "a-after";
            first.Run(log, activation => observed.Add(activation.Dictionary["b"]));
            harness.Storage.RequestCompaction = false;
            gate.SetResult();
            await snapshot;
            Assert.False(first.Manager.Positions.HasBarriers);
            log.Flush();
            await first.Manager.WriteStateAsync(Cancellation);
            log.Close();
        }

        await using var replay = await harness.ActivateAsync();
        var replayLog = replay.CreateLog();
        var replayed = new List<object?>();
        replay.Run(replayLog, activation =>
        {
            replayed.Add(activation.Dictionary["b"]);
            replayed.Add(activation.Dictionary["a"]);
            replayed.Add(activation.Dictionary["b"]);
        });
        Assert.Equal(["b-before", "a-before", "b-before"], observed);
        Assert.Equal(observed, replayed);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task DeletionResolvesPendingPositionsFirst(string format)
    {
        await using var harness = new Harness(format);
        await using var activation = await harness.ActivateAsync();
        activation.Dictionary["a"] = "x";
        await activation.Manager.WriteStateAsync(Cancellation);
        var log = activation.CreateLog();
        activation.Run(log, static activation => _ = activation.Dictionary["a"]);
        Assert.NotNull(DurableTaskAccessLog.FindPositionalRecords(log.PendingRecords, log.PendingStates));

        await activation.Manager.DeleteStateAsync(Cancellation);

        Assert.Null(DurableTaskAccessLog.FindPositionalRecords(log.PendingRecords, log.PendingStates));
        Assert.False(activation.Manager.Positions.HasBarriers);
    }

    [Fact]
    public async Task ForcedResolutionFencesWhenAnotherWriterChangedTheJournal()
    {
        await using var harness = new Harness(DurableTaskJournalHarness.OrleansBinary);
        await using var first = await harness.ActivateAsync();
        first.Dictionary["a"] = "first";
        await first.Manager.WriteStateAsync(Cancellation);
        var log = first.CreateLog();
        first.Run(log, static activation => _ = activation.Dictionary["a"]);
        log.Flush();
        harness.Storage.RequestCompaction = true;
        for (var i = 0; i < JournalPositionTracker.MaxDeferredSnapshots; i++)
        {
            await first.Manager.WriteStateAsync(Cancellation);
        }

        harness.Storage.RequestCompaction = false;
        Assert.True(first.Manager.Positions.HasBarriers);

        // A second activation of the same journal, as a split brain has, takes it over and writes.
        await using (var second = await harness.ActivateAsync())
        {
            second.Dictionary["a"] = "second";
            await second.Manager.WriteStateAsync(Cancellation);
        }

        // The first activation's forced resolution reads history which another writer changed, and fences.
        harness.Storage.RequestCompaction = true;
        harness.Storage.Replaces = 0;
        await Assert.ThrowsAsync<InconsistentStateException>(async () => await first.Manager.WriteStateAsync(Cancellation));
        harness.Storage.RequestCompaction = false;
        Assert.Equal(0, harness.Storage.Replaces);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await first.Manager.WriteStateAsync(Cancellation));

        await using var recovered = await harness.ActivateAsync();
        Assert.Equal("second", recovered.Dictionary["a"]);
    }

    [Fact]
    public async Task UnresolvedPositionStallsTheReplay()
    {
        await using var harness = new Harness(DurableTaskJournalHarness.OrleansBinary);
        await using (var first = await harness.ActivateAsync())
        {
            // A position which no barrier holds history for, as a record which a crossing snapshot left would be.
            var id = new JournaledStateAccessTarget("dictionary").Id;
            first.Storage.AppendAccessLog(Execution, [0xC1, (byte)JournaledStateOperation.DictionaryCount, 0, .. BitConverter.GetBytes(id), 3, 0]);
            await first.Manager.WriteStateAsync(Cancellation);
        }

        await using var replay = await harness.ActivateAsync();
        var log = replay.CreateLog();
        var stall = Assert.Throws<DurableTaskAttemptCanceledException>(() => replay.Run(log, static activation => _ = activation.Dictionary.Count));
        var divergence = Assert.IsType<DurableTaskDivergenceException>(stall.InnerException);
        Assert.Contains("position of the journal", divergence.Message, StringComparison.Ordinal);
    }

    private static Step Read(int operation, int key) => new(StepKind.Read, operation, key, 0);

    private static Step Write(int operation, int key, int value) => new(StepKind.Write, operation, key, value);

    private enum StepKind
    {
        Read,
        Write,
        Outside,
        Flush,
        WriteJournal,
        Snapshot,
        ForceResolution,
    }

    private readonly record struct Step(StepKind Kind, int Operation, int Key, int Value);

    private enum Failure
    {
        None,
        AppendBefore,
        ReplaceBefore,
        ReplaceAfter,
    }

    /// <summary>A journal harness whose storage requests compaction and fails writes on demand.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        public Harness(string format, VolatileJournalStorageProvider? storage = null, JournalId? journalId = null)
        {
            Inner = new DurableTaskJournalHarness(format, storageProvider: storage, journalId: journalId, wrapStorage: inner => new ControlledProvider(inner, Storage));
        }

        public DurableTaskJournalHarness Inner { get; }

        public StorageControl Storage { get; } = new();

        public async Task<Activation> ActivateAsync()
        {
            var scope = Inner.CreateScope();
            try
            {
                var services = scope.ServiceProvider;
                var dictionary = (DurableDictionary<string, string>)services.GetRequiredKeyedService<IDurableDictionary<string, string>>("dictionary");
                var value = (DurableValue<string>)services.GetRequiredKeyedService<IDurableValue<string>>("value");
                var list = (DurableList<string>)services.GetRequiredKeyedService<IDurableList<string>>("list");
                var storage = services.GetRequiredService<DurableTaskGrainStorage>();
                var manager = (JournaledStateManager)services.GetRequiredService<IJournaledStateManager>();
                var provider = new Provider();
                manager.ContextProvider = provider;
                await storage.ReadAsync(Cancellation);
                return new(scope, this, manager, storage, provider, dictionary, value, list, services.GetRequiredService<Serializer>());
            }
            catch
            {
                await scope.DisposeAsync();
                throw;
            }
        }

        public ValueTask DisposeAsync() => Inner.DisposeAsync();
    }

    private sealed class Activation(
        AsyncServiceScope scope,
        Harness harness,
        JournaledStateManager manager,
        DurableTaskGrainStorage storage,
        Provider provider,
        DurableDictionary<string, string> dictionary,
        DurableValue<string> value,
        DurableList<string> list,
        Serializer serializer) : IAsyncDisposable
    {
        public JournaledStateManager Manager => manager;

        public DurableTaskGrainStorage Storage => storage;

        public DurableDictionary<string, string> Dictionary => dictionary;

        public DurableTaskAccessLog CreateLog(TaskId? taskId = null) =>
            new(new TestDurableTaskAttempt(taskId ?? Execution), storage, serializer, TaskScheduler.Current, static exception => new DurableTaskAttemptCanceledException(exception.Message, exception));

        public int CountPositionalRecords() => DurableTaskAccessLog.FindPositionalRecords(storage.GetAccessLog(Execution), [])?.Count ?? 0;

        public void Run(DurableTaskAccessLog log, Action<Activation> body)
        {
            provider.Log = log;
            try
            {
                body(this);
            }
            finally
            {
                provider.Log = null;
            }
        }

        /// <summary>
        /// Runs the steps as the execution's body; a first run then stages and writes its records, and a replay checks that it
        /// consumed them all. Returns what the body observed.
        /// </summary>
        public async Task<List<object?>> RunAsync(Step[] steps, bool replaying)
        {
            var log = CreateLog();
            var observed = new List<object?>();
            foreach (var step in steps)
            {
                switch (step.Kind)
                {
                    case StepKind.Read or StepKind.Write:
                        provider.Log = log;
                        try
                        {
                            observed.Add(step.Kind is StepKind.Read ? ReadStep(step) : WriteStep(step));
                        }
                        catch (Exception exception) when (exception is not DurableTaskAttemptCanceledException)
                        {
                            observed.Add(exception.GetType().Name);
                        }
                        finally
                        {
                            provider.Log = null;
                        }

                        break;
                    case StepKind.Outside when !replaying:
                        try
                        {
                            WriteStep(step);
                        }
                        catch (ArgumentOutOfRangeException)
                        {
                        }

                        break;
                    case StepKind.Flush when !replaying:
                        log.Flush();
                        break;
                    case StepKind.WriteJournal when !replaying:
                        await manager.WriteStateAsync(Cancellation);
                        break;
                    case StepKind.Snapshot when !replaying:
                        harness.Storage.RequestCompaction = true;
                        await manager.WriteStateAsync(Cancellation);
                        harness.Storage.RequestCompaction = false;
                        break;
                    case StepKind.ForceResolution when !replaying:
                        harness.Storage.RequestCompaction = true;
                        for (var i = 0; i <= JournalPositionTracker.MaxDeferredSnapshots && manager.Positions.HasBarriers; i++)
                        {
                            await manager.WriteStateAsync(Cancellation);
                        }

                        harness.Storage.RequestCompaction = false;
                        break;
                }
            }

            if (replaying)
            {
                Assert.False(log.IsReplaying);
            }
            else
            {
                log.Flush();
                await manager.WriteStateAsync(Cancellation);
            }

            log.Close();
            return observed;
        }

        private object? ReadStep(Step step)
        {
            var key = Keys[step.Key];
            return step.Operation switch
            {
                0 => value.Value,
                1 => dictionary.TryGetValue(key, out var found) ? $"found {found}" : "missing",
                2 => dictionary[key],
                3 => dictionary.ContainsKey(key),
                4 => dictionary.Count,
                5 => string.Join(",", dictionary.Keys),
                6 => list.Count,
                7 => string.Join(",", list),
                _ => list[0],
            };
        }

        private object? WriteStep(Step step)
        {
            switch (step.Operation)
            {
                case 0:
                    value.Value = Values[step.Value];
                    return null;
                case 1:
                    dictionary[Keys[step.Key]] = Values[step.Value];
                    return null;
                case 2:
                    return dictionary.Remove(Keys[step.Key]);
                case 3:
                    list.Add(Values[step.Value]);
                    return null;
                default:
                    list.RemoveAt(0);
                    return null;
            }
        }

        public ValueTask DisposeAsync() => scope.DisposeAsync();
    }

    private sealed class Provider : JournaledStateContextProvider
    {
        public DurableTaskAccessLog? Log;

        public override JournaledStateContext? Current => Log is { IsActive: true } log ? log : null;
    }

    private sealed class StorageControl
    {
        public bool RequestCompaction { get; set; }

        public Failure FailNext { get; set; }

        public int Reads { get; set; }

        // Counts replacements which storage accepted.
        public int Replaces { get; set; }

        // Holds the next read until the test completes it.
        public TaskCompletionSource? ReadGate { get; set; }

        public TaskCompletionSource ReadStarted { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ControlledProvider(IJournalStorageProvider inner, StorageControl control) : IJournalStorageProvider
    {
        public IJournalStorage CreateStorage(JournalId journalId) => new ControlledStorage(inner.CreateStorage(journalId), control);
    }

    private sealed class ControlledStorage(IJournalStorage inner, StorageControl control) : IJournalStorage
    {
        public bool IsCompactionRequested => control.RequestCompaction || inner.IsCompactionRequested;

        public async ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken)
        {
            control.Reads++;
            if (control.ReadGate is { } gate)
            {
                control.ReadGate = null;
                control.ReadStarted.TrySetResult();
                await gate.Task;
            }

            await inner.ReadAsync(consumer, cancellationToken);
        }

        public async ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
        {
            ThrowIf(Failure.ReplaceBefore);
            await inner.ReplaceAsync(value, cancellationToken);
            control.Replaces++;
            ThrowIf(Failure.ReplaceAfter);
        }

        public async ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
        {
            ThrowIf(Failure.AppendBefore);
            await inner.AppendAsync(value, cancellationToken);
        }

        public ValueTask DeleteAsync(CancellationToken cancellationToken) => inner.DeleteAsync(cancellationToken);

        public ValueTask<bool> CreateIfNotExistsAsync(IReadOnlyDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default) =>
            inner.CreateIfNotExistsAsync(metadata, cancellationToken);

        public ValueTask<IJournalMetadata?> GetMetadataAsync(CancellationToken cancellationToken = default) => inner.GetMetadataAsync(cancellationToken);

        public ValueTask<IJournalMetadata?> UpdateMetadataAsync(
            IReadOnlyDictionary<string, string>? set = null,
            IEnumerable<string>? remove = null,
            string? expectedETag = null,
            CancellationToken cancellationToken = default) => inner.UpdateMetadataAsync(set, remove, expectedETag, cancellationToken);

        private void ThrowIf(Failure failure)
        {
            if (control.FailNext == failure)
            {
                control.FailNext = Failure.None;
                throw new IOException($"Injected failure: {failure}.");
            }
        }
    }
}
