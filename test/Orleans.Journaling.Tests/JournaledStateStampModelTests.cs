using System.Buffers;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Accordant;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Journaling.Json;
using Orleans.Serialization;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Verifies the change stamp of every journaled state type against a model of the content which its reads observe:
/// whenever that content changes, through a public operation which runs directly or through a context, a journal
/// write's acknowledgement, a deletion, or recovery from appended entries or a snapshot, the stamp strictly increases,
/// and a read leaves it unchanged. Each type runs in both the binary and the JSON journal formats.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class JournaledStateStampModelTests
{
    private const string WriteCommand = "Write";
    private const string DeleteCommand = "Delete";
    private const string RecoverCommand = "Recover";
    private const string WriteRecoverCommand = "Write, then recover";
    private const string SnapshotRecoverCommand = "Snapshot, then recover";

    public static TheoryData<string, string> Cases
    {
        get
        {
            var cases = new TheoryData<string, string>();
            foreach (var subject in Subjects.Keys)
            {
                cases.Add(subject, OrleansBinaryJournalFormat.JournalFormatKey);
                cases.Add(subject, JsonLinesJournalFormat.JournalFormatKey);
            }

            return cases;
        }
    }

    private static Dictionary<string, StampSubject> Subjects { get; } = new StampSubject[]
    {
        new ValueSubject(),
        new DictionarySubject(),
        new ListSubject(),
        new QueueSubject(),
        new SetSubject(),
        new LogSubject(),
        new CompletionSourceSubject(),
    }.ToDictionary(static subject => subject.Name);

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task StampIncreasesWithEveryObservableChangeAndReadsLeaveItUnchanged(string subjectName, string formatKey)
    {
        var subject = Subjects[subjectName];
        var spec = new StampSpec(subject);
        var inputs = new InputSet();
        foreach (var command in subject.Commands)
        {
            inputs.Add(spec.Step.With(new StampRequest(command.Name), command.Name));
        }

        foreach (var command in (string[])[WriteCommand, DeleteCommand, RecoverCommand, WriteRecoverCommand, SnapshotRecoverCommand])
        {
            inputs.Add(spec.Step.With(new StampRequest(command), command));
        }

        var initial = new StampModelState { Live = subject.Empty, Persisted = subject.Empty };
        var testCases = spec.GenerateTests(
            initial,
            inputs,
            new TestGenerationOptions
            {
                MaxDepth = 3,
                SequentialTestCaseAlgorithm = SequentialTestCaseAlgorithms.CreateTransitionCoverage(maxSequenceLength: 2),
            });
        Assert.True(testCases.Count >= subject.Commands.Count, $"Accordant generated {testCases.Count} cases.");

        var format = new StampFormat(formatKey);
        var context = spec.CreateTestingContext($"{nameof(JournaledStateStampModelTests)}-{subjectName}-{formatKey}");
        context.RequestPrinter = static request => request?.ToString() ?? "<null>";
        context.ResponsePrinter = static response => response?.ToString() ?? "<null>";
        var results = await spec.RunTests(
            context,
            initial,
            testCases,
            new TestExecutionOptions
            {
                StopOnFirstFailure = true,
                BeforeEachAsync = async info => info.Context.Register(await StampSystem.CreateAsync(subject, format)),
                AfterEachAsync = static info => info.Context.Get<StampSystem>().DisposeAsync().AsTask(),
            });

        var failure = results.FirstOrDefault(static result => !result.Success);
        Assert.True(failure is null, $"{failure?.LastFailureMessage} Log: {failure?.LogFilePath}");
    }

    private sealed record StampRequest(string Name)
    {
        public override string ToString() => Name;
    }

    /// <summary>What a step observed: the content and the stamp of the state before and after it, and its exception.</summary>
    private sealed record StampObservation(string Before, long StampBefore, string After, long StampAfter, string? Error)
    {
        public override string ToString() => $"before=[{Before}]@{StampBefore} after=[{After}]@{StampAfter} error={Error ?? "none"}";
    }

    private sealed class StampSpec : Spec<StampModelState>
    {
        public StampSpec(StampSubject subject)
        {
            Step = new StepOperation(subject);
            Add(Step);
        }

        public StepOperation Step { get; }
    }

    private sealed class StepOperation(StampSubject subject) : Operation<StampRequest, StampObservation, StampModelState>("Step")
    {
        public override ExpectedOutcomes Apply(StampRequest request, StampModelState state)
        {
            var live = state.Live;
            var persisted = state.Persisted;
            var isRead = false;
            var throws = false;
            switch (request.Name)
            {
                case WriteCommand:
                    live = subject.Acknowledge(live);
                    persisted = live;
                    break;
                case DeleteCommand:
                    live = persisted = subject.Empty;
                    break;
                case RecoverCommand:
                    live = persisted;
                    break;
                case WriteRecoverCommand:
                case SnapshotRecoverCommand:
                    live = persisted = subject.Acknowledge(live);
                    break;
                default:
                    var command = subject.Find(request.Name);
                    isRead = command.IsRead;
                    var next = command.Model(live);
                    throws = next is null;
                    live = next ?? live;
                    break;
            }

            var expected = string.Join("|", live);
            return Expect.That(observation => Validate(observation, expected, isRead, throws))
                .ThenState(next =>
                {
                    next.Live = live;
                    next.Persisted = persisted;
                });
        }

        public override Task<StampObservation> ExecuteAsync(TestingContext context, StampRequest request) =>
            context.Get<StampSystem>().StepAsync(request.Name);

        private static ValidationResult Validate(StampObservation observation, string expected, bool isRead, bool throws)
        {
            if (observation.After != expected)
            {
                return ValidationResult.Invalid($"The state holds [{observation.After}], and the model [{expected}].");
            }

            if (observation.Error is not null != throws)
            {
                return ValidationResult.Invalid($"The step threw {observation.Error ?? "nothing"}, and the model expects {(throws ? "an exception" : "none")}.");
            }

            if (observation.Before != observation.After && observation.StampAfter <= observation.StampBefore)
            {
                return ValidationResult.Invalid($"The content changed from [{observation.Before}] to [{observation.After}], but the stamp went from {observation.StampBefore} to {observation.StampAfter}.");
            }

            if (observation.StampAfter < observation.StampBefore)
            {
                return ValidationResult.Invalid($"The stamp decreased from {observation.StampBefore} to {observation.StampAfter}.");
            }

            if (isRead && observation.StampAfter != observation.StampBefore)
            {
                return ValidationResult.Invalid($"A read changed the stamp from {observation.StampBefore} to {observation.StampAfter}.");
            }

            return ValidationResult.Valid();
        }
    }

    /// <summary>
    /// A state of one manager, which runs the model's steps: its public operations, alternately directly and through a
    /// context, and journal writes, deletions and recoveries, which recover into a new manager and state.
    /// </summary>
    /// <summary>The services of one journal format, which every system of a test shares.</summary>
    private sealed class StampFormat
    {
        public StampFormat(string formatKey)
        {
            var services = new ServiceCollection();
            services.AddSerializer();
            services.AddLogging();
            if (formatKey == JsonLinesJournalFormat.JournalFormatKey)
            {
                services.AddJsonJournalFormat(tryAdd: false);
                services.Configure<JsonJournalOptions>(static options => options.AddTypeInfoResolver(new DefaultJsonTypeInfoResolver()));
            }
            else
            {
                services.AddSingleton<OrleansBinaryJournalFormat>();
                services.AddKeyedSingleton<IJournalFormat>(formatKey, static (sp, _) => sp.GetRequiredService<OrleansBinaryJournalFormat>());
                services.AddKeyedSingleton(typeof(IDurableDictionaryCommandCodec<,>), formatKey, typeof(OrleansBinaryDurableDictionaryCommandCodec<,>));
                services.AddKeyedSingleton(typeof(IDurableListCommandCodec<>), formatKey, typeof(OrleansBinaryDurableListCommandCodec<>));
                services.AddKeyedSingleton(typeof(IDurableQueueCommandCodec<>), formatKey, typeof(OrleansBinaryDurableQueueCommandCodec<>));
                services.AddKeyedSingleton(typeof(IDurableSetCommandCodec<>), formatKey, typeof(OrleansBinaryDurableSetCommandCodec<>));
                services.AddKeyedSingleton(typeof(IDurableLogCommandCodec<>), formatKey, typeof(OrleansBinaryDurableLogCommandCodec<>));
                services.AddKeyedSingleton(typeof(IDurableValueCommandCodec<>), formatKey, typeof(OrleansBinaryDurableValueCommandCodec<>));
                services.AddKeyedSingleton(typeof(IDurableTaskCompletionSourceCommandCodec<>), formatKey, typeof(OrleansBinaryDurableTaskCompletionSourceCommandCodec<>));
            }

            Services = services.BuildServiceProvider();
            Shared = new JournaledStateManagerShared(
                Services.GetRequiredService<ILogger<JournaledStateManager>>(),
                Options.Create(new JournaledStateManagerOptions { JournalFormatKey = formatKey }),
                TimeProvider.System,
                Services);
        }

        public ServiceProvider Services { get; }

        public JournaledStateManagerShared Shared { get; }
    }

    private sealed class StampSystem
    {
        private readonly StampSubject _subject;
        private readonly IServiceProvider _services;
        private readonly JournaledStateManagerShared _shared;
        private readonly VolatileJournalStorage.Store _store = new($"stamp-{Guid.NewGuid():N}");
        private readonly PassThroughProvider _provider = new();
        private CompactingStorage _storage = null!;
        private JournaledStateManager _manager = null!;
        private object _state = null!;
        private int _operations;

        private StampSystem(StampSubject subject, StampFormat format)
        {
            _subject = subject;
            _services = format.Services;
            _shared = format.Shared;
        }

        public static async Task<StampSystem> CreateAsync(StampSubject subject, StampFormat format)
        {
            var system = new StampSystem(subject, format);
            system.Bind();
            await system._manager.InitializeAsync();
            return system;
        }

        public async Task<StampObservation> StepAsync(string name)
        {
            var before = Fingerprint();
            var stampBefore = Stamp();
            string? error = null;
            switch (name)
            {
                case WriteCommand:
                    await _manager.WriteStateAsync();
                    break;
                case DeleteCommand:
                    await _manager.DeleteStateAsync();
                    break;
                case RecoverCommand:
                case WriteRecoverCommand:
                case SnapshotRecoverCommand:
                    if (name != RecoverCommand)
                    {
                        _storage.IsCompactionRequested = name == SnapshotRecoverCommand;
                        await _manager.WriteStateAsync();
                    }

                    await _manager.DisposeAsync();

                    // Recovery fills a new state, whose stamp the step compares from before recovery.
                    Bind();
                    before = Fingerprint();
                    stampBefore = Stamp();
                    await _manager.InitializeAsync();
                    break;
                default:
                    var command = _subject.Find(name);
                    _provider.Context = ++_operations % 2 == 0 ? new PassThroughContext() : null;
                    try
                    {
                        command.Run(_state);
                    }
                    catch (Exception exception)
                    {
                        error = exception.GetType().Name;
                    }
                    finally
                    {
                        _provider.Context = null;
                    }

                    break;
            }

            return new(before, stampBefore, Fingerprint(), Stamp(), error);
        }

        public ValueTask DisposeAsync() => _manager.DisposeAsync();

        private void Bind()
        {
            _storage = new CompactingStorage(new VolatileJournalStorage(_store, _shared.JournalFormatKey));
            _manager = new JournaledStateManager(_shared, _storage) { ContextProvider = _provider };
            _state = _subject.Create("state", _manager, _shared, _services);
        }

        private string Fingerprint() => string.Join("|", _subject.Observe(_state));

        private long Stamp() => ((JournaledStateAccessTarget)_state.GetType()
            .GetField("_accessTarget", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(_state)!).Stamp;
    }

    /// <summary>Volatile storage whose writes are snapshots while <see cref="IsCompactionRequested"/> is set.</summary>
    private sealed class CompactingStorage(VolatileJournalStorage inner) : IJournalStorage
    {
        public bool IsCompactionRequested { get; set; }

        public ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken) => inner.ReadAsync(consumer, cancellationToken);

        public ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
        {
            IsCompactionRequested = false;
            return inner.ReplaceAsync(value, cancellationToken);
        }

        public ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken) => inner.AppendAsync(value, cancellationToken);

        public ValueTask DeleteAsync(CancellationToken cancellationToken) => inner.DeleteAsync(cancellationToken);
    }

    private sealed class PassThroughProvider : JournaledStateContextProvider
    {
        public PassThroughContext? Context { get; set; }

        public override JournaledStateContext? Current => Context is { Depth: 0 } context ? context : null;
    }

    /// <summary>A context which runs each operation against the state, as a recording attempt does.</summary>
    private sealed class PassThroughContext : JournaledStateContext
    {
        public int Depth { get; private set; }

        protected override TResult Access<TArgument, TState, TResult>(
            in JournaledStateAccess access,
            TArgument argument,
            TState state,
            Func<TState, TResult> operation,
            Action<TState, TResult>? apply)
        {
            Depth++;
            try
            {
                var result = operation(state);
                apply?.Invoke(state, result);
                return result;
            }
            finally
            {
                Depth--;
            }
        }
    }

    /// <summary>
    /// A public operation of a state, and the model of its effect on the content which reads observe, which returns
    /// <see langword="null"/> when the operation throws and leaves the content unchanged.
    /// </summary>
    private sealed record StampCommand(string Name, bool IsRead, Action<object> Run, Func<List<string>, List<string>?> Model);

    /// <summary>A state type, its operations, and the content which its reads observe, as a list of strings.</summary>
    private abstract class StampSubject
    {
        public abstract string Name { get; }

        public abstract List<string> Empty { get; }

        public abstract IReadOnlyList<StampCommand> Commands { get; }

        public StampCommand Find(string name) => Commands.Single(command => command.Name == name);

        public abstract object Create(string key, JournaledStateManager manager, JournaledStateManagerShared shared, IServiceProvider services);

        public abstract List<string> Observe(object state);

        /// <summary>Gets the content after a journal write acknowledges every change.</summary>
        public virtual List<string> Acknowledge(List<string> content) => content;

        protected static StampCommand Read<T>(string name, Action<T> read, Func<List<string>, bool>? throws = null) =>
            new(name, true, state => read((T)state), content => throws?.Invoke(content) is true ? null : content);

        protected static StampCommand Write<T>(string name, Action<T> write, Func<List<string>, List<string>?> model) => new(name, false, state => write((T)state), model);
    }

    private sealed class ValueSubject : StampSubject
    {
        public override string Name => "value";

        public override List<string> Empty => ["0"];

        public override IReadOnlyList<StampCommand> Commands { get; } =
        [
            Read<DurableValue<int>>("Get", static value => _ = value.Value),
            Write<DurableValue<int>>("Set 1", static value => value.Value = 1, static _ => ["1"]),
            Write<DurableValue<int>>("Set 2", static value => value.Value = 2, static _ => ["2"]),
        ];

        public override object Create(string key, JournaledStateManager manager, JournaledStateManagerShared shared, IServiceProvider services) =>
            new DurableValue<int>(key, manager, shared, services);

        public override List<string> Observe(object state) => [((DurableValue<int>)state).Value.ToString()];
    }

    private sealed class DictionarySubject : StampSubject
    {
        public override string Name => "dictionary";

        public override List<string> Empty => [];

        public override IReadOnlyList<StampCommand> Commands { get; } =
        [
            Read<DurableDictionary<string, int>>("Get a", static d => _ = d["a"], static c => Find(c, "a") is null),
            Read<DurableDictionary<string, int>>("TryGetValue a", static d => _ = d.TryGetValue("a", out _)),
            Read<DurableDictionary<string, int>>("ContainsKey a", static d => _ = d.ContainsKey("a")),
            Read<DurableDictionary<string, int>>("Contains a=1", static d => _ = d.Contains(new KeyValuePair<string, int>("a", 1))),
            Read<DurableDictionary<string, int>>("Count", static d => _ = d.Count),
            Read<DurableDictionary<string, int>>("Keys", static d => _ = d.Keys),
            Read<DurableDictionary<string, int>>("Values", static d => _ = d.Values),
            Read<DurableDictionary<string, int>>("Enumerate", static d => _ = d.ToList()),
            Write<DurableDictionary<string, int>>("Set a=1", static d => d["a"] = 1, static c => Set(c, "a", 1)),
            Write<DurableDictionary<string, int>>("Set b=2", static d => d["b"] = 2, static c => Set(c, "b", 2)),
            Write<DurableDictionary<string, int>>("Add a=2", static d => d.Add("a", 2), static c => Find(c, "a") is null ? Set(c, "a", 2) : null),
            Write<DurableDictionary<string, int>>("Remove a", static d => d.Remove("a"), static c => Remove(c, "a")),
            Write<DurableDictionary<string, int>>("Remove a=1", static d => d.Remove(new KeyValuePair<string, int>("a", 1)), static c => Find(c, "a") == "a=1" ? Remove(c, "a") : c),
            Write<DurableDictionary<string, int>>("Clear", static d => d.Clear(), static _ => []),
        ];

        public override object Create(string key, JournaledStateManager manager, JournaledStateManagerShared shared, IServiceProvider services) =>
            new DurableDictionary<string, int>(key, manager, shared, services);

        // Enumeration order follows the dictionary's history, so the model compares the entries in key order.
        public override List<string> Observe(object state) =>
            [.. ((DurableDictionary<string, int>)state).Select(static pair => $"{pair.Key}={pair.Value}").Order(StringComparer.Ordinal)];

        private static string? Find(List<string> content, string key) => content.FirstOrDefault(entry => entry.StartsWith(key + "=", StringComparison.Ordinal));

        private static List<string> Set(List<string> content, string key, int value) =>
            [.. Remove(content, key).Append($"{key}={value}").Order(StringComparer.Ordinal)];

        private static List<string> Remove(List<string> content, string key) => [.. content.Where(entry => !entry.StartsWith(key + "=", StringComparison.Ordinal))];
    }

    private sealed class ListSubject : StampSubject
    {
        public override string Name => "list";

        public override List<string> Empty => [];

        public override IReadOnlyList<StampCommand> Commands { get; } =
        [
            Read<DurableList<string>>("Get 0", static l => _ = l[0], static c => c.Count == 0),
            Read<DurableList<string>>("Count", static l => _ = l.Count),
            Read<DurableList<string>>("Contains a", static l => _ = l.Contains("a")),
            Read<DurableList<string>>("IndexOf a", static l => _ = l.IndexOf("a")),
            Read<DurableList<string>>("Enumerate", static l => _ = l.ToList()),
            Write<DurableList<string>>("Add a", static l => l.Add("a"), static c => [.. c, "a"]),
            Write<DurableList<string>>("Add b", static l => l.Add("b"), static c => [.. c, "b"]),
            Write<DurableList<string>>("Insert 0 c", static l => l.Insert(0, "c"), static c => ["c", .. c]),
            Write<DurableList<string>>("Set 0 d", static l => l[0] = "d", static c => c.Count == 0 ? null : ["d", .. c.Skip(1)]),
            Write<DurableList<string>>("Remove a", static l => l.Remove("a"), static c => RemoveFirst(c, "a")),
            Write<DurableList<string>>("RemoveAt 0", static l => l.RemoveAt(0), static c => c.Count == 0 ? null : [.. c.Skip(1)]),
            Write<DurableList<string>>("AddRange e f", static l => l.AddRange(["e", "f"]), static c => [.. c, "e", "f"]),
            Write<DurableList<string>>("Clear", static l => l.Clear(), static _ => []),
        ];

        public override object Create(string key, JournaledStateManager manager, JournaledStateManagerShared shared, IServiceProvider services) =>
            new DurableList<string>(key, manager, shared, services);

        public override List<string> Observe(object state) => [.. (DurableList<string>)state];

        private static List<string> RemoveFirst(List<string> content, string item)
        {
            var next = new List<string>(content);
            next.Remove(item);
            return next;
        }
    }

    private sealed class QueueSubject : StampSubject
    {
        public override string Name => "queue";

        public override List<string> Empty => [];

        public override IReadOnlyList<StampCommand> Commands { get; } =
        [
            Read<DurableQueue<string>>("Count", static q => _ = q.Count),
            Read<DurableQueue<string>>("Peek", static q => _ = q.Peek(), static c => c.Count == 0),
            Read<DurableQueue<string>>("TryPeek", static q => _ = q.TryPeek(out _)),
            Read<DurableQueue<string>>("Contains a", static q => _ = q.Contains("a")),
            Read<DurableQueue<string>>("Enumerate", static q => _ = q.ToList()),
            Write<DurableQueue<string>>("Enqueue a", static q => q.Enqueue("a"), static c => [.. c, "a"]),
            Write<DurableQueue<string>>("Enqueue b", static q => q.Enqueue("b"), static c => [.. c, "b"]),
            Write<DurableQueue<string>>("Dequeue", static q => q.Dequeue(), static c => c.Count == 0 ? null : [.. c.Skip(1)]),
            Write<DurableQueue<string>>("TryDequeue", static q => q.TryDequeue(out _), static c => [.. c.Skip(1)]),
            Write<DurableQueue<string>>("Clear", static q => q.Clear(), static _ => []),
        ];

        public override object Create(string key, JournaledStateManager manager, JournaledStateManagerShared shared, IServiceProvider services) =>
            new DurableQueue<string>(key, manager, shared, services);

        public override List<string> Observe(object state) => [.. (DurableQueue<string>)state];
    }

    private sealed class SetSubject : StampSubject
    {
        public override string Name => "set";

        public override List<string> Empty => [];

        public override IReadOnlyList<StampCommand> Commands { get; } =
        [
            Read<DurableSet<string>>("Count", static s => _ = s.Count),
            Read<DurableSet<string>>("Contains a", static s => _ = s.Contains("a")),
            Read<DurableSet<string>>("Enumerate", static s => _ = s.ToList()),
            Read<DurableSet<string>>("IsSubsetOf a b", static s => _ = s.IsSubsetOf(["a", "b"])),
            Read<DurableSet<string>>("IsProperSupersetOf a", static s => _ = s.IsProperSupersetOf(["a"])),
            Read<DurableSet<string>>("Overlaps b", static s => _ = s.Overlaps(["b"])),
            Read<DurableSet<string>>("SetEquals a", static s => _ = s.SetEquals(["a"])),
            Write<DurableSet<string>>("Add a", static s => s.Add("a"), static c => Sorted(c.Union(["a"]))),
            Write<DurableSet<string>>("Add b", static s => s.Add("b"), static c => Sorted(c.Union(["b"]))),
            Write<DurableSet<string>>("Remove a", static s => s.Remove("a"), static c => Sorted(c.Except(["a"]))),
            Write<DurableSet<string>>("UnionWith b c", static s => s.UnionWith(["b", "c"]), static c => Sorted(c.Union(["b", "c"]))),
            Write<DurableSet<string>>("IntersectWith a b", static s => s.IntersectWith(["a", "b"]), static c => Sorted(c.Intersect(["a", "b"]))),
            Write<DurableSet<string>>("ExceptWith a", static s => s.ExceptWith(["a"]), static c => Sorted(c.Except(["a"]))),
            Write<DurableSet<string>>("SymmetricExceptWith c", static s => s.SymmetricExceptWith(["c"]), static c => Sorted(c.Contains("c") ? c.Except(["c"]) : c.Append("c"))),
            Write<DurableSet<string>>("Clear", static s => s.Clear(), static _ => []),
        ];

        public override object Create(string key, JournaledStateManager manager, JournaledStateManagerShared shared, IServiceProvider services) =>
            new DurableSet<string>(key, manager, shared, services);

        // Enumeration order follows the set's history, so the model compares the items in order.
        public override List<string> Observe(object state) => Sorted((DurableSet<string>)state);

        private static List<string> Sorted(IEnumerable<string> items) => [.. items.Order(StringComparer.Ordinal)];
    }

    private sealed class LogSubject : StampSubject
    {
        public override string Name => "log";

        public override List<string> Empty => Format(first: 0, committed: 0, isSealed: false, []);

        public override IReadOnlyList<StampCommand> Commands { get; } =
        [
            Read<DurableLog<string>>("Count", static l => _ = l.Count),
            Read<DurableLog<string>>("FirstSequence", static l => _ = l.FirstSequence),
            Read<DurableLog<string>>("NextSequence", static l => _ = l.NextSequence),
            Read<DurableLog<string>>("CommittedSequence", static l => _ = l.CommittedSequence),
            Read<DurableLog<string>>("IsSealed", static l => _ = l.IsSealed),
            Read<DurableLog<string>>("Get 1", static l => _ = l[1], static c => Change(c, static m => m.First <= 1 && 1 < m.Next ? m : null) is null),
            Read<DurableLog<string>>("TryGet 1", static l => _ = l.TryGet(1, out _)),
            Read<DurableLog<string>>("Enumerate", static l => _ = l.ToList()),
            Write<DurableLog<string>>("Append a", static l => l.Append("a"), static c => Change(c, static m => m.Sealed ? null : m with { Items = [.. m.Items, "a"] })),
            Write<DurableLog<string>>("Commit draft d", static l =>
            {
                using var draft = l.BeginDraft();
                draft.Write("partial");
                draft.Commit("d");
            }, static c => Change(c, static m => m.Sealed ? null : m with { Items = [.. m.Items, "d"] })),
            Write<DurableLog<string>>("TrimBefore 1", static l => l.TrimBefore(1), static c => Change(c, static m =>
                m.Next < 1 ? null : m.First >= 1 ? m : m with { First = 1, Items = [.. m.Items.Skip((int)(1 - m.First))] })),
            Write<DurableLog<string>>("Clear", static l => l.Clear(), static c => Change(c, static m => m.Sealed ? null : m with { First = m.Next + 1, Items = [] })),
            Write<DurableLog<string>>("Seal", static l => l.Seal(), static c => Change(c, static m => m with { Sealed = true })),
        ];

        public override object Create(string key, JournaledStateManager manager, JournaledStateManagerShared shared, IServiceProvider services) =>
            new DurableLog<string>(key, manager, shared, services);

        public override List<string> Observe(object state)
        {
            var log = (DurableLog<string>)state;
            return Format(log.FirstSequence, log.CommittedSequence, log.IsSealed, [.. log]);
        }

        public override List<string> Acknowledge(List<string> content) => Change(content, static m => m with { Committed = m.Next })!;

        private static List<string> Format(long first, long committed, bool isSealed, List<string> items) =>
            [$"first={first}", $"committed={committed}", $"sealed={isSealed}", .. items];

        private static List<string>? Change(List<string> content, Func<LogModel, LogModel?> change)
        {
            var model = new LogModel(
                long.Parse(content[0]["first=".Length..]),
                long.Parse(content[1]["committed=".Length..]),
                bool.Parse(content[2]["sealed=".Length..]),
                [.. content.Skip(3)]);
            return change(model) is { } next ? Format(next.First, next.Committed, next.Sealed, next.Items) : null;
        }

        private sealed record LogModel(long First, long Committed, bool Sealed, List<string> Items)
        {
            public long Next => First + Items.Count;
        }
    }

    private sealed class CompletionSourceSubject : StampSubject
    {
        public override string Name => "completion";

        public override List<string> Empty => ["Pending"];

        public override IReadOnlyList<StampCommand> Commands { get; } =
        [
            Read<DurableTaskCompletionSource<int>>("State", static c => _ = c.State),
            Write<DurableTaskCompletionSource<int>>("TrySetResult 1", static c => c.TrySetResult(1), static c => Complete(c, "Completed:1")),
            Write<DurableTaskCompletionSource<int>>("TrySetException", static c => c.TrySetException(new InvalidOperationException("boom")), static c => Complete(c, "Faulted:boom")),
            Write<DurableTaskCompletionSource<int>>("TrySetCanceled", static c => c.TrySetCanceled(), static c => Complete(c, "Canceled")),
        ];

        public override object Create(string key, JournaledStateManager manager, JournaledStateManagerShared shared, IServiceProvider services) =>
            new DurableTaskCompletionSource<int>(
                key,
                manager,
                shared,
                services,
                services.GetRequiredService<DeepCopier<int>>(),
                services.GetRequiredService<DeepCopier<Exception>>());

        public override List<string> Observe(object state)
        {
            var completion = ((DurableTaskCompletionSource<int>)state).State;
            return [completion.Status switch
            {
                DurableTaskCompletionSourceStatus.Completed => $"Completed:{completion.Value}",
                DurableTaskCompletionSourceStatus.Faulted => $"Faulted:{completion.Exception!.Message}",
                _ => completion.Status.ToString(),
            }];
        }

        private static List<string> Complete(List<string> content, string outcome) => content[0] == "Pending" ? [outcome] : content;
    }
}

[State]
internal partial class StampModelState : State
{
    public List<string> Live { get; set; } = [];

    public List<string> Persisted { get; set; } = [];
}
