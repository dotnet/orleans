using System.Buffers;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Runtime;
using Orleans.DurableTasks.Storage;
using Orleans.Journaling;
using Orleans.Serialization;

namespace Benchmarks.Journaling;

/// <summary>
/// Reports what recording pure reads as journal positions saves and costs against recording their values, across journal
/// sizes and storage latencies: the happy path, in which an execution records its reads, the grain commits them and the
/// execution completes; a recovery which resolves recorded positions; a snapshot which must resolve them first; and a
/// long-lived execution whose journal compacts repeatedly.
/// </summary>
/// <remarks>
/// <para>
/// Each journal holds a dictionary of 64 entries. Each history write sets 8 of them, in turn, to new values: strings of 32
/// or 1,024 characters, or instances of <see cref="AccessRecordingItem"/> holding a 32-character string. An execution
/// reads keys in turn, in attempts of <see cref="AccessRecordingFixture.TaskId"/> which the runtime's context provider
/// routes. Positions need the binary journal format, whose codecs reproduce values exactly; JSON journals record values.
/// </para>
/// <para>
/// Storage is in memory behind <see cref="SimulatedStorage"/>, which adds a latency per call and a cost per MiB read to
/// the measured operations, spin-waited because Windows timers tick at 15.6 ms. Setup runs without latency. Every case
/// checks that the execution's replay returns what its reads observed, after the history changed every key they read.
/// </para>
/// <para>Pass <c>quick</c> to run the smallest sizes only.</para>
/// </remarks>
internal static class PositionResolutionCost
{
    private const int KeyCount = 64;
    private const int SetsPerWrite = 8;
    private const int StepReads = 8;

    private static readonly StorageLatency[] Latencies =
    [
        new("0", 0, 0),
        new("1 ms + 4 ms/MiB", 1, 4),
        new("10 ms + 10 ms/MiB", 10, 10),
    ];

    public static void Run(string[] args)
    {
        var quick = args.Contains("quick");
        int[] historyWrites = quick ? [32, 256] : [32, 256, 2_048, 10_000];
        int[] readCounts = quick ? [64] : [64, 1_024];
        int[] compactionThresholds = quick ? [32] : [32, 256, 2_048];
        IServiceProvider services = CreateServices();
        ValueShape[] shapes =
        [
            new("string(32)", 32, MaxHistory: int.MaxValue),
            new("record(32)", 32, MaxHistory: int.MaxValue),
            new("string(1024)", 1_024, MaxHistory: 2_048),
        ];

        Console.WriteLine($"Task identifier: `{AccessRecordingFixture.TaskId}`; binary journal; {Environment.ProcessorCount} logical processors.");
        Console.WriteLine();
        Console.WriteLine("### Happy path: an execution records its reads, the grain commits them, and the execution completes");
        Console.WriteLine();
        Console.WriteLine("Commit: the write which journals the execution's records (and, with positions, its barrier). Completion: the write which journals the log's removal. Time: the attempt's reads and the commit write. Medians of 9 runs after one warm-up, without storage latency, which adds the same two calls with and without positions.");
        Console.WriteLine();
        Console.WriteLine("| Values | History writes | Journal KiB | Reads | Commit B off | on | Completion B off | on | Bytes on/off | µs off | on | Time on/off |");
        Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var shape in shapes)
        {
            foreach (var history in historyWrites.Where(h => h <= shape.MaxHistory))
            {
                foreach (var reads in readCounts)
                {
                    var off = Bench.For(shape, services).HappyPath(history, reads, positions: false);
                    var on = Bench.For(shape, services).HappyPath(history, reads, positions: true);
                    Console.WriteLine(
                        $"| {shape.Name} | {history:N0} | {off.JournalBytes / 1024.0:N0} | {reads:N0} | {off.CommitBytes:N0} | {on.CommitBytes:N0} | {off.CompletionBytes:N0} | {on.CompletionBytes:N0} | "
                        + $"{(double)(on.CommitBytes + on.CompletionBytes) / (off.CommitBytes + off.CompletionBytes):0.00} | {off.Micros:N0} | {on.Micros:N0} | {on.Micros / off.Micros:0.00} |");
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine("### Recovery: the execution recorded its reads halfway through the history and is still running");
        Console.WriteLine();
        Console.WriteLine("Recovery: the activation's initialization, which with positions resolves the recovered reads from the content it read. First write: what the write after recovery journals (with positions, the resolved log and the barrier's release). Medians of 5 runs (3 with latency) after one warm-up.");
        Console.WriteLine();
        Console.WriteLine("| Values | History writes | Journal KiB | Reads | Latency | Recovery ms off | on | on/off | Storage reads off/on | First write B off | on | Allocated KiB off | on |");
        Console.WriteLine("|---|---:|---:|---:|---|---:|---:|---:|---|---:|---:|---:|---:|");
        foreach (var shape in shapes)
        {
            foreach (var history in historyWrites.Where(h => h <= shape.MaxHistory))
            {
                foreach (var reads in readCounts)
                {
                    var off = Bench.For(shape, services).PrepareRecovery(history, reads, positions: false);
                    var on = Bench.For(shape, services).PrepareRecovery(history, reads, positions: true);
                    foreach (var latency in Latencies)
                    {
                        var a = off.Measure(latency);
                        var b = on.Measure(latency);
                        Console.WriteLine(
                            $"| {shape.Name} | {history:N0} | {a.JournalBytes / 1024.0:N0} | {reads:N0} | {latency.Name} | {a.Millis:N2} | {b.Millis:N2} | {b.Millis / a.Millis:0.00} | {a.StorageReads}/{b.StorageReads} | "
                            + $"{a.FirstWriteBytes:N0} | {b.FirstWriteBytes:N0} | {a.AllocatedBytes / 1024.0:N0} | {b.AllocatedBytes / 1024.0:N0} |");
                    }
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine("### Forced resolution: a snapshot while a running execution holds positional reads");
        Console.WriteLine();
        Console.WriteLine($"Compaction is requested after the reads and a history which changes every key they read. With positions, the barrier defers up to {JournalPositionTracker.MaxDeferredSnapshots} snapshots, then the next write reads the history, resolves the reads and snapshots; without, the first write snapshots. Time and allocation: the snapshotting write's. Medians of 5 runs (3 with latency) after one warm-up.");
        Console.WriteLine();
        Console.WriteLine("| Values | History writes | Journal KiB | Reads | Latency | Snapshot ms off | on | on/off | Deferred writes on | Storage reads off/on | MiB read on | Allocated KiB off | on |");
        Console.WriteLine("|---|---:|---:|---:|---|---:|---:|---:|---:|---|---:|---:|---:|");
        foreach (var shape in shapes)
        {
            foreach (var history in historyWrites.Where(h => h <= shape.MaxHistory))
            {
                foreach (var reads in readCounts)
                {
                    foreach (var latency in Latencies)
                    {
                        var a = Bench.For(shape, services).ForcedSnapshot(history, reads, positions: false, latency);
                        var b = Bench.For(shape, services).ForcedSnapshot(history, reads, positions: true, latency);
                        Console.WriteLine(
                            $"| {shape.Name} | {history:N0} | {a.JournalBytes / 1024.0:N0} | {reads:N0} | {latency.Name} | {a.Millis:N2} | {b.Millis:N2} | {b.Millis / a.Millis:0.00} | {b.DeferredWrites} | "
                            + $"{a.StorageReads}/{b.StorageReads} | {b.BytesRead / 1048576.0:N2} | {a.AllocatedBytes / 1024.0:N0} | {b.AllocatedBytes / 1024.0:N0} |");
                    }
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"### Steady state: a long-lived execution reads {StepReads} keys per step while the grain writes {SetsPerWrite} sets per step");
        Console.WriteLine();
        Console.WriteLine($"Storage requests compaction once it holds the threshold's appends since its last snapshot. Each run takes 3 × (threshold + {JournalPositionTracker.MaxDeferredSnapshots}) steps of one attempt. One run per row.");
        Console.WriteLine();
        Console.WriteLine("| Values | Compaction after | Steps | Latency | µs/step off | on | on/off | Appended KiB off | on | Snapshot KiB off | on | Snapshots off/on | History reads on | MiB read on |");
        Console.WriteLine("|---|---:|---:|---|---:|---:|---:|---:|---:|---:|---:|---|---:|---:|");
        foreach (var shape in shapes)
        {
            foreach (var threshold in compactionThresholds)
            {
                foreach (var latency in new[] { Latencies[0], Latencies[2] })
                {
                    var a = Bench.For(shape, services).SteadyState(threshold, positions: false, latency);
                    var b = Bench.For(shape, services).SteadyState(threshold, positions: true, latency);
                    Console.WriteLine(
                        $"| {shape.Name} | {threshold:N0} | {a.Steps:N0} | {latency.Name} | {a.MicrosPerStep:N0} | {b.MicrosPerStep:N0} | {b.MicrosPerStep / a.MicrosPerStep:0.00} | "
                        + $"{a.AppendedBytes / 1024.0:N0} | {b.AppendedBytes / 1024.0:N0} | {a.ReplacedBytes / 1024.0:N0} | {b.ReplacedBytes / 1024.0:N0} | {a.Snapshots}/{b.Snapshots} | {b.HistoryReads} | {b.BytesRead / 1048576.0:N2} |");
                }
            }
        }
    }

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddSerializer(serializer => serializer.AddAssembly(typeof(AccessRecordingItem).Assembly));
        services.AddSingleton<OrleansBinaryJournalFormat>();
        services.AddKeyedSingleton<IJournalFormat>(OrleansBinaryJournalFormat.JournalFormatKey, static (sp, _) => sp.GetRequiredService<OrleansBinaryJournalFormat>());
        services.AddKeyedSingleton(typeof(IDurableDictionaryCommandCodec<,>), OrleansBinaryJournalFormat.JournalFormatKey, typeof(OrleansBinaryDurableDictionaryCommandCodec<,>));
        return services.BuildServiceProvider();
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        return values[values.Count / 2];
    }

    private sealed record ValueShape(string Name, int Length, int MaxHistory);

    private readonly record struct StorageLatency(string Name, double MillisPerCall, double MillisPerMiBRead);

    private sealed record HappyPathResult(long JournalBytes, long CommitBytes, long CompletionBytes, double Micros);

    private sealed record RecoveryResult(long JournalBytes, double Millis, int StorageReads, long FirstWriteBytes, long AllocatedBytes);

    private sealed record SnapshotResult(long JournalBytes, double Millis, int DeferredWrites, int StorageReads, long BytesRead, long AllocatedBytes);

    private sealed record SteadyStateResult(int Steps, double MicrosPerStep, long AppendedBytes, long ReplacedBytes, int Snapshots, int HistoryReads, long BytesRead);

    private abstract class RecoveryBench
    {
        public abstract RecoveryResult Measure(StorageLatency latency);
    }

    private abstract class Bench
    {
        // The journals of each value shape's histories, which the cases clone.
        protected static readonly Dictionary<(string Shape, int Writes), SimulatedStorage> Histories = [];

        public static Bench For(ValueShape shape, IServiceProvider services) => shape.Name.StartsWith("record", StringComparison.Ordinal)
            ? new Bench<AccessRecordingItem>(shape, services, (write, key) => new(Text(write, key, shape.Length), write))
            : new Bench<string>(shape, services, (write, key) => Text(write, key, shape.Length));

        public abstract HappyPathResult HappyPath(int historyWrites, int reads, bool positions);

        public abstract RecoveryBench PrepareRecovery(int historyWrites, int reads, bool positions);

        public abstract SnapshotResult ForcedSnapshot(int historyWrites, int reads, bool positions, StorageLatency latency);

        public abstract SteadyStateResult SteadyState(int compactionThreshold, bool positions, StorageLatency latency);

        private static string Text(int write, int key, int length)
        {
            var prefix = $"value-{write:D5}-{key:D2}-";
            return prefix + new string('x', length - prefix.Length);
        }
    }

    private sealed class Bench<TValue>(ValueShape shape, IServiceProvider services, Func<int, int, TValue> createValue) : Bench where TValue : class
    {
        // The number of the next history write, whose values differ from every earlier write's.
        private int _write;

        public override HappyPathResult HappyPath(int historyWrites, int reads, bool positions)
        {
            var history = History(historyWrites);
            var storage = history.Clone();
            using var activation = new Activation(services, storage, positions);
            var micros = new List<double>();
            long commitBytes = 0;
            long completionBytes = 0;
            for (var i = -1; i < 9; i++)
            {
                var appended = storage.AppendedBytes;
                var timestamp = Stopwatch.GetTimestamp();
                var log = activation.Runtime.RunAttempt(AccessRecordingFixture.TaskId, activation.AccessLogs, () => Read(activation, 0, reads, observed: null));
                log.Flush();
                activation.Write();
                var elapsed = Stopwatch.GetElapsedTime(timestamp).TotalMicroseconds;
                var commit = storage.AppendedBytes - appended;
                log.Close();
                activation.Logs.Remove(AccessRecordingFixture.TaskId);
                appended = storage.AppendedBytes;
                activation.Write();
                if (i >= 0)
                {
                    micros.Add(elapsed);
                    commitBytes = commit;
                    completionBytes = storage.AppendedBytes - appended;
                }
            }

            return new(history.StoredBytes, commitBytes, completionBytes, Median(micros));
        }

        public override RecoveryBench PrepareRecovery(int historyWrites, int reads, bool positions)
        {
            // The execution reads halfway through the history; the rest of the history changes every key which it read.
            var storage = new SimulatedStorage();
            var observed = new List<TValue?>();
            using (var activation = new Activation(services, storage, positions))
            {
                WriteHistory(activation, historyWrites / 2);
                var log = activation.Runtime.RunAttempt(AccessRecordingFixture.TaskId, activation.AccessLogs, () => Read(activation, 0, reads, observed));
                log.Flush();
                activation.Write();
                WriteHistory(activation, historyWrites - historyWrites / 2);
            }

            return new Recovery(services, storage, positions, reads, observed);
        }

        public override SnapshotResult ForcedSnapshot(int historyWrites, int reads, bool positions, StorageLatency latency)
        {
            var history = History(historyWrites);
            var millis = new List<double>();
            var allocated = new List<double>();
            SnapshotResult? result = null;
            var runs = latency.MillisPerCall == 0 ? 5 : 3;
            for (var i = -1; i < runs; i++)
            {
                var storage = history.Clone();
                using var activation = new Activation(services, storage, positions);
                _write = historyWrites;
                var observed = new List<TValue?>();
                var attempt = activation.Runtime.StartAttempt(AccessRecordingFixture.TaskId, activation.AccessLogs);
                AccessRecordingRuntime.RunSegment(attempt, () => Read(activation, 0, reads, observed));
                attempt.AccessLog!.Flush();
                activation.Write();
                WriteHistory(activation, KeyCount / SetsPerWrite);
                storage.CompactionRequested = true;
                for (var deferred = 0; ; deferred++)
                {
                    if (deferred > JournalPositionTracker.MaxDeferredSnapshots)
                    {
                        throw new InvalidOperationException("The barrier deferred the snapshot beyond its limit.");
                    }

                    SetNextValues(activation);
                    var (replaces, storageReads, bytesRead) = (storage.Replaces, storage.Reads, storage.BytesRead);
                    storage.Latency = latency;
                    var start = GC.GetTotalAllocatedBytes(precise: true);
                    var timestamp = Stopwatch.GetTimestamp();
                    activation.Write();
                    var elapsed = Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;
                    var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - start;
                    storage.Latency = default;
                    if (storage.Replaces > replaces)
                    {
                        result = new(history.StoredBytes, 0, deferred, storage.Reads - storageReads, storage.BytesRead - bytesRead, 0);
                        if (i >= 0)
                        {
                            millis.Add(elapsed);
                            allocated.Add(allocatedBytes);
                        }

                        break;
                    }
                }

                if (activation.Manager.Positions.HasBarriers)
                {
                    throw new InvalidOperationException("The snapshot didn't resolve the reads.");
                }

                attempt.AccessLog!.Close();
                CheckReplay(activation, observed, (activation, replayed) => Read(activation, 0, reads, replayed));
            }

            return result! with { Millis = Median(millis), AllocatedBytes = (long)Median(allocated) };
        }

        public override SteadyStateResult SteadyState(int compactionThreshold, bool positions, StorageLatency latency)
        {
            var storage = new SimulatedStorage { CompactAfterAppends = compactionThreshold };
            using var activation = new Activation(services, storage, positions);
            var steps = 3 * (compactionThreshold + JournalPositionTracker.MaxDeferredSnapshots);
            var observed = new List<TValue?>();
            var attempt = activation.Runtime.StartAttempt(AccessRecordingFixture.TaskId, activation.AccessLogs);
            var (appended, replaced, replaces, reads, bytesRead) = (storage.AppendedBytes, storage.ReplacedBytes, storage.Replaces, storage.Reads, storage.BytesRead);
            storage.Latency = latency;
            var timestamp = Stopwatch.GetTimestamp();
            for (var step = 0; step < steps; step++)
            {
                var first = step * StepReads;
                AccessRecordingRuntime.RunSegment(attempt, () => Read(activation, first, StepReads, observed));
                attempt.AccessLog!.Flush();
                SetNextValues(activation);
                activation.Write();
            }

            var elapsed = Stopwatch.GetElapsedTime(timestamp).TotalMicroseconds;
            storage.Latency = default;
            attempt.AccessLog!.Close();
            CheckReplay(activation, observed, (activation, replayed) => Read(activation, 0, steps * StepReads, replayed));
            return new(
                steps,
                elapsed / steps,
                storage.AppendedBytes - appended,
                storage.ReplacedBytes - replaced,
                storage.Replaces - replaces,
                storage.Reads - reads,
                storage.BytesRead - bytesRead);
        }

        // The journal of historyWrites writes, which runs clone and continue from.
        private SimulatedStorage History(int historyWrites)
        {
            if (!Histories.TryGetValue((shape.Name, historyWrites), out var history))
            {
                history = new SimulatedStorage();
                using var activation = new Activation(services, history, positions: false);
                WriteHistory(activation, historyWrites);
                Histories[(shape.Name, historyWrites)] = history;
            }

            _write = historyWrites;
            return history;
        }

        private void WriteHistory(Activation activation, int writes)
        {
            for (var write = 0; write < writes; write++)
            {
                SetNextValues(activation);
                activation.Write();
            }
        }

        // Sets the keys of the next write, in turn, to new values, so every 8 writes change every key.
        private void SetNextValues(Activation activation)
        {
            var write = _write++;
            for (var i = 0; i < SetsPerWrite; i++)
            {
                var key = (write * SetsPerWrite + i) % KeyCount;
                activation.Dictionary[Key(key)] = createValue(write, key);
            }
        }

        private static void Read(Activation activation, int first, int count, List<TValue?>? observed)
        {
            for (var i = first; i < first + count; i++)
            {
                activation.Dictionary.TryGetValue(Key(i % KeyCount), out var value);
                observed?.Add(value);
            }
        }

        // Replays the execution's records in a new attempt, which must consume every record and return what the first run's
        // reads observed, which the live state no longer holds.
        private static void CheckReplay(Activation activation, List<TValue?> observed, Action<Activation, List<TValue?>> body)
        {
            var replayed = new List<TValue?>();
            var log = activation.Runtime.RunAttempt(AccessRecordingFixture.TaskId, activation.AccessLogs, () => body(activation, replayed));
            if (log.IsReplaying || !replayed.SequenceEqual(observed))
            {
                throw new InvalidOperationException("The execution's replay didn't return what its reads observed.");
            }

            log.Close();
            activation.Dictionary.TryGetValue(Key(0), out var live);
            if (Equals(live, observed[0]))
            {
                throw new InvalidOperationException("The history after the reads didn't change what they observed.");
            }
        }

        private static string Key(int key) => $"key-{key:D2}";

        private sealed class Recovery(IServiceProvider services, SimulatedStorage template, bool positions, int reads, List<TValue?> observed) : RecoveryBench
        {
            public override RecoveryResult Measure(StorageLatency latency)
            {
                var millis = new List<double>();
                var allocated = new List<double>();
                var storageReads = 0;
                long firstWrite = 0;
                var runs = latency.MillisPerCall == 0 ? 5 : 3;
                for (var i = -1; i < runs; i++)
                {
                    var storage = template.Clone();
                    storage.Latency = latency;
                    var start = GC.GetTotalAllocatedBytes(precise: true);
                    var timestamp = Stopwatch.GetTimestamp();
                    using var activation = new Activation(services, storage, positions);
                    var elapsed = Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;
                    var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - start;
                    storage.Latency = default;
                    var appended = storage.AppendedBytes;
                    activation.Write();
                    CheckReplay(activation, observed, (activation, replayed) => Read(activation, 0, reads, replayed));
                    if (i >= 0)
                    {
                        millis.Add(elapsed);
                        allocated.Add(allocatedBytes);
                        storageReads = storage.Reads;
                        firstWrite = storage.AppendedBytes - appended;
                    }
                }

                return new(template.StoredBytes, Median(millis), storageReads, firstWrite, (long)Median(allocated));
            }
        }

        private sealed class Activation : IDisposable
        {
            public Activation(IServiceProvider services, SimulatedStorage storage, bool positions)
            {
                var formatKey = OrleansBinaryJournalFormat.JournalFormatKey;
                var shared = new JournaledStateManagerShared(
                    NullLogger<JournaledStateManager>.Instance,
                    Options.Create(new JournaledStateManagerOptions { JournalFormatKey = formatKey }),
                    TimeProvider.System,
                    services);
                var serializer = services.GetRequiredService<Serializer>();
                Runtime = new AccessRecordingRuntime(serializer);
                Manager = new JournaledStateManager(shared, storage) { ContextProvider = new DurableTaskAccessContextProvider(Runtime) };
                Dictionary = new DurableDictionary<string, TValue>(
                    "dict",
                    Manager,
                    JournalFormatServices.GetRequiredCommandCodec<IDurableDictionaryCommandCodec<string, TValue>>(services, formatKey));
                Logs = new DurableTaskAccessLogs(
                    Manager,
                    JournalFormatServices.GetRequiredCommandCodec<IDurableDictionaryCommandCodec<TaskId, byte[]>>(services, formatKey),
                    positions ? serializer : null);
                AccessLogs = new AccessLogStorage(Logs);
                Manager.InitializeAsync().AsTask().GetAwaiter().GetResult();
            }

            public JournaledStateManager Manager { get; }

            public DurableDictionary<string, TValue> Dictionary { get; }

            public DurableTaskAccessLogs Logs { get; }

            public AccessLogStorage AccessLogs { get; }

            public AccessRecordingRuntime Runtime { get; }

            public void Write() => Manager.WriteStateAsync().AsTask().GetAwaiter().GetResult();

            public void Dispose() => Manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// In-memory journal storage which adds <see cref="Latency"/> to each call, requests compaction on demand or after a
    /// number of appends, and counts what calls read and store.
    /// </summary>
    private sealed class SimulatedStorage : IJournalStorage
    {
        private readonly VolatileJournalStorage _inner = new(OrleansBinaryJournalFormat.JournalFormatKey);
        private int _appendsSinceSnapshot;

        public StorageLatency Latency { get; set; }

        public bool CompactionRequested { get; set; }

        public int CompactAfterAppends { get; init; } = int.MaxValue;

        public int Reads { get; private set; }

        public long BytesRead { get; private set; }

        public long AppendedBytes { get; private set; }

        public long ReplacedBytes { get; private set; }

        public int Replaces { get; private set; }

        public long StoredBytes => _inner.Segments.Sum(segment => (long)segment.Length);

        public bool IsCompactionRequested => CompactionRequested || _appendsSinceSnapshot >= CompactAfterAppends;

        /// <summary>Returns new storage with this storage's content.</summary>
        public SimulatedStorage Clone()
        {
            var clone = new SimulatedStorage { CompactAfterAppends = CompactAfterAppends };
            clone._inner.CreateIfNotExistsAsync().AsTask().GetAwaiter().GetResult();
            foreach (var segment in _inner.Segments)
            {
                clone._inner.AppendAsync(new ReadOnlySequence<byte>(segment), CancellationToken.None).AsTask().GetAwaiter().GetResult();
            }

            return clone;
        }

        public ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken)
        {
            var bytes = StoredBytes;
            Delay(Latency.MillisPerCall + Latency.MillisPerMiBRead * bytes / 1048576.0);
            Reads++;
            BytesRead += bytes;
            return _inner.ReadAsync(consumer, cancellationToken);
        }

        public ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
        {
            Delay(Latency.MillisPerCall);
            AppendedBytes += value.Length;
            _appendsSinceSnapshot++;
            return _inner.AppendAsync(value, cancellationToken);
        }

        public ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
        {
            Delay(Latency.MillisPerCall);
            Replaces++;
            ReplacedBytes += value.Length;
            _appendsSinceSnapshot = 0;
            CompactionRequested = false;
            return _inner.ReplaceAsync(value, cancellationToken);
        }

        public ValueTask DeleteAsync(CancellationToken cancellationToken) => _inner.DeleteAsync(cancellationToken);

        public ValueTask<bool> CreateIfNotExistsAsync(IReadOnlyDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default) =>
            _inner.CreateIfNotExistsAsync(metadata, cancellationToken);

        public ValueTask<IJournalMetadata?> GetMetadataAsync(CancellationToken cancellationToken = default) => _inner.GetMetadataAsync(cancellationToken);

        public ValueTask<IJournalMetadata?> UpdateMetadataAsync(
            IReadOnlyDictionary<string, string>? set = null,
            IEnumerable<string>? remove = null,
            string? expectedETag = null,
            CancellationToken cancellationToken = default) => _inner.UpdateMetadataAsync(set, remove, expectedETag, cancellationToken);

        // Waits by spinning: the timer's resolution on Windows is 15.6 ms.
        private static void Delay(double millis)
        {
            if (millis <= 0)
            {
                return;
            }

            var end = Stopwatch.GetTimestamp() + (long)(millis * Stopwatch.Frequency / 1000);
            while (Stopwatch.GetTimestamp() < end)
            {
                Thread.SpinWait(64);
            }
        }
    }
}
