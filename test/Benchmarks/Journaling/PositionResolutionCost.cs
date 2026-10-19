using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Runtime;
using Orleans.DurableTasks.Storage;
using Orleans.Journaling;
using Orleans.Journaling.Json;
using Orleans.Serialization;

namespace Benchmarks.Journaling;

/// <summary>
/// Reports what positional reads cost on the recovery path: a recovery whose journal holds a barrier, which reads the
/// journal a second time and resolves the reads into values; and a snapshot which must resolve them first.
/// </summary>
/// <remarks>
/// Each journal holds a dictionary of 64 entries with 32-character values, written in <see cref="HistoryWrites"/> journal
/// writes of 8 sets each, and one execution's access log with <see cref="Reads"/> reads of distinct keys. With
/// <c>BENCH_POSITIONS</c> set to <c>off</c>, the reads record values, as P2 does.
/// </remarks>
internal static class PositionResolutionCost
{
    public const int Reads = 64;
    public const int HistoryWrites = 32;
    private const int Iterations = 200;

    public static void Run()
    {
        Console.WriteLine("| Path | Format | Positions | Median µs | Allocated KB | Journal bytes |");
        Console.WriteLine("|---|---|---|---:|---:|---:|");
        foreach (var format in AccessRecordingFixture.Formats)
        {
            var bench = new Bench(format, AccessRecordingFixture.Positions);
            bench.Recovery();
            bench.Compaction();
        }
    }

    private sealed class Bench(string format, bool positions)
    {
        private readonly IServiceProvider _services = CreateServices();
        private readonly string _formatKey = format == "json" ? JsonLinesJournalFormat.JournalFormatKey : OrleansBinaryJournalFormat.JournalFormatKey;

        // A recovery of a journal whose execution recorded its reads, then the first write after it.
        public void Recovery()
        {
            var storage = new Storage(new VolatileJournalStorage(_formatKey));
            using (var first = Activate(storage))
            {
                WriteHistory(first);
                RecordReads(first);
            }

            var times = new List<double>();
            long allocated = 0;
            long written = 0;
            for (var i = 0; i < Iterations; i++)
            {
                var start = GC.GetTotalAllocatedBytes(precise: true);
                var timestamp = Stopwatch.GetTimestamp();
                using var activation = Activate(storage);
                times.Add(Stopwatch.GetElapsedTime(timestamp).TotalMicroseconds);
                allocated += GC.GetTotalAllocatedBytes(precise: true) - start;

                // What the first write after the recovery journals; a crash before it leaves the journal as it was.
                var bytes = storage.AppendedBytes;
                storage.Discard = true;
                activation.Manager.WriteStateAsync().AsTask().GetAwaiter().GetResult();
                storage.Discard = false;
                written += storage.AppendedBytes - bytes;
            }

            Report("Recovery", times, allocated, written);
        }

        // A snapshot after the execution recorded its reads: one which resolves them first, or, recording values, a plain one.
        public void Compaction()
        {
            var times = new List<double>();
            long allocated = 0;
            long written = 0;
            for (var i = 0; i < Iterations; i++)
            {
                var storage = new Storage(new VolatileJournalStorage(_formatKey));
                using var activation = Activate(storage);
                WriteHistory(activation);
                RecordReads(activation, complete: false);
                storage.RequestCompaction = true;
                for (var deferred = 0; deferred < JournaledStateManager.MaxDeferredSnapshots && activation.Manager.HasBarriers; deferred++)
                {
                    activation.Manager.WriteStateAsync().AsTask().GetAwaiter().GetResult();
                }

                var start = GC.GetTotalAllocatedBytes(precise: true);
                var timestamp = Stopwatch.GetTimestamp();
                activation.Manager.WriteStateAsync().AsTask().GetAwaiter().GetResult();
                times.Add(Stopwatch.GetElapsedTime(timestamp).TotalMicroseconds);
                allocated += GC.GetTotalAllocatedBytes(precise: true) - start;
                written += storage.ReplacedBytes;
                if (storage.Replaces != 1 || activation.Manager.HasBarriers)
                {
                    throw new InvalidOperationException("The snapshot didn't resolve the reads.");
                }
            }

            Report("Snapshot", times, allocated, written);
        }

        private void Report(string path, List<double> times, long allocated, long written)
        {
            times.Sort();
            Console.WriteLine($"| {path} | {format} | {(positions ? "on" : "off")} | {times[times.Count / 2]:0.0} | {allocated / Iterations / 1024.0:0.0} | {written / Iterations} |");
        }

        private void WriteHistory(Activation activation)
        {
            for (var write = 0; write < HistoryWrites; write++)
            {
                for (var i = 0; i < 8; i++)
                {
                    activation.Dictionary[$"key-{(write * 8 + i) % 64:D2}"] = $"value-{write:D2}-{i}-{new string('x', 21)}";
                }

                activation.Manager.WriteStateAsync().AsTask().GetAwaiter().GetResult();
            }
        }

        // An attempt reads distinct keys, writes once, and stages and journals its records; its execution keeps running.
        private void RecordReads(Activation activation, bool complete = false)
        {
            var log = activation.Runtime.RunAttempt(AccessRecordingFixture.TaskId, activation.AccessLogs, () =>
            {
                for (var i = 0; i < Reads; i++)
                {
                    _ = activation.Dictionary.TryGetValue($"key-{i:D2}", out _);
                }

                activation.Dictionary["key-00"] = "changed";
            });
            log.Flush();
            log.Close();
            if (complete)
            {
                activation.Logs.Remove(AccessRecordingFixture.TaskId);
            }

            activation.Manager.WriteStateAsync().AsTask().GetAwaiter().GetResult();
        }

        private Activation Activate(Storage storage)
        {
            var shared = new JournaledStateManagerShared(
                NullLogger<JournaledStateManager>.Instance,
                Options.Create(new JournaledStateManagerOptions { JournalFormatKey = _formatKey }),
                TimeProvider.System,
                _services);
            var serializer = _services.GetRequiredService<Serializer>();
            var runtime = new AccessRecordingRuntime(serializer);
            var manager = new JournaledStateManager(shared, storage) { ContextProvider = new DurableTaskAccessContextProvider(runtime) };
            var dictionary = new DurableDictionary<string, string>(
                "dict",
                manager,
                JournalFormatServices.GetRequiredCommandCodec<IDurableDictionaryCommandCodec<string, string>>(_services, _formatKey));
            var logs = new DurableTaskAccessLogs(
                manager,
                JournalFormatServices.GetRequiredCommandCodec<IDurableDictionaryCommandCodec<TaskId, byte[]>>(_services, _formatKey),
                positions ? serializer : null);
            manager.InitializeAsync().AsTask().GetAwaiter().GetResult();
            return new(manager, dictionary, logs, new AccessLogStorage(logs), runtime);
        }

        private static ServiceProvider CreateServices()
        {
            var services = new ServiceCollection();
            services.AddSerializer();
            services.AddSingleton<OrleansBinaryJournalFormat>();
            services.AddKeyedSingleton<IJournalFormat>(OrleansBinaryJournalFormat.JournalFormatKey, static (sp, _) => sp.GetRequiredService<OrleansBinaryJournalFormat>());
            services.AddKeyedSingleton(typeof(IDurableDictionaryCommandCodec<,>), OrleansBinaryJournalFormat.JournalFormatKey, typeof(OrleansBinaryDurableDictionaryCommandCodec<,>));
            services.Configure<JsonJournalOptions>(options => options.SerializerOptions = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() });
            services.AddJsonJournalFormat(tryAdd: false);
            return services.BuildServiceProvider();
        }
    }

    private sealed record Activation(
        JournaledStateManager Manager,
        DurableDictionary<string, string> Dictionary,
        DurableTaskAccessLogs Logs,
        AccessLogStorage AccessLogs,
        AccessRecordingRuntime Runtime) : IDisposable
    {
        public void Dispose() => Manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    // Requests compaction on demand, counts what writes store, and can discard a write without storing it.
    private sealed class Storage(VolatileJournalStorage inner) : IJournalStorage
    {
        public bool RequestCompaction { get; set; }

        public bool Discard { get; set; }

        public long AppendedBytes { get; private set; }

        public long ReplacedBytes { get; private set; }

        public int Replaces { get; private set; }

        public bool IsCompactionRequested => RequestCompaction || inner.IsCompactionRequested;

        public ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken) => inner.ReadAsync(consumer, cancellationToken);

        public ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
        {
            Replaces++;
            ReplacedBytes += value.Length;
            return Discard ? default : inner.ReplaceAsync(value, cancellationToken);
        }

        public ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
        {
            AppendedBytes += value.Length;
            return Discard ? default : inner.AppendAsync(value, cancellationToken);
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
    }
}
