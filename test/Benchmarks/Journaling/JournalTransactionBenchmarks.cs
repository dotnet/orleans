using System.Buffers;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Journaling;
using Orleans.Serialization;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Session;

namespace Benchmarks.Journaling;

/// <summary>
/// Measures what a transaction adds to a batch of journaled writes on the happy path: the same writes staged directly, in a
/// transaction which commits, and, for reference, in one which aborts and rebuilds its states from the journal.
/// </summary>
/// <remarks>
/// The manager has no activation, so the transaction runs inline: the figures exclude the exclusive section which an
/// activation adds. Each operation writes its entries to storage which discards them: the direct writes with a journal
/// write, and the transaction with the write which its commit awaits. The aborted transaction writes nothing.
/// </remarks>
[BenchmarkCategory("Journaling")]
[MemoryDiagnoser(displayGenColumns: false)]
public class JournalTransactionBenchmarks
{
    private static readonly string[] Keys = Enumerable.Range(0, 64).Select(static i => $"key-{i:D2}").ToArray();

    private JournaledStateManager _manager = null!;
    private DurableDictionary<string, string> _dictionary = null!;
    private DurableValue<string> _value = null!;
    private Func<CancellationToken, Task> _writes = null!;
    private Func<CancellationToken, Task<string?>> _read = null!;
    private Func<CancellationToken, Task> _abort = null!;

    [Params(1, 8, 64)]
    public int Writes { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        (_manager, _dictionary, _value, _) = CreateAsync().GetAwaiter().GetResult();
        _writes = _ =>
        {
            Write();
            return Task.CompletedTask;
        };
        _read = _ => Task.FromResult(_value.Value);
        _abort = _ =>
        {
            Write();
            throw new InvalidOperationException("abort");
        };
    }

    [GlobalCleanup]
    public void GlobalCleanup() => _manager.DisposeAsync().AsTask().GetAwaiter().GetResult();

    [Benchmark(Baseline = true)]
    public async Task Direct()
    {
        Write();
        await _manager.WriteStateAsync();
    }

    [Benchmark]
    public Task Committed() => _manager.RunTransactionAsync(_writes);

    [Benchmark]
    public Task<string?> ReadOnly() => _manager.RunTransactionAsync(_read);

    [Benchmark]
    public async Task Aborted()
    {
        try
        {
            await _manager.RunTransactionAsync(_abort);
        }
        catch (InvalidOperationException)
        {
        }

        _manager.JournalWriter.Reset();
    }

    /// <summary>Prints the journal bytes which the same writes stage directly and in a committed transaction.</summary>
    public static void ReportBytes()
    {
        var (manager, dictionary, value, storage) = CreateAsync().GetAwaiter().GetResult();
        foreach (var writes in new[] { 1, 8, 64 })
        {
            void Write()
            {
                for (var i = 0; i < writes; i++)
                {
                    dictionary[Keys[i]] = "value";
                }

                value.Value = "value";
            }

            Write();
            manager.WriteStateAsync().AsTask().GetAwaiter().GetResult();
            var direct = storage.LastAppendLength;
            manager.RunTransactionAsync(_ =>
            {
                Write();
                return Task.CompletedTask;
            }).GetAwaiter().GetResult();
            var transactional = storage.LastAppendLength;
            Console.WriteLine($"{writes,3} writes: direct {direct,6} B, transaction {transactional,6} B (+{transactional - direct} B)");
        }
    }

    private void Write()
    {
        for (var i = 0; i < Writes; i++)
        {
            _dictionary[Keys[i]] = "value";
        }

        _value.Value = "value";
    }

    private static async Task<(JournaledStateManager, DurableDictionary<string, string>, DurableValue<string>, DiscardingJournalStorage)> CreateAsync()
    {
        var serviceCollection = new ServiceCollection().AddSerializer();
        serviceCollection.AddSingleton<OrleansBinaryJournalFormat>();
        serviceCollection.AddKeyedSingleton<IJournalFormat>(OrleansBinaryJournalFormat.JournalFormatKey, static (sp, _) => sp.GetRequiredService<OrleansBinaryJournalFormat>());
        serviceCollection.AddKeyedSingleton(typeof(IDurableDictionaryCommandCodec<,>), OrleansBinaryJournalFormat.JournalFormatKey, typeof(OrleansBinaryDurableDictionaryCommandCodec<,>));
        var services = serviceCollection.BuildServiceProvider();
        var codecs = services.GetRequiredService<ICodecProvider>();
        var sessions = services.GetRequiredService<SerializerSessionPool>();
        var shared = new JournaledStateManagerShared(
            NullLogger<JournaledStateManager>.Instance,
            Options.Create(new JournaledStateManagerOptions { JournalFormatKey = OrleansBinaryJournalFormat.JournalFormatKey }),
            TimeProvider.System,
            services);
        var storage = new DiscardingJournalStorage();
        var manager = new JournaledStateManager(shared, storage);
        var dictionary = new DurableDictionary<string, string>(
            "dictionary",
            manager,
            new OrleansBinaryDurableDictionaryCommandCodec<string, string>(codecs.GetCodec<string>(), codecs.GetCodec<string>(), sessions));
        var value = new DurableValue<string>("value", manager, new OrleansBinaryDurableValueCommandCodec<string>(codecs.GetCodec<string>(), sessions));
        await manager.InitializeAsync();
        return (manager, dictionary, value, storage);
    }

    // Storage which holds nothing, so the benchmarks measure the manager rather than a growing store; an abort's rebuild
    // reads it as empty.
    private sealed class DiscardingJournalStorage : IJournalStorage
    {
        public long LastAppendLength { get; private set; }

        public bool IsCompactionRequested => false;

        public ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken) => default;

        public ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
        {
            LastAppendLength = value.Length;
            return default;
        }

        public ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken) => default;

        public ValueTask DeleteAsync(CancellationToken cancellationToken) => default;
    }
}
