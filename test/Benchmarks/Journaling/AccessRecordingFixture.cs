using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
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
    ReadOnce,
    ReadDistinct,
    ReadSameKey,
    ReadAlternating,
    ReadValue,
    ReadAWriteB,
    WriteValue,
    WriteDictionary,
    Keys,
    TypicalBody,
}

/// <summary>The values which the fixture's states hold.</summary>
public enum AccessRecordingValueKind
{
    /// <summary>
    /// 32-character strings: the access log records a read which repeats the attempt's latest read of the same state and
    /// operation with an equal result as a reference to it.
    /// </summary>
    String,

    /// <summary>
    /// Instances of <see cref="AccessRecordingItem"/>, a record class with the same string and a number: the access log
    /// records every read with its value, since code can change a class instance in place.
    /// </summary>
    Record,
}

/// <summary>A class value, as grains commonly keep in journaled state.</summary>
[GenerateSerializer]
public sealed record AccessRecordingItem([property: Id(0)] string Name, [property: Id(1)] int Quantity);

/// <summary>
/// The bodies of <see cref="AccessRecordingScenario"/> for each kind of value, as exact-typed delegates into which the JIT
/// inlines <see cref="AccessRecordingScenarios{TValue}"/>, so their accesses compile as a grain's code does rather than as
/// generic code shared between reference types, which adds lookups of its own to each access.
/// </summary>
internal static class AccessRecordingBodies
{
    public static Func<AccessRecordingFixture<string>, int> OfStrings(AccessRecordingScenario scenario) => scenario switch
    {
        AccessRecordingScenario.ReadOnce => static f => AccessRecordingScenarios<string>.ReadOnce(f),
        AccessRecordingScenario.ReadDistinct => static f => AccessRecordingScenarios<string>.ReadDistinct(f),
        AccessRecordingScenario.ReadSameKey => static f => AccessRecordingScenarios<string>.ReadSameKey(f),
        AccessRecordingScenario.ReadAlternating => static f => AccessRecordingScenarios<string>.ReadAlternating(f),
        AccessRecordingScenario.ReadValue => static f => AccessRecordingScenarios<string>.ReadValue(f),
        AccessRecordingScenario.ReadAWriteB => static f => AccessRecordingScenarios<string>.ReadAWriteB(f),
        AccessRecordingScenario.WriteValue => static f => AccessRecordingScenarios<string>.WriteValue(f),
        AccessRecordingScenario.WriteDictionary => static f => AccessRecordingScenarios<string>.WriteDictionary(f),
        AccessRecordingScenario.Keys => static f => AccessRecordingScenarios<string>.Keys(f),
        AccessRecordingScenario.TypicalBody => static f => AccessRecordingScenarios<string>.TypicalBody(f),
        _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
    };

    public static Func<AccessRecordingFixture<AccessRecordingItem>, int> OfRecords(AccessRecordingScenario scenario) => scenario switch
    {
        AccessRecordingScenario.ReadOnce => static f => AccessRecordingScenarios<AccessRecordingItem>.ReadOnce(f),
        AccessRecordingScenario.ReadDistinct => static f => AccessRecordingScenarios<AccessRecordingItem>.ReadDistinct(f),
        AccessRecordingScenario.ReadSameKey => static f => AccessRecordingScenarios<AccessRecordingItem>.ReadSameKey(f),
        AccessRecordingScenario.ReadAlternating => static f => AccessRecordingScenarios<AccessRecordingItem>.ReadAlternating(f),
        AccessRecordingScenario.ReadValue => static f => AccessRecordingScenarios<AccessRecordingItem>.ReadValue(f),
        AccessRecordingScenario.ReadAWriteB => static f => AccessRecordingScenarios<AccessRecordingItem>.ReadAWriteB(f),
        AccessRecordingScenario.WriteValue => static f => AccessRecordingScenarios<AccessRecordingItem>.WriteValue(f),
        AccessRecordingScenario.WriteDictionary => static f => AccessRecordingScenarios<AccessRecordingItem>.WriteDictionary(f),
        AccessRecordingScenario.Keys => static f => AccessRecordingScenarios<AccessRecordingItem>.Keys(f),
        AccessRecordingScenario.TypicalBody => static f => AccessRecordingScenarios<AccessRecordingItem>.TypicalBody(f),
        _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
    };
}

internal static class AccessRecordingScenarios<TValue> where TValue : class
{
    /// <summary>One read of a key.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadOnce(AccessRecordingFixture<TValue> f) => f.Dictionary.TryGetValue(f.Keys[0], out _) ? 1 : 0;

    /// <summary><see cref="AccessRecordingBenchmarks.Ops"/> reads of different keys, in turn.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadDistinct(AccessRecordingFixture<TValue> f)
    {
        var dictionary = f.Dictionary;
        var keys = f.Keys;
        var n = 0;
        for (var i = 0; i < AccessRecordingBenchmarks.Ops; i++)
        {
            n += dictionary.TryGetValue(keys[i & 63], out _) ? 1 : 0;
        }

        return n;
    }

    /// <summary><see cref="AccessRecordingBenchmarks.Ops"/> reads of one key of an unchanged dictionary.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadSameKey(AccessRecordingFixture<TValue> f)
    {
        var dictionary = f.Dictionary;
        var key = f.Keys[0];
        var n = 0;
        for (var i = 0; i < AccessRecordingBenchmarks.Ops; i++)
        {
            n += dictionary.TryGetValue(key, out _) ? 1 : 0;
        }

        return n;
    }

    /// <summary><see cref="AccessRecordingBenchmarks.Ops"/> reads of an unchanged dictionary, alternating between two keys.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadAlternating(AccessRecordingFixture<TValue> f)
    {
        var dictionary = f.Dictionary;
        var keys = f.Keys;
        var n = 0;
        for (var i = 0; i < AccessRecordingBenchmarks.Ops; i++)
        {
            n += dictionary.TryGetValue(keys[i & 1], out _) ? 1 : 0;
        }

        return n;
    }

    /// <summary><see cref="AccessRecordingBenchmarks.Ops"/> reads of an unchanged value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadValue(AccessRecordingFixture<TValue> f)
    {
        var state = f.Value;
        var n = 0;
        for (var i = 0; i < AccessRecordingBenchmarks.Ops; i++)
        {
            n += state.Value is null ? 0 : 1;
        }

        return n;
    }

    /// <summary>Iterations which each read one key and write another key of the same dictionary.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadAWriteB(AccessRecordingFixture<TValue> f)
    {
        var dictionary = f.Dictionary;
        var read = f.Keys[0];
        var written = f.Keys[1];
        var values = f.Values;
        var n = 0;
        for (var i = 0; i < AccessRecordingBenchmarks.Ops; i++)
        {
            n += dictionary.TryGetValue(read, out _) ? 1 : 0;
            dictionary[written] = values[i & 63];
        }

        return n;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int WriteValue(AccessRecordingFixture<TValue> f)
    {
        var state = f.Value;
        var values = f.Values;
        for (var i = 0; i < AccessRecordingBenchmarks.Ops; i++)
        {
            state.Value = values[i & 63];
        }

        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int WriteDictionary(AccessRecordingFixture<TValue> f)
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

    /// <summary><see cref="AccessRecordingBenchmarks.KeysReads"/> reads of the keys of an unchanged dictionary.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Keys(AccessRecordingFixture<TValue> f)
    {
        var dictionary = f.Dictionary;
        var n = 0;
        for (var i = 0; i < AccessRecordingBenchmarks.KeysReads; i++)
        {
            n += dictionary.Keys.Count;
        }

        return n;
    }

    /// <summary>A typical step: three reads, three writes and a count.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int TypicalBody(AccessRecordingFixture<TValue> f)
    {
        var dictionary = f.Dictionary;
        var keys = f.Keys;
        var values = f.Values;
        var n = 0;
        for (var i = 0; i < 3; i++)
        {
            n += dictionary.TryGetValue(keys[i], out _) ? 1 : 0;
            dictionary[keys[i + 32]] = values[i];
        }

        return n + dictionary.Count;
    }
}

/// <summary>
/// Runs the bodies of <see cref="AccessRecordingScenario"/> over a journaled state manager with a dictionary of 64 entries,
/// a value and the durable task access logs: outside a durable task, or as attempts of a durable task which the runtime's
/// context provider routes the accesses of, as a durable task grain does.
/// </summary>
/// <remarks>
/// <para>
/// Each attempt is a <see cref="GrainDurableTaskContext"/> of <see cref="TaskId"/>, a task identifier of the depth and form
/// which the runtime gives a step of a child of an invoked task, and runs its body through
/// <see cref="DurableTaskRuntimeHelper.RunAsync"/>, which makes the attempt current, so
/// <see cref="DurableTaskAccessContextProvider"/> finds the attempt's access log as it does in a grain.
/// </para>
/// <para>
/// Pure reads record journal positions unless <c>BENCH_POSITIONS</c> is <c>off</c>, which records values as P2 does.
/// Replays always consume value records: a recovery resolves positions into values before any attempt replays.
/// </para>
/// </remarks>
internal abstract class AccessRecordingFixture : IDisposable
{
    /// <summary>
    /// The task whose attempts run the bodies: a step of the first child of an invoked task with a generated identifier.
    /// </summary>
    public static TaskId TaskId { get; } = TaskId.CreateRoot("0f8fad5bd9cb469fa16570867728950e").Child("$child-1").Child("reserve-stock-1");

    public static IEnumerable<string> Formats => (Environment.GetEnvironmentVariable("BENCH_FORMATS") ?? "binary,json").Split(',');

    /// <summary>Gets whether pure reads record journal positions.</summary>
    public static bool Positions => Environment.GetEnvironmentVariable("BENCH_POSITIONS") is not "off";

    public static AccessRecordingFixture Create(string format, AccessRecordingValueKind valueKind) => valueKind switch
    {
        AccessRecordingValueKind.String => new AccessRecordingFixture<string>(format, AccessRecordingBodies.OfStrings, static i => $"value-{i:D2}-{new string('x', 23)}"),
        AccessRecordingValueKind.Record => new AccessRecordingFixture<AccessRecordingItem>(format, AccessRecordingBodies.OfRecords, static i => new($"value-{i:D2}-{new string('x', 23)}", i)),
        _ => throw new ArgumentOutOfRangeException(nameof(valueKind)),
    };

    /// <summary>Runs the body of <paramref name="scenario"/> <paramref name="count"/> times outside a durable task.</summary>
    public abstract int RunOutside(AccessRecordingScenario scenario, int count = 1);

    /// <summary>Runs the body of <paramref name="scenario"/> <paramref name="count"/> times, each in an attempt which has no access log.</summary>
    public abstract int RunUnrecorded(AccessRecordingScenario scenario, int count = 1);

    /// <summary>
    /// Runs the body of <paramref name="scenario"/> <paramref name="count"/> times, each in a recording attempt which then
    /// stages, journals and removes its records, as completing it does.
    /// </summary>
    public abstract int Record(AccessRecordingScenario scenario, int count = 1);

    /// <summary>Runs the body of <paramref name="scenario"/> <paramref name="count"/> times, each in an attempt which replays its records.</summary>
    public abstract int Replay(AccessRecordingScenario scenario, int count = 1);

    /// <summary>
    /// Runs the body of <paramref name="scenario"/> in a recording attempt and returns the bytes which journaling its
    /// records takes, and the bytes of the removal which completing it journals.
    /// </summary>
    public abstract (int Records, int Removal) MeasureBytes(AccessRecordingScenario scenario);

    /// <summary>Returns the bytes which journaling a barrier's set and its release takes.</summary>
    public abstract int MeasureBarrierBytes();

    public abstract void Dispose();
}

internal sealed class AccessRecordingFixture<TValue> : AccessRecordingFixture where TValue : class
{
    private readonly AccessRecordingRuntime _runtime;
    private readonly DurableTaskAccessLogs _logs;
    private readonly AccessLogStorage _storage;
    private readonly JournalBufferWriter _stateJournal;
    private readonly JournalBufferWriter _accessJournal;
    private readonly JournalStreamWriter _accessStream;
    private readonly Func<AccessRecordingFixture<TValue>, int>[] _bodies;
    private readonly AccessLogStorage[] _recorded;
    private readonly BodyTask _body;
    private readonly IJournalFormat _journalFormat;
    private readonly Func<IDurableDictionaryCommandCodec<string, string>> _barrierCodecFactory;

    public AccessRecordingFixture(string format, Func<AccessRecordingScenario, Func<AccessRecordingFixture<TValue>, int>> getBody, Func<int, TValue> createValue)
    {
        var formatKey = format == "json" ? JsonLinesJournalFormat.JournalFormatKey : OrleansBinaryJournalFormat.JournalFormatKey;
        var services = new ServiceCollection();
        services.AddSerializer(serializer => serializer.AddAssembly(typeof(AccessRecordingItem).Assembly));
        services.AddSingleton<OrleansBinaryJournalFormat>();
        services.AddKeyedSingleton<IJournalFormat>(OrleansBinaryJournalFormat.JournalFormatKey, static (sp, _) => sp.GetRequiredService<OrleansBinaryJournalFormat>());
        services.AddKeyedSingleton(typeof(IDurableDictionaryCommandCodec<,>), OrleansBinaryJournalFormat.JournalFormatKey, typeof(OrleansBinaryDurableDictionaryCommandCodec<,>));
        services.AddKeyedSingleton(typeof(IDurableValueCommandCodec<>), OrleansBinaryJournalFormat.JournalFormatKey, typeof(OrleansBinaryDurableValueCommandCodec<>));
        services.Configure<JsonJournalOptions>(options => options.SerializerOptions = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() });
        services.AddJsonJournalFormat(tryAdd: false);
        var serviceProvider = services.BuildServiceProvider();
        var journalFormat = _journalFormat = serviceProvider.GetRequiredKeyedService<IJournalFormat>(formatKey);
        _barrierCodecFactory = () => JournalFormatServices.GetRequiredCommandCodec<IDurableDictionaryCommandCodec<string, string>>(serviceProvider, formatKey);
        var accessCodec = JournalFormatServices.GetRequiredCommandCodec<IDurableDictionaryCommandCodec<TaskId, byte[]>>(serviceProvider, formatKey);
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        _runtime = new AccessRecordingRuntime(serializer);

        var shared = new JournaledStateManagerShared(
            NullLogger<JournaledStateManager>.Instance,
            Options.Create(new JournaledStateManagerOptions { JournalFormatKey = formatKey }),
            TimeProvider.System,
            serviceProvider);
        var manager = new JournaledStateManager(shared, new VolatileJournalStorage(formatKey))
        {
            ContextProvider = new DurableTaskAccessContextProvider(_runtime),
        };
        Dictionary = new DurableDictionary<string, TValue>(
            "dict",
            manager,
            JournalFormatServices.GetRequiredCommandCodec<IDurableDictionaryCommandCodec<string, TValue>>(serviceProvider, formatKey));
        Value = new DurableValue<TValue>(
            "value",
            manager,
            JournalFormatServices.GetRequiredCommandCodec<IDurableValueCommandCodec<TValue>>(serviceProvider, formatKey));
        _logs = new DurableTaskAccessLogs(manager, accessCodec, Positions ? serializer : null);
        _storage = new(_logs);
        manager.InitializeAsync().AsTask().GetAwaiter().GetResult();
        _body = new(this);

        // The states journal into buffers which each invocation resets, rather than through the manager's writes.
        _stateJournal = journalFormat.CreateWriter();
        _accessJournal = journalFormat.CreateWriter();
        _accessStream = _accessJournal.CreateJournalStreamWriter(new JournalStreamId(3));
        ((IStateMachine)Dictionary).Reset(_stateJournal.CreateJournalStreamWriter(new JournalStreamId(1)));
        ((IStateMachine)Value).Reset(_stateJournal.CreateJournalStreamWriter(new JournalStreamId(2)));
        ((IStateMachine)_logs).Reset(_accessStream);

        Keys = [.. Enumerable.Range(0, 64).Select(i => $"key-{i:D2}")];
        Values = [.. Enumerable.Range(0, 64).Select(createValue)];
        foreach (var (key, value) in Keys.Zip(Values))
        {
            ((IDurableDictionaryCommandHandler<string, TValue>)Dictionary).ApplySet(key, value);
        }

        ((IDurableValueCommandHandler<TValue>)Value).ApplySet(Values[0]);

        // Record each scenario once, so a replay consumes exactly its records, and check that it does.
        var scenarios = Enum.GetValues<AccessRecordingScenario>();
        _bodies = new Func<AccessRecordingFixture<TValue>, int>[scenarios.Length];
        _recorded = new AccessLogStorage[scenarios.Length];
        foreach (var scenario in scenarios)
        {
            var body = _bodies[(int)scenario] = getBody(scenario);
            var storage = new AccessLogStorage(new DurableTaskAccessLogs(new NullManager(), accessCodec));
            var log = RunInAttempt(storage, body, out _);
            log.Flush();
            log.Close();
            _recorded[(int)scenario] = storage;

            var replay = RunInAttempt(storage, body, out _);
            if (replay.IsReplaying)
            {
                throw new InvalidOperationException($"The replay of {scenario} didn't consume its records.");
            }

            var length = storage.GetAccessLogLength(TaskId);
            replay.Flush();
            if (storage.GetAccessLogLength(TaskId) != length)
            {
                throw new InvalidOperationException($"The replay of {scenario} recorded further accesses.");
            }
        }

        ResetJournals();
    }

    public string[] Keys { get; }

    public TValue[] Values { get; }

    public DurableDictionary<string, TValue> Dictionary { get; }

    public DurableValue<TValue> Value { get; }

    public override int RunOutside(AccessRecordingScenario scenario, int count = 1)
    {
        var body = _bodies[(int)scenario];
        var result = 0;
        for (var i = 0; i < count; i++)
        {
            result += body(this);
        }

        ResetJournals();
        return result;
    }

    public override int RunUnrecorded(AccessRecordingScenario scenario, int count = 1)
    {
        var body = _bodies[(int)scenario];
        var result = 0;
        for (var i = 0; i < count; i++)
        {
            result += RunInAttempt(body);
        }

        ResetJournals();
        return result;
    }

    public override int Record(AccessRecordingScenario scenario, int count = 1)
    {
        var body = _bodies[(int)scenario];
        var result = 0;
        for (var i = 0; i < count; i++)
        {
            var log = RunInAttempt(_storage, body, out var bodyResult);
            result += bodyResult;
            log.Flush();
            log.Close();
            JournalAndComplete();
        }

        ResetJournals();
        return result;
    }

    public override int Replay(AccessRecordingScenario scenario, int count = 1)
    {
        var body = _bodies[(int)scenario];
        var storage = _recorded[(int)scenario];
        var result = 0;
        for (var i = 0; i < count; i++)
        {
            var log = RunInAttempt(storage, body, out var bodyResult);
            result += bodyResult;
            log.Close();
        }

        ResetJournals();
        return result;
    }

    public override (int Records, int Removal) MeasureBytes(AccessRecordingScenario scenario)
    {
        var log = RunInAttempt(_storage, _bodies[(int)scenario], out _);
        log.Flush();
        log.Close();
        ResetJournals();
        ((IStateMachine)_logs).WritePendingEntries(_accessStream);
        var records = _accessJournal.CommittedLength;
        _logs.Remove(TaskId);
        ((IStateMachine)_logs).WritePendingEntries(_accessStream);
        var removal = _accessJournal.CommittedLength - records;
        ResetJournals();
        return (records, removal);
    }

    public override void Dispose()
    {
        _stateJournal.Dispose();
        _accessJournal.Dispose();
    }

    public override int MeasureBarrierBytes()
    {
        var positions = new JournalPositionState(() => _barrierCodecFactory());
        using var journal = _journalFormat.CreateWriter();
        var stream = journal.CreateJournalStreamWriter(new JournalStreamId(JournalPositionState.Id));
        positions.Set(DurableTaskAccessLogs.StateMachineName, 12_345);
        ((IStateMachine)positions).WritePendingEntries(stream);
        positions.RemoveAll();
        ((IStateMachine)positions).WritePendingEntries(stream);
        return journal.CommittedLength;
    }

    private void ResetJournals()
    {
        _stateJournal.Reset();
        _accessJournal.Reset();
    }

    private void JournalAndComplete()
    {
        ((IStateMachine)_logs).WritePendingEntries(_accessStream);
        _logs.Remove(TaskId);
        ((IStateMachine)_logs).WritePendingEntries(_accessStream);
    }

    // Runs body in an attempt whose access log the runtime creates over storage, and returns the log.
    private DurableTaskAccessLog RunInAttempt(AccessLogStorage storage, Func<AccessRecordingFixture<TValue>, int> body, out int result)
    {
        _runtime.Storage = storage;
        var attempt = new GrainDurableTaskContext(TaskId, _runtime, grainContext: null!, TaskScheduler.Current);
        _runtime.Storage = null;
        result = Run(attempt, body);
        return attempt.AccessLog!;
    }

    // Runs body in an attempt which has no access log.
    private int RunInAttempt(Func<AccessRecordingFixture<TValue>, int> body) =>
        Run(new GrainDurableTaskContext(TaskId, _runtime, grainContext: null!, TaskScheduler.Current), body);

    private int Run(GrainDurableTaskContext attempt, Func<AccessRecordingFixture<TValue>, int> body)
    {
        _body.Body = body;
        var run = DurableTaskRuntimeHelper.RunAsync(_body, attempt);
        Debug.Assert(run.IsCompleted, "The bodies run synchronously.");
        if (run.Result.Exception is { } exception)
        {
            ExceptionDispatchInfo.Throw(exception);
        }

        return _body.Result;
    }

    // Runs the fixture's current body as a durable task's body, as the runtime runs a grain's durable task method.
    private sealed class BodyTask(AccessRecordingFixture<TValue> fixture) : DurableTask
    {
        public Func<AccessRecordingFixture<TValue>, int> Body = null!;
        public int Result;

        protected override ValueTask<DurableTaskResponse> RunAsync(DurableTaskContext context)
        {
            Result = Body(fixture);
            return new(DurableTaskResponse.Completed);
        }
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
