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
/// Measures what the journaled state context costs a durable dictionary's reads: without a provider, as in grains which run
/// no durable tasks; with a provider and no current context, as in a durable task grain's other code; and through a context
/// which runs each access against the state, the mechanism which recording adds to.
/// </summary>
[BenchmarkCategory("Journaling")]
[MemoryDiagnoser(displayGenColumns: false)]
public class JournaledStateContextBenchmarks
{
    private const int Operations = 1_024;
    private static readonly AsyncLocal<JournaledStateContext?> Ambient = new();

    private string[] _keys = null!;
    private Dictionary<string, string> _plain = null!;
    private DurableDictionary<string, string> _withoutProvider = null!;
    private DurableDictionary<string, string> _withProvider = null!;
    private PassThroughContext _context = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        var serviceCollection = new ServiceCollection().AddSerializer();
        serviceCollection.AddSingleton<OrleansBinaryJournalFormat>();
        serviceCollection.AddKeyedSingleton<IJournalFormat>(OrleansBinaryJournalFormat.JournalFormatKey, static (sp, _) => sp.GetRequiredService<OrleansBinaryJournalFormat>());
        serviceCollection.AddKeyedSingleton(typeof(IDurableDictionaryCommandCodec<,>), OrleansBinaryJournalFormat.JournalFormatKey, typeof(OrleansBinaryDurableDictionaryCommandCodec<,>));
        var services = serviceCollection.BuildServiceProvider();
        var codecs = services.GetRequiredService<ICodecProvider>();
        var codec = new OrleansBinaryDurableDictionaryCommandCodec<string, string>(
            codecs.GetCodec<string>(),
            codecs.GetCodec<string>(),
            services.GetRequiredService<SerializerSessionPool>());
        _keys = Enumerable.Range(0, Operations).Select(i => $"key-{i:D4}-abcdefghijklmnopqrstuv").ToArray();
        _plain = _keys.ToDictionary(key => key, key => key);
        _withoutProvider = new("items", CreateManager(services), codec);
        var manager = CreateManager(services);
        manager.ContextProvider = new AmbientProvider();
        _withProvider = new("items", manager, codec);
        foreach (var key in _keys)
        {
            ((IDurableDictionaryCommandHandler<string, string>)_withoutProvider).ApplySet(key, key);
            ((IDurableDictionaryCommandHandler<string, string>)_withProvider).ApplySet(key, key);
        }

        _context = new PassThroughContext();
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = Operations)]
    public int PlainDictionary()
    {
        var found = 0;
        foreach (var key in _keys)
        {
            found += _plain.TryGetValue(key, out _) ? 1 : 0;
        }

        return found;
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    public int NoProvider() => ReadAll(_withoutProvider);

    [Benchmark(OperationsPerInvoke = Operations)]
    public int ProviderWithoutContext()
    {
        Ambient.Value = null;
        return ReadAll(_withProvider);
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    public int ThroughContext()
    {
        Ambient.Value = _context;
        var result = ReadAll(_withProvider);
        Ambient.Value = null;
        return result;
    }

    private int ReadAll(DurableDictionary<string, string> dictionary)
    {
        var found = 0;
        foreach (var key in _keys)
        {
            found += dictionary.TryGetValue(key, out _) ? 1 : 0;
        }

        return found;
    }

    private static JournaledStateManager CreateManager(IServiceProvider services)
    {
        var shared = new JournaledStateManagerShared(
            NullLogger<JournaledStateManager>.Instance,
            Options.Create(new JournaledStateManagerOptions { JournalFormatKey = OrleansBinaryJournalFormat.JournalFormatKey }),
            TimeProvider.System,
            services);
        return new JournaledStateManager(shared, new VolatileJournalStorage());
    }

    // Reads an asynchronous local, as the durable task runtime's provider reads the running attempt.
    private sealed class AmbientProvider : JournaledStateContextProvider
    {
        public override JournaledStateContext? Current => Ambient.Value;
    }

    private sealed class PassThroughContext : JournaledStateContext
    {
        protected override TResult Access<TState, TResult>(in JournaledStateAccess access, TState state, Func<TState, TResult> operation) =>
            operation(state);
    }
}
