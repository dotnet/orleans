using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Concurrency;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Serialization;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Session;
using Orleans.TestingHost;

namespace Benchmarks.Journaling;

/// <summary>
/// Measures transactions where they run: on a grain activation, against direct writes on the same activation, with commits
/// which share appends; and aborts, whose rebuild reads the journal's stored history.
/// </summary>
/// <remarks>
/// <para>
/// The activation cases run on one in-process silo, inside a grain method, timed by the grain: a transaction opens an
/// exclusive section through the activation's <c>IGrainRequestAdmission</c> component and returns
/// once the journal write which holds its commit has completed. Direct writes stage the same changes and await a journal
/// write. Storage is in memory and never compacts.
/// </para>
/// <para>
/// The abort cases run a manager without an activation, whose rebuild is the same: it reads storage, replays the held
/// state's stream, and replays the unwritten journal buffer. Storage adds a latency per call and a cost per MiB read,
/// spin-waited on a thread-pool thread because Windows timers tick at 15.6 ms, so the activation's scheduler stays free
/// while a write is in flight. Setup runs without latency.
/// </para>
/// <para>Figures are medians after one warm-up. Pass <c>quick</c> to run fewer and smaller cases.</para>
/// </remarks>
internal static class JournalTransactionCost
{
    private static readonly string[] Keys = Enumerable.Range(0, 1_024).Select(static i => $"key-{i:D4}").ToArray();

    private static readonly StorageLatency[] Latencies =
    [
        new("0", 0, 0),
        new("1 ms + 4 ms/MiB", 1, 4),
        new("10 ms + 10 ms/MiB", 10, 10),
    ];

    public static void Run(string[] args)
    {
        var quick = args.Contains("quick");
        Console.WriteLine($"Binary journal; {Environment.ProcessorCount} logical processors; {(quick ? "quick" : "full")} run.");
        RunOnActivationAsync(quick).GetAwaiter().GetResult();
        RunAbortsAsync(quick).GetAwaiter().GetResult();
    }

    private static async Task RunOnActivationAsync(bool quick)
    {
        var provider = new LatencyJournalStorageProvider();
        var builder = new InProcessTestClusterBuilder(1);
        builder.Options.ConfigureFileLogging = false;
        builder.ConfigureHost(host => host.Logging.SetMinimumLevel(LogLevel.Warning));
        builder.ConfigureSilo((_, silo) =>
        {
            silo.AddJournaling();
            silo.Services.Configure<JournaledStateManagerOptions>(options => options.JournalFormatKey = OrleansBinaryJournalFormat.JournalFormatKey);
            silo.Services.AddSingleton<IJournalStorageProvider>(provider);
        });

        var cluster = builder.Build();
        try
        {
            await cluster.DeployAsync();
            var grain = cluster.Client.GetGrain<ITransactionCostGrain>(0);
            var samples = quick ? 5 : 9;
            var operations = quick ? 200 : 1_000;

            Console.WriteLine();
            Console.WriteLine("### Commit on an activation against direct writes");
            Console.WriteLine();
            Console.WriteLine($"Each operation sets N keys of a dictionary and a value, then awaits a journal write: directly, or as a transaction which commits. µs per operation, timed in the grain over {operations:N0} operations; medians of {samples} samples. Storage without latency.");
            Console.WriteLine();
            Console.WriteLine("| Writes | Direct µs | Transaction µs | Transaction/direct | Appends per op direct | transaction |");
            Console.WriteLine("|---:|---:|---:|---:|---:|---:|");
            foreach (var writes in new[] { 1, 8, 64 })
            {
                var direct = await MeasureAsync(provider, samples, operations, () => grain.RunDirect(writes, operations));
                var committed = await MeasureAsync(provider, samples, operations, () => grain.RunCommitted(writes, operations));
                Console.WriteLine($"| {writes} | {direct.Micros:N1} | {committed.Micros:N1} | {committed.Micros / direct.Micros:0.00} | {direct.AppendsPerOperation:0.00} | {committed.AppendsPerOperation:0.00} |");
            }

            Console.WriteLine();
            Console.WriteLine("### Group commit: concurrent committers on a reentrant activation");
            Console.WriteLine();
            Console.WriteLine("Each round runs C transactions of 8 writes: one after another, each awaiting its own write, or concurrently, so commits which happen while a write is in flight share the next append. µs per transaction and appends per round; medians of the samples.");
            Console.WriteLine();
            Console.WriteLine("| Committers | Latency | Sequential µs/tx | Concurrent µs/tx | Concurrent/sequential | Appends per round sequential | concurrent |");
            Console.WriteLine("|---:|---|---:|---:|---:|---:|---:|");
            foreach (var latency in quick ? Latencies[..2] : Latencies)
            {
                provider.Latency = latency;
                var rounds = latency.MillisPerCall switch { 0 => quick ? 50 : 200, 1 => quick ? 10 : 30, _ => quick ? 3 : 6 };
                foreach (var committers in new[] { 1, 4, 16 })
                {
                    var transactions = rounds * committers;
                    var sequential = await MeasureAsync(provider, samples, transactions, () => grain.RunSequential(committers, 8, rounds));
                    var concurrent = await MeasureAsync(provider, samples, transactions, () => grain.RunConcurrent(committers, 8, rounds));
                    Console.WriteLine(
                        $"| {committers} | {latency.Name} | {sequential.Micros:N1} | {concurrent.Micros:N1} | {concurrent.Micros / sequential.Micros:0.00} | "
                        + $"{sequential.AppendsPerOperation * committers:0.00} | {concurrent.AppendsPerOperation * committers:0.00} |");
                }
            }

            provider.Latency = default;
        }
        finally
        {
            await cluster.DisposeAsync();
        }
    }

    // Runs one warm-up and the samples of a measurement which the grain times.
    private static async Task<(double Micros, double AppendsPerOperation)> MeasureAsync(
        LatencyJournalStorageProvider provider,
        int samples,
        int operations,
        Func<Task<long>> run)
    {
        await run();
        var micros = new List<double>(samples);
        var appends = new List<double>(samples);
        for (var i = 0; i < samples; i++)
        {
            var before = provider.Appends;
            var ticks = await run();
            micros.Add(ticks * 1_000_000.0 / Stopwatch.Frequency / operations);
            appends.Add((double)(provider.Appends - before) / operations);
        }

        return (Median(micros), Median(appends));
    }

    private static async Task RunAbortsAsync(bool quick)
    {
        var services = CreateServices();
        int[] histories = quick ? [0, 1_000] : [0, 1_000, 100_000];

        Console.WriteLine();
        Console.WriteLine("### Abort against stored history");
        Console.WriteLine();
        Console.WriteLine("The transaction sets one key of a dictionary whose stored history holds H sets of 1,024 keys, written 1,000 per append, then throws. The abort reads storage and rebuilds the dictionary from it; with a snapshot, storage holds the dictionary's 1,024 keys instead of their history. Commit: the same transaction committing, for reference. Medians of 5 runs (3 with latency) after one warm-up.");
        Console.WriteLine();
        Console.WriteLine("| History sets | Snapshot | Journal KiB | Latency | Abort ms | Commit ms | Abort/commit | MiB read per abort |");
        Console.WriteLine("|---:|---|---:|---|---:|---:|---:|---:|");
        foreach (var history in histories)
        {
            foreach (var snapshot in new[] { false, true })
            {
                if (history == 0 && snapshot)
                {
                    continue;
                }

                foreach (var latency in Latencies)
                {
                    var result = await MeasureAbortAsync(services, history, snapshot, latency);
                    Console.WriteLine(
                        $"| {history:N0} | {(snapshot ? "yes" : "no")} | {result.JournalBytes / 1024.0:N0} | {latency.Name} | {result.AbortMillis:N2} | {result.CommitMillis:N2} | "
                        + $"{result.AbortMillis / result.CommitMillis:0.0} | {result.MiBReadPerAbort:N2} |");
                }
            }
        }
    }

    private static async Task<(long JournalBytes, double AbortMillis, double CommitMillis, double MiBReadPerAbort)> MeasureAbortAsync(
        IServiceProvider services,
        int history,
        bool snapshot,
        StorageLatency latency)
    {
        var storage = new LatencyJournalStorage(new VolatileJournalStorage(OrleansBinaryJournalFormat.JournalFormatKey, NeverCompact));
        var (manager, dictionary) = await CreateManagerAsync(services, storage);
        try
        {
            for (var written = 0; written < history;)
            {
                for (var end = Math.Min(history, written + 1_000); written < end; written++)
                {
                    dictionary[Keys[written % Keys.Length]] = written;
                }

                await manager.WriteStateAsync();
            }

            if (snapshot)
            {
                storage.CompactionRequested = true;
                await manager.WriteStateAsync();
            }

            var runs = latency.MillisPerCall == 0 ? 5 : 3;
            storage.Latency = latency;
            var bytesRead = storage.BytesRead;
            var abort = await MedianMillisAsync(runs, async () =>
            {
                try
                {
                    await manager.RunTransactionAsync(_ =>
                    {
                        dictionary[Keys[0]] = -1;
                        throw new InvalidOperationException("abort");
                    });
                }
                catch (InvalidOperationException)
                {
                }
            });
            var mibRead = (storage.BytesRead - bytesRead) / 1048576.0 / (runs + 1);
            var commit = await MedianMillisAsync(runs, () => manager.RunTransactionAsync(_ =>
            {
                dictionary[Keys[0]] = 0;
                return Task.CompletedTask;
            }));
            storage.Latency = default;
            return (storage.StoredBytes, abort, commit, mibRead);
        }
        finally
        {
            await manager.DisposeAsync();
        }
    }

    private static async Task<double> MedianMillisAsync(int runs, Func<Task> operation)
    {
        await operation();
        var millis = new List<double>(runs);
        for (var i = 0; i < runs; i++)
        {
            var start = Stopwatch.GetTimestamp();
            await operation();
            millis.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        return Median(millis);
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        return values[values.Count / 2];
    }

    private static VolatileJournalStorageOptions NeverCompact => new() { MaxAppendsBeforeSnapshot = int.MaxValue, MaxBytesBeforeSnapshot = long.MaxValue };

    private static IServiceProvider CreateServices()
    {
        var serviceCollection = new ServiceCollection().AddSerializer();
        serviceCollection.AddSingleton<OrleansBinaryJournalFormat>();
        serviceCollection.AddKeyedSingleton<IJournalFormat>(OrleansBinaryJournalFormat.JournalFormatKey, static (sp, _) => sp.GetRequiredService<OrleansBinaryJournalFormat>());
        serviceCollection.AddKeyedSingleton(typeof(IDurableDictionaryCommandCodec<,>), OrleansBinaryJournalFormat.JournalFormatKey, typeof(OrleansBinaryDurableDictionaryCommandCodec<,>));
        return serviceCollection.BuildServiceProvider();
    }

    private static async Task<(JournaledStateManager Manager, DurableDictionary<string, int> Dictionary)> CreateManagerAsync(IServiceProvider services, IJournalStorage storage)
    {
        var codecs = services.GetRequiredService<ICodecProvider>();
        var sessions = services.GetRequiredService<SerializerSessionPool>();
        var shared = new JournaledStateManagerShared(
            NullLogger<JournaledStateManager>.Instance,
            Options.Create(new JournaledStateManagerOptions { JournalFormatKey = OrleansBinaryJournalFormat.JournalFormatKey }),
            TimeProvider.System,
            services);
        var manager = new JournaledStateManager(shared, storage);
        var dictionary = new DurableDictionary<string, int>(
            "dictionary",
            manager,
            new OrleansBinaryDurableDictionaryCommandCodec<string, int>(codecs.GetCodec<string>(), codecs.GetCodec<int>(), sessions));
        await manager.InitializeAsync();
        return (manager, dictionary);
    }

    private readonly record struct StorageLatency(string Name, double MillisPerCall, double MillisPerMiBRead);

    /// <summary>Creates in-memory storage with the latency which the measurement sets, and counts appends across journals.</summary>
    private sealed class LatencyJournalStorageProvider : IJournalStorageProvider
    {
        private readonly ConcurrentDictionary<JournalId, LatencyJournalStorage> _storage = new();
        private int _appends;

        public StorageLatency Latency { get; set; }

        public int Appends => Volatile.Read(ref _appends);

        public IJournalStorage CreateStorage(JournalId journalId) =>
            _storage.GetOrAdd(journalId, _ => new LatencyJournalStorage(new VolatileJournalStorage(OrleansBinaryJournalFormat.JournalFormatKey, NeverCompact), this));

        public void OnAppend() => Interlocked.Increment(ref _appends);
    }

    /// <summary>In-memory storage which adds a latency per call and a cost per MiB read, and requests compaction on demand.</summary>
    private sealed class LatencyJournalStorage(VolatileJournalStorage inner, LatencyJournalStorageProvider? provider = null) : IJournalStorage
    {
        private StorageLatency _latency;

        public StorageLatency Latency
        {
            get => provider?.Latency ?? _latency;
            set => _latency = value;
        }

        public bool CompactionRequested { get; set; }

        public long BytesRead { get; private set; }

        public long StoredBytes => inner.Segments.Sum(static segment => (long)segment.Length);

        public bool IsCompactionRequested => CompactionRequested;

        public async ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken)
        {
            var bytes = StoredBytes;
            await DelayAsync(Latency.MillisPerCall + Latency.MillisPerMiBRead * bytes / 1048576.0);
            BytesRead += bytes;
            await inner.ReadAsync(consumer, cancellationToken);
        }

        public async ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
        {
            await DelayAsync(Latency.MillisPerCall);
            await inner.AppendAsync(value, cancellationToken);
            provider?.OnAppend();
        }

        public async ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
        {
            await DelayAsync(Latency.MillisPerCall);
            CompactionRequested = false;
            await inner.ReplaceAsync(value, cancellationToken);
        }

        public ValueTask DeleteAsync(CancellationToken cancellationToken) => inner.DeleteAsync(cancellationToken);

        // Spins on a thread-pool thread: the timer's resolution on Windows is 15.6 ms, and the caller's scheduler stays free.
        private static async ValueTask DelayAsync(double millis)
        {
            if (millis <= 0)
            {
                return;
            }

            await Task.Run(() =>
            {
                var end = Stopwatch.GetTimestamp() + (long)(millis * Stopwatch.Frequency / 1000);
                while (Stopwatch.GetTimestamp() < end)
                {
                    Thread.SpinWait(64);
                }
            });
        }
    }
}

/// <summary>Runs and times journaled writes and transactions on its activation, for <see cref="JournalTransactionCost"/>.</summary>
public interface ITransactionCostGrain : IGrainWithIntegerKey
{
    /// <summary>Sets <paramref name="writes"/> keys and a value, then awaits a journal write, <paramref name="operations"/> times.</summary>
    /// <returns>The elapsed stopwatch ticks.</returns>
    Task<long> RunDirect(int writes, int operations);

    /// <summary>Runs <paramref name="operations"/> transactions which set <paramref name="writes"/> keys and a value.</summary>
    /// <returns>The elapsed stopwatch ticks.</returns>
    Task<long> RunCommitted(int writes, int operations);

    /// <summary>Runs <paramref name="rounds"/> rounds of <paramref name="committers"/> transactions, one after another.</summary>
    /// <returns>The elapsed stopwatch ticks.</returns>
    Task<long> RunSequential(int committers, int writes, int rounds);

    /// <summary>Runs <paramref name="rounds"/> rounds of <paramref name="committers"/> concurrent transactions.</summary>
    /// <returns>The elapsed stopwatch ticks.</returns>
    Task<long> RunConcurrent(int committers, int writes, int rounds);
}

/// <inheritdoc cref="ITransactionCostGrain"/>
[Reentrant]
public sealed class TransactionCostGrain(
    [FromKeyedServices("dictionary")] IDurableDictionary<string, int> dictionary,
    [FromKeyedServices("value")] IDurableValue<int> value) : DurableGrain, ITransactionCostGrain
{
    private static readonly string[] Keys = Enumerable.Range(0, 64).Select(static i => $"key-{i:D2}").ToArray();

    private JournaledStateManager Manager => (JournaledStateManager)StateManager;

    public async Task<long> RunDirect(int writes, int operations)
    {
        var start = Stopwatch.GetTimestamp();
        for (var i = 0; i < operations; i++)
        {
            Write(writes, i);
            await WriteStateAsync();
        }

        return Stopwatch.GetTimestamp() - start;
    }

    public async Task<long> RunCommitted(int writes, int operations)
    {
        var start = Stopwatch.GetTimestamp();
        for (var i = 0; i < operations; i++)
        {
            await Commit(writes, i);
        }

        return Stopwatch.GetTimestamp() - start;
    }

    public async Task<long> RunSequential(int committers, int writes, int rounds)
    {
        var start = Stopwatch.GetTimestamp();
        for (var round = 0; round < rounds; round++)
        {
            for (var committer = 0; committer < committers; committer++)
            {
                await Commit(writes, round);
            }
        }

        return Stopwatch.GetTimestamp() - start;
    }

    public async Task<long> RunConcurrent(int committers, int writes, int rounds)
    {
        var commits = new Task[committers];
        var start = Stopwatch.GetTimestamp();
        for (var round = 0; round < rounds; round++)
        {
            for (var committer = 0; committer < committers; committer++)
            {
                commits[committer] = Commit(writes, round);
            }

            await Task.WhenAll(commits);
        }

        return Stopwatch.GetTimestamp() - start;
    }

    private Task Commit(int writes, int iteration) => Manager.RunTransactionAsync(_ =>
    {
        Write(writes, iteration);
        return Task.CompletedTask;
    });

    private void Write(int writes, int iteration)
    {
        for (var i = 0; i < writes; i++)
        {
            dictionary[Keys[i]] = iteration;
        }

        value.Value = iteration;
    }
}
