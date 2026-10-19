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
/// The manager has no activation, so the transaction runs inline: the figures exclude the exclusive section or request
/// which an activation adds. Each operation stages its entries and then clears the journal buffer, so no write runs.
/// </remarks>
[BenchmarkCategory("Journaling")]
[MemoryDiagnoser(displayGenColumns: false)]
public class JournalTransactionBenchmarks
{
    private static readonly string[] Keys = Enumerable.Range(0, 64).Select(static i => $"key-{i:D2}").ToArray();

    private JournaledStateManager _manager = null!;
    private DurableDictionary<string, string> _dictionary = null!;
    private DurableValue<string> _value = null!;
    private uint _valueStream;
    private Func<CancellationToken, Task> _writes = null!;
    private Func<CancellationToken, Task<string?>> _read = null!;
    private Func<CancellationToken, Task> _abort = null!;

    [Params(1, 8, 64)]
    public int Writes { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        (_manager, _dictionary, _value) = CreateAsync().GetAwaiter().GetResult();
        _valueStream = GetStream(_manager);
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
    public void Direct()
    {
        Write();

        // A write captures the value's pending change, as the transaction's commit does.
        Stage(_manager, _value, _valueStream);
        _manager.JournalWriter.Reset();
    }

    [Benchmark]
    public async Task Committed()
    {
        await _manager.RunTransactionAsync(_writes);
        _manager.JournalWriter.Reset();
    }

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
        var (manager, dictionary, value) = CreateAsync().GetAwaiter().GetResult();
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

            manager.JournalWriter.Reset();
            Write();
            Stage(manager, value, GetStream(manager));
            var direct = manager.JournalWriter.CommittedLength;
            manager.JournalWriter.Reset();
            manager.RunTransactionAsync(_ =>
            {
                Write();
                return Task.CompletedTask;
            }).GetAwaiter().GetResult();
            var transactional = manager.JournalWriter.CommittedLength;
            manager.JournalWriter.Reset();
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

    private static uint GetStream(JournaledStateManager manager) => manager.GetTransactionalState(new JournaledStateAccessTarget("value", manager)).StreamId;

    // Journals the value's pending change, as a write's capture does.
    private static void Stage(JournaledStateManager manager, DurableValue<string> value, uint stream) =>
        ((ICoalescingJournaledState)value).StagePendingChanges(manager.JournalWriter.CreateJournalStreamWriter(new(stream)));

    private static async Task<(JournaledStateManager, DurableDictionary<string, string>, DurableValue<string>)> CreateAsync()
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
        var manager = new JournaledStateManager(shared, new VolatileJournalStorage(OrleansBinaryJournalFormat.JournalFormatKey));
        var dictionary = new DurableDictionary<string, string>(
            "dictionary",
            manager,
            new OrleansBinaryDurableDictionaryCommandCodec<string, string>(codecs.GetCodec<string>(), codecs.GetCodec<string>(), sessions));
        var value = new DurableValue<string>("value", manager, new OrleansBinaryDurableValueCommandCodec<string>(codecs.GetCodec<string>(), sessions));
        await manager.InitializeAsync();
        return (manager, dictionary, value);
    }
}
