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

/// <summary>The bodies which <see cref="AccessRecordingBenchmarks"/> and <see cref="AccessRecordingBytes"/> run.</summary>
internal enum AccessRecordingScenario
{
    ReadDistinct,
    ReadSameKey,
    ReadValue,
    ReadAWriteB,
    WriteValue,
    WriteDictionary,
    Keys,
    TypicalBody,
}

internal static class AccessRecordingScenarios
{
    public const int KeysReads = 64;

    public static Func<AccessRecordingFixture, int> Get(AccessRecordingScenario scenario) => scenario switch
    {
        AccessRecordingScenario.ReadDistinct => ReadDistinct,
        AccessRecordingScenario.ReadSameKey => ReadSameKey,
        AccessRecordingScenario.ReadValue => ReadValue,
        AccessRecordingScenario.ReadAWriteB => ReadAWriteB,
        AccessRecordingScenario.WriteValue => WriteValue,
        AccessRecordingScenario.WriteDictionary => WriteDictionary,
        AccessRecordingScenario.Keys => Keys,
        AccessRecordingScenario.TypicalBody => TypicalBody,
        _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
    };

    /// <summary><see cref="AccessRecordingBenchmarks.Ops"/> reads of different keys, in turn.</summary>
    public static int ReadDistinct(AccessRecordingFixture f)
    {
        var dictionary = f.Dictionary;
        var keys = f.Keys;
        var n = 0;
        for (var i = 0; i < AccessRecordingBenchmarks.Ops; i++)
        {
            n += dictionary.TryGetValue(keys[i & 63], out var value) ? value.Length : 0;
        }

        return n;
    }

    /// <summary><see cref="AccessRecordingBenchmarks.Ops"/> reads of one key of an unchanged dictionary.</summary>
    public static int ReadSameKey(AccessRecordingFixture f)
    {
        var dictionary = f.Dictionary;
        var key = f.Keys[0];
        var n = 0;
        for (var i = 0; i < AccessRecordingBenchmarks.Ops; i++)
        {
            n += dictionary.TryGetValue(key, out var value) ? value.Length : 0;
        }

        return n;
    }

    /// <summary><see cref="AccessRecordingBenchmarks.Ops"/> reads of an unchanged value.</summary>
    public static int ReadValue(AccessRecordingFixture f)
    {
        var state = f.Value;
        var n = 0;
        for (var i = 0; i < AccessRecordingBenchmarks.Ops; i++)
        {
            n += state.Value!.Length;
        }

        return n;
    }

    /// <summary>Iterations which each read one key and write another key of the same dictionary.</summary>
    public static int ReadAWriteB(AccessRecordingFixture f)
    {
        var dictionary = f.Dictionary;
        var read = f.Keys[0];
        var written = f.Keys[1];
        var values = f.Values;
        var n = 0;
        for (var i = 0; i < AccessRecordingBenchmarks.Ops; i++)
        {
            n += dictionary.TryGetValue(read, out var value) ? value.Length : 0;
            dictionary[written] = values[i & 63];
        }

        return n;
    }

    public static int WriteValue(AccessRecordingFixture f)
    {
        var state = f.Value;
        var values = f.Values;
        for (var i = 0; i < AccessRecordingBenchmarks.Ops; i++)
        {
            state.Value = values[i & 63];
        }

        return 0;
    }

    public static int WriteDictionary(AccessRecordingFixture f)
    {
        var dictionary = f.Dictionary;
        var keys = f.Keys;
        var values = f.Values;
        for (var i = 0; i < AccessRecordingBenchmarks.Ops; i++)
        {
            dictionary[keys[(i & 31) + 32]] = values[i & 63];
        }

        return 0;
    }

    /// <summary><see cref="KeysReads"/> reads of the keys of an unchanged dictionary.</summary>
    public static int Keys(AccessRecordingFixture f)
    {
        var dictionary = f.Dictionary;
        var n = 0;
        for (var i = 0; i < KeysReads; i++)
        {
            n += dictionary.Keys.Count;
        }

        return n;
    }

    /// <summary>A typical step: three reads, three writes and a count.</summary>
    public static int TypicalBody(AccessRecordingFixture f)
    {
        var dictionary = f.Dictionary;
        var keys = f.Keys;
        var values = f.Values;
        var n = 0;
        for (var i = 0; i < 3; i++)
        {
            n += dictionary.TryGetValue(keys[i], out var value) ? value.Length : 0;
            dictionary[keys[i + 32]] = values[i];
        }

        return n + dictionary.Count;
    }
}

/// <summary>
/// A journaled state manager with a dictionary, a value and the durable task access logs, whose accesses run through the
/// access log of the attempt which <see cref="Provider"/> holds, as a durable task grain's do.
/// </summary>
internal sealed class AccessRecordingFixture : IDisposable
{
    private readonly TaskId _taskId = TaskId.CreateRoot("bench");
    private readonly Serializer _serializer;
    private readonly AccessLogProvider _provider = new();
    private readonly DurableTaskAccessLogs _logs;
    private readonly AccessLogStorage _storage;
    private readonly JournalBufferWriter _stateJournal;
    private readonly JournalBufferWriter _accessJournal;
    private readonly JournalStreamWriter _accessStream;
    private readonly IJournalFormat _journalFormat;
    private readonly IDurableDictionaryCommandCodec<TaskId, byte[]> _accessCodec;
    private readonly JournaledStateManager _manager;
    private readonly Dictionary<AccessRecordingScenario, AccessLogStorage> _recorded = [];

    public AccessRecordingFixture(string format)
    {
        var formatKey = format == "json" ? JsonLinesJournalFormat.JournalFormatKey : OrleansBinaryJournalFormat.JournalFormatKey;
        var services = new ServiceCollection();
        services.AddSerializer();
        services.AddSingleton<OrleansBinaryJournalFormat>();
        services.AddKeyedSingleton<IJournalFormat>(OrleansBinaryJournalFormat.JournalFormatKey, static (sp, _) => sp.GetRequiredService<OrleansBinaryJournalFormat>());
        services.AddKeyedSingleton(typeof(IDurableDictionaryCommandCodec<,>), OrleansBinaryJournalFormat.JournalFormatKey, typeof(OrleansBinaryDurableDictionaryCommandCodec<,>));
        services.AddKeyedSingleton(typeof(IDurableValueCommandCodec<>), OrleansBinaryJournalFormat.JournalFormatKey, typeof(OrleansBinaryDurableValueCommandCodec<>));
        services.Configure<JsonJournalOptions>(options => options.SerializerOptions = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() });
        services.AddJsonJournalFormat(tryAdd: false);
        var serviceProvider = services.BuildServiceProvider();
        _serializer = serviceProvider.GetRequiredService<Serializer>();
        _journalFormat = serviceProvider.GetRequiredKeyedService<IJournalFormat>(formatKey);
        _accessCodec = JournalFormatServices.GetRequiredCommandCodec<IDurableDictionaryCommandCodec<TaskId, byte[]>>(serviceProvider, formatKey);

        var shared = new JournaledStateManagerShared(
            NullLogger<JournaledStateManager>.Instance,
            Options.Create(new JournaledStateManagerOptions { JournalFormatKey = formatKey }),
            TimeProvider.System,
            serviceProvider);
        _manager = new JournaledStateManager(shared, new VolatileJournalStorage(formatKey)) { ContextProvider = _provider };
        Dictionary = new DurableDictionary<string, string>(
            "dict",
            _manager,
            JournalFormatServices.GetRequiredCommandCodec<IDurableDictionaryCommandCodec<string, string>>(serviceProvider, formatKey));
        Value = new DurableValue<string>(
            "value",
            _manager,
            JournalFormatServices.GetRequiredCommandCodec<IDurableValueCommandCodec<string>>(serviceProvider, formatKey));
        _logs = new DurableTaskAccessLogs(_manager, _accessCodec);
        _storage = new(_logs);

        // The states journal into buffers which each invocation resets, rather than through the manager's writes.
        _stateJournal = _journalFormat.CreateWriter();
        _accessJournal = _journalFormat.CreateWriter();
        _accessStream = _accessJournal.CreateJournalStreamWriter(new JournalStreamId(3));
        ((IStateMachine)Dictionary).Reset(_stateJournal.CreateJournalStreamWriter(new JournalStreamId(1)));
        ((IStateMachine)Value).Reset(_stateJournal.CreateJournalStreamWriter(new JournalStreamId(2)));
        ((IStateMachine)_logs).Reset(_accessStream);

        Keys = [.. Enumerable.Range(0, 64).Select(i => $"key-{i:D2}")];
        Values = [.. Enumerable.Range(0, 64).Select(i => $"value-{i:D2}-{new string('x', 23)}")];
        foreach (var (key, value) in Keys.Zip(Values))
        {
            ((IDurableDictionaryCommandHandler<string, string>)Dictionary).ApplySet(key, value);
        }

        ((IDurableValueCommandHandler<string>)Value).ApplySet(Values[0]);

        // Record each scenario once, so a replay consumes exactly its records, and check that it does.
        foreach (var scenario in Enum.GetValues<AccessRecordingScenario>())
        {
            var logs = new DurableTaskAccessLogs(new NullManager(), _accessCodec);
            var storage = new AccessLogStorage(logs);
            var log = CreateLog(storage);
            RunInAttempt(log, AccessRecordingScenarios.Get(scenario));
            log.Flush();
            log.Close();
            _recorded[scenario] = storage;

            var replay = CreateLog(storage);
            RunInAttempt(replay, AccessRecordingScenarios.Get(scenario));
            if (replay.IsReplaying)
            {
                throw new InvalidOperationException($"The replay of {scenario} didn't consume its records.");
            }

            var length = storage.GetAccessLogLength(_taskId);
            replay.Flush();
            if (storage.GetAccessLogLength(_taskId) != length)
            {
                throw new InvalidOperationException($"The replay of {scenario} recorded further accesses.");
            }
        }

        ResetJournals();
    }

    public static IEnumerable<string> Formats => (Environment.GetEnvironmentVariable("BENCH_FORMATS") ?? "binary,json").Split(',');

    public string[] Keys { get; }

    public string[] Values { get; }

    public DurableDictionary<string, string> Dictionary { get; }

    public DurableValue<string> Value { get; }

    /// <summary>Runs <paramref name="body"/> in one recording attempt, then stages, journals and removes its records.</summary>
    public int Record(Func<AccessRecordingFixture, int> body)
    {
        var log = CreateLog(_storage);
        var result = RunInAttempt(log, body);
        log.Flush();
        log.Close();
        JournalAndComplete();
        ResetJournals();
        return result;
    }

    /// <summary>Runs <paramref name="count"/> bodies, each in an attempt which journals and completes.</summary>
    public int RecordBodies(Func<AccessRecordingFixture, int> body, int count)
    {
        var result = 0;
        for (var i = 0; i < count; i++)
        {
            var log = CreateLog(_storage);
            result += RunInAttempt(log, body);
            log.Flush();
            log.Close();
            JournalAndComplete();
        }

        ResetJournals();
        return result;
    }

    /// <summary>Runs the body of <paramref name="scenario"/> in an attempt which replays its records.</summary>
    public int Replay(AccessRecordingScenario scenario)
    {
        var log = CreateLog(_recorded[scenario]);
        var result = RunInAttempt(log, AccessRecordingScenarios.Get(scenario));
        log.Close();
        return result;
    }

    /// <summary>Replays the body of <paramref name="scenario"/> <paramref name="count"/> times.</summary>
    public int ReplayBodies(AccessRecordingScenario scenario, int count)
    {
        var body = AccessRecordingScenarios.Get(scenario);
        var storage = _recorded[scenario];
        var result = 0;
        for (var i = 0; i < count; i++)
        {
            var log = CreateLog(storage);
            result += RunInAttempt(log, body);
            log.Close();
        }

        return result;
    }

    /// <summary>
    /// Runs <paramref name="body"/> in a recording attempt and returns the bytes which journaling its records takes, and
    /// the bytes of the removal which completing it journals.
    /// </summary>
    public (int Records, int Removal) MeasureBytes(Func<AccessRecordingFixture, int> body)
    {
        var log = CreateLog(_storage);
        RunInAttempt(log, body);
        log.Flush();
        log.Close();
        ResetJournals();
        ((IStateMachine)_logs).WritePendingEntries(_accessStream);
        var records = _accessJournal.CommittedLength;
        _logs.Remove(_taskId);
        ((IStateMachine)_logs).WritePendingEntries(_accessStream);
        var removal = _accessJournal.CommittedLength - records;
        ResetJournals();
        return (records, removal);
    }

    public void ResetJournals()
    {
        _stateJournal.Reset();
        _accessJournal.Reset();
    }

    public void Dispose()
    {
        _stateJournal.Dispose();
        _accessJournal.Dispose();
    }

    private void JournalAndComplete()
    {
        ((IStateMachine)_logs).WritePendingEntries(_accessStream);
        _logs.Remove(_taskId);
        ((IStateMachine)_logs).WritePendingEntries(_accessStream);
    }

    private DurableTaskAccessLog CreateLog(AccessLogStorage storage) =>
        new(new BenchmarkAttempt(_taskId), storage, _serializer, TaskScheduler.Current, static exception => exception);

    private int RunInAttempt(DurableTaskAccessLog log, Func<AccessRecordingFixture, int> body)
    {
        _provider.Log = log;
        try
        {
            return body(this);
        }
        finally
        {
            _provider.Log = null;
        }
    }

    // Supplies the attempt's log while it records, as the durable task runtime's provider does.
    private sealed class AccessLogProvider : JournaledStateContextProvider
    {
        public DurableTaskAccessLog? Log;

        public override JournaledStateContext? Current => Log is { IsActive: true } log ? log : null;
    }

    private sealed class NullManager : IJournaledStateManager
    {
        public ValueTask InitializeAsync(CancellationToken cancellationToken) => default;

        public void RegisterStateMachine(string name, IStateMachine state) { }

        public bool TryGetStateMachine(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IStateMachine? stateMachine)
        {
            stateMachine = null;
            return false;
        }

        public ValueTask WriteStateAsync(CancellationToken cancellationToken) => default;

        public ValueTask DeleteStateAsync(CancellationToken cancellationToken) => default;

        public ValueTask DisposeAsync() => default;
    }
}
