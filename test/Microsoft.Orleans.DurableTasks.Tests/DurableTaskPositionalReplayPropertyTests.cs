using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json.Serialization;
using CsCheck;
using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Runtime;
using Orleans.DurableTasks.Storage;
using Orleans.Journaling;
using Orleans.Journaling.Json;
using Orleans.Serialization;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.Tests;

/// <summary>
/// Verifies differentially that positional reads keep the replay contract: a replay returns what the first run observed.
/// The same random bodies run against a journal whose reads may record positions and against one whose reads record
/// values; once recovery resolves the positions, the two access logs hold the same records, and a replay observes what
/// the first run observed.
/// </summary>
/// <remarks>
/// The bodies read and write every journaled state type, including element types which compare by reference, an unsealed
/// polymorphic type, and types which JSON doesn't round-trip exactly, in both journal formats. The logs are compared record
/// by record, with each record's form and outcome bytes, after merging adjacent references to the same read: a journal
/// write stages an open run of references, so the positional journal's extra writes can split one run into several.
/// </remarks>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class DurableTaskPositionalReplayPropertyTests
{
    private static readonly TaskId Execution = TaskId.CreateRoot("positional-replay");
    private static readonly string[] Texts = ["x", "y", "z"];

    public static TheoryData<string> Formats => DurableTaskJournalHarness.Formats;

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task PositionalAccessLogsResolveToValueLogs(string format)
    {
        var access = Gen.Select(Gen.Int[0, StateKinds - 1], Gen.Int[0, 7], Gen.Int[0, 2]);
        var step = Gen.Frequency(
            (8, access.Select(static a => new Step(StepKind.Read, a.Item1, a.Item2, a.Item3))),
            (4, access.Select(static a => new Step(StepKind.Write, a.Item1, a.Item2, a.Item3))),
            (3, access.Select(static a => new Step(StepKind.Outside, a.Item1, a.Item2, a.Item3))),
            (2, Gen.Const(new Step(StepKind.Flush, 0, 0, 0))),
            (2, Gen.Const(new Step(StepKind.WriteJournal, 0, 0, 0))),
            (1, Gen.Const(new Step(StepKind.Snapshot, 0, 0, 0))),
            (1, Gen.Const(new Step(StepKind.ForceResolution, 0, 0, 0))));
        var positionalRuns = 0;
        async Task CheckAsync(Step[] steps)
        {
            await using var positional = new World(format, recordsPositions: true);
            await using var values = new World(format, recordsPositions: false);
            var (observed, positionalRecords) = await positional.RunFirstAsync(steps);
            var (valueObserved, valueRecords) = await values.RunFirstAsync(steps);
            Assert.Equal(0, valueRecords);
            Assert.Equal(valueObserved, observed);
            if (positionalRecords > 0)
            {
                Interlocked.Increment(ref positionalRuns);
            }

            // Recovery resolves every position to the value which the read observed, from the history which it read.
            await using var recovered = await positional.ActivateAsync();
            await using var valueRecovered = await values.ActivateAsync();
            Assert.Null(DurableTaskAccessLog.FindPositionalRecords(recovered.Storage.GetAccessLog(Execution), []));
            Assert.False(recovered.Manager.Positions.HasBarriers);
            Assert.Equal(
                CanonicalRecords(valueRecovered.Storage.GetAccessLog(Execution).Span, valueRecovered.DescribeException),
                CanonicalRecords(recovered.Storage.GetAccessLog(Execution).Span, recovered.DescribeException));
            Assert.Equal(observed, recovered.Replay(steps));
        }

        // A read of a journaled value at the positional floor, which the binary format records as a position: the random runs
        // record positions only when they happen to write and then read such a value.
        await CheckAsync(
        [
            new Step(StepKind.Write, 0, 0, Texts.Length - 1),
            new Step(StepKind.WriteJournal, 0, 0, 0),
            new Step(StepKind.Read, 0, 0, Texts.Length - 1),
            new Step(StepKind.Flush, 0, 0, 0),
        ]);

        // Positions are offered only where the journal's codecs reproduce values exactly: the binary format, not JSON.
        Assert.Equal(format == DurableTaskJournalHarness.OrleansBinary ? 1 : 0, positionalRuns);

        await step.Array[1, 40].SampleAsync(CheckAsync, iter: 80);
        if (format != DurableTaskJournalHarness.OrleansBinary)
        {
            Assert.Equal(0, positionalRuns);
        }
    }

    [Fact]
    public void PositionsAreRecordedOnlyForExactTypes()
    {
        Assert.True(PositionalResults.IsExact(typeof(string)));
        Assert.True(PositionalResults.IsExact(typeof((bool, string))));
        Assert.True(PositionalResults.IsExact(typeof(long[])));
        Assert.True(PositionalResults.IsExact(typeof((int, string)[])));
        Assert.False(PositionalResults.IsExact(typeof(string[])));
        Assert.True(PositionalResults.IsExact(typeof(DurableTaskCompletionSourceState<string>)));
        Assert.True(PositionalResults.IsExact(typeof(ReplayRecord)));
        Assert.False(PositionalResults.IsExact(typeof(ReplayShape)));
        Assert.False(PositionalResults.IsExact(typeof(ReplayShape[])));
        Assert.False(PositionalResults.IsExact(typeof((bool, ReplayShape))));
        Assert.False(PositionalResults.IsExact(typeof(object)));

        Assert.True(PositionalResults.IsSmall(typeof(int)));
        Assert.True(PositionalResults.IsSmall(typeof(bool)));
        Assert.True(PositionalResults.IsSmall(typeof(DayOfWeek)));
        Assert.True(PositionalResults.IsSmall(typeof((bool, long))));
        Assert.False(PositionalResults.IsSmall(typeof((bool, string))));
        Assert.False(PositionalResults.IsSmall(typeof(string)));

        Assert.True(ValueEquality<string>.IsSupported);
        Assert.True(ValueEquality<int>.IsSupported);
        Assert.True(ValueEquality<KeyValuePair<string, int>>.IsSupported);
        Assert.True(ValueEquality<ReplayRecord>.IsSupported);
        Assert.False(ValueEquality<ReplayKey>.IsSupported);
        Assert.False(ValueEquality<byte[]>.IsSupported);
        Assert.False(ValueEquality<KeyValuePair<string, ReplayKey>>.IsSupported);
    }

    private const int StateKinds = 11;

    /// <summary>
    /// Decodes access log records into one line per record, naming each state by its id rather than its index, and merges
    /// adjacent references to the same read, with the same argument hash, into one with the sum of their counts.
    /// </summary>
    /// <remarks>
    /// Fails on a record which isn't of the documented format, or on trailing bytes. An exception record is described by
    /// <paramref name="describeException"/> rather than its bytes: its stack trace differs between runs as the JIT inlines
    /// differently, and what a replay rethrows is its type and message.
    /// </remarks>
    private static List<string> CanonicalRecords(ReadOnlySpan<byte> records, Func<byte[], string> describeException)
    {
        const byte KindMask = 0x03, HasValue = 0x04, IsException = 0x08, HasArgumentHash = 0x10, IsReference = 0x20, IsPosition = 0x40, Format = 0x80;
        var canonical = new List<string>();
        var states = new List<int>();
        string? openReference = null;
        ulong openCount = 0;
        var position = 0;
        while (position < records.Length)
        {
            var header = records[position];
            var form = header & (HasValue | IsException | IsReference | IsPosition);
            if ((header & Format) == 0 || form is not (0 or HasValue or (HasValue | IsException) or IsReference or IsPosition) || records.Length - position < 3)
            {
                Assert.Fail($"Unreadable record with header {header:X2} at {position}.");
            }

            var operation = (JournaledStateOperation)records[position + 1];
            position += 2;
            var index = (int)ReadVarUInt(records, ref position);
            Assert.InRange(index, 0, states.Count);
            if (index == states.Count)
            {
                states.Add(BinaryPrimitives.ReadInt32LittleEndian(records[position..]));
                position += 4;
            }

            var hash = "-";
            if ((header & HasArgumentHash) != 0)
            {
                hash = BinaryPrimitives.ReadInt32LittleEndian(records[position..]).ToString(CultureInfo.InvariantCulture);
                position += 4;
            }

            var access = $"{(JournaledStateAccessKind)(header & KindMask)} {operation} {states[index]} {hash}";
            if (form is IsReference)
            {
                var count = ReadVarUInt(records, ref position);
                if (openReference == access)
                {
                    openCount += count;
                }
                else
                {
                    EndReference();
                    openReference = access;
                    openCount = count;
                }

                continue;
            }

            EndReference();
            string outcome;
            switch (form)
            {
                case 0:
                    outcome = "none";
                    break;
                case IsPosition:
                    var journalPosition = ReadVarUInt(records, ref position);
                    outcome = $"position {journalPosition} {Convert.ToHexString(ReadLengthPrefixed(records, ref position))}";
                    break;
                case HasValue | IsException:
                    outcome = $"exception {describeException(ReadLengthPrefixed(records, ref position).ToArray())}";
                    break;
                default:
                    outcome = $"value {Convert.ToHexString(ReadLengthPrefixed(records, ref position))}";
                    break;
            }

            canonical.Add($"{access} {outcome}");
        }

        EndReference();
        return canonical;

        void EndReference()
        {
            if (openReference is not null)
            {
                canonical.Add($"{openReference} reference {openCount}");
                openReference = null;
            }
        }

        static ReadOnlySpan<byte> ReadLengthPrefixed(ReadOnlySpan<byte> records, ref int position)
        {
            var length = (int)ReadVarUInt(records, ref position);
            Assert.InRange(length, 0, records.Length - position);
            var bytes = records.Slice(position, length);
            position += length;
            return bytes;
        }

        static ulong ReadVarUInt(ReadOnlySpan<byte> records, ref int position)
        {
            ulong value = 0;
            for (var shift = 0; shift < 70; shift += 7)
            {
                Assert.True(position < records.Length, "Truncated varint.");
                var next = records[position++];
                value |= (ulong)(next & 0x7F) << shift;
                if (next < 0x80)
                {
                    return value;
                }
            }

            Assert.Fail("Overlong varint.");
            return 0;
        }
    }

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

    private readonly record struct Step(StepKind Kind, int State, int Operation, int Argument);

    /// <summary>One journal, whose activations recover from it in turn.</summary>
    private sealed class World : IAsyncDisposable
    {
        private readonly DurableTaskJournalHarness _harness;
        private readonly bool _recordsPositions;
        private readonly StorageControl _control = new();

        public World(string format, bool recordsPositions)
        {
            _recordsPositions = recordsPositions;
            _harness = new DurableTaskJournalHarness(
                format,
                wrapStorage: inner => new ControlledProvider(inner, _control),
                configureServices: static services => services.Configure<JsonJournalOptions>(static options => options.AddTypeInfoResolver(ReplayJsonContext.Default)));
        }

        public async Task<Activation> ActivateAsync()
        {
            var scope = _harness.CreateScope();
            try
            {
                var services = scope.ServiceProvider;
                var activation = new Activation(scope, services, _recordsPositions);
                await activation.Storage.ReadAsync(Cancellation);
                return activation;
            }
            catch
            {
                await scope.DisposeAsync();
                throw;
            }
        }

        /// <summary>Runs the steps as the execution's first run, stages and writes its records, and returns what it observed.</summary>
        public async Task<(List<string> Observed, int PositionalRecords)> RunFirstAsync(Step[] steps)
        {
            await using var activation = await ActivateAsync();
            var log = activation.CreateLog();
            var observed = new List<string>();
            var positionalRecords = 0;
            foreach (var step in steps)
            {
                switch (step.Kind)
                {
                    case StepKind.Read or StepKind.Write:
                        observed.Add(activation.RunAccess(log, step));
                        break;
                    case StepKind.Outside:
                        activation.Change(step);
                        break;
                    case StepKind.Flush:
                        positionalRecords += CountPositional(log, activation);
                        log.Flush();
                        break;
                    case StepKind.WriteJournal:
                        await activation.Manager.WriteStateAsync(Cancellation);
                        break;
                    case StepKind.Snapshot:
                        _control.RequestCompaction = true;
                        await activation.Manager.WriteStateAsync(Cancellation);
                        _control.RequestCompaction = false;
                        break;
                    case StepKind.ForceResolution:
                        _control.RequestCompaction = true;
                        for (var i = 0; i <= JournalPositionTracker.MaxDeferredSnapshots && activation.Manager.Positions.HasBarriers; i++)
                        {
                            await activation.Manager.WriteStateAsync(Cancellation);
                        }

                        _control.RequestCompaction = false;
                        break;
                }
            }

            positionalRecords += CountPositional(log, activation);
            log.Flush();
            await activation.Manager.WriteStateAsync(Cancellation);
            log.Close();
            return (observed, positionalRecords);
        }

        private static int CountPositional(DurableTaskAccessLog log, Activation activation) =>
            (DurableTaskAccessLog.FindPositionalRecords(log.PendingRecords, log.PendingStates)?.Count ?? 0)
            + (DurableTaskAccessLog.FindPositionalRecords(activation.Storage.GetAccessLog(Execution), [])?.Count ?? 0);

        public ValueTask DisposeAsync() => _harness.DisposeAsync();
    }

    private sealed class Activation : IAsyncDisposable
    {
        private readonly AsyncServiceScope _scope;
        private readonly Provider _provider = new();
        private readonly IDurableTaskAccessLogStorage _logStorage;
        private readonly Serializer _serializer;
        private readonly IDurableDictionary<string, string> _dictionary;
        private readonly IDurableDictionary<ReplayKey, string> _keyedByReference;
        private readonly IDurableValue<string> _text;
        private readonly IDurableValue<int> _number;
        private readonly IDurableValue<ReplayShape> _shape;
        private readonly IDurableValue<DateTime> _time;
        private readonly IDurableList<string> _list;
        private readonly IDurableQueue<string> _queue;
        private readonly IDurableSet<string> _set;
        private readonly IDurableLog<ReplayRecord> _log;
        private readonly IDurableTaskCompletionSource<string> _completion;
        private readonly IDurableList<long> _numbers;
        // Distinguishes written values: the body's writes count apart from other requests' writes, which a replay doesn't
        // repeat, so a replayed write has the arguments which the first run recorded.
        private int _created;
        private int _createdOutside;
        private bool _outside;

        public Activation(AsyncServiceScope scope, IServiceProvider services, bool recordsPositions)
        {
            _scope = scope;
            _dictionary = services.GetRequiredKeyedService<IDurableDictionary<string, string>>("dictionary");
            _keyedByReference = services.GetRequiredKeyedService<IDurableDictionary<ReplayKey, string>>("keyed-by-reference");
            _text = services.GetRequiredKeyedService<IDurableValue<string>>("text");
            _number = services.GetRequiredKeyedService<IDurableValue<int>>("number");
            _shape = services.GetRequiredKeyedService<IDurableValue<ReplayShape>>("shape");
            _time = services.GetRequiredKeyedService<IDurableValue<DateTime>>("time");
            _list = services.GetRequiredKeyedService<IDurableList<string>>("list");
            _queue = services.GetRequiredKeyedService<IDurableQueue<string>>("queue");
            _set = services.GetRequiredKeyedService<IDurableSet<string>>("set");
            _log = services.GetRequiredKeyedService<IDurableLog<ReplayRecord>>("log");
            _completion = services.GetRequiredKeyedService<IDurableTaskCompletionSource<string>>("completion");
            _numbers = services.GetRequiredKeyedService<IDurableList<long>>("numbers");
            Storage = services.GetRequiredService<DurableTaskGrainStorage>();
            Manager = (JournaledStateManager)services.GetRequiredService<IJournaledStateManager>();
            Manager.ContextProvider = _provider;
            _serializer = services.GetRequiredService<Serializer>();
            _logStorage = recordsPositions ? Storage : new ValueRecordingStorage(Storage);
        }

        public JournaledStateManager Manager { get; }

        public DurableTaskGrainStorage Storage { get; }

        public string DescribeException(byte[] encoded) =>
            _serializer.Deserialize<Exception>(encoded) is { } exception ? $"{exception.GetType().FullName}: {exception.Message}" : "null";

        public DurableTaskAccessLog CreateLog() =>
            new(new TestDurableTaskAttempt(Execution), _logStorage, _serializer, TaskScheduler.Current, static exception => new DurableTaskAttemptCanceledException(exception.Message, exception));

        /// <summary>Replays the steps' accesses, checks that the replay consumed every record, and returns what it observed.</summary>
        public List<string> Replay(Step[] steps)
        {
            var log = CreateLog();
            var observed = new List<string>();
            foreach (var step in steps)
            {
                if (step.Kind is StepKind.Read or StepKind.Write)
                {
                    observed.Add(RunAccess(log, step));
                }
            }

            Assert.False(log.IsReplaying);
            log.Close();
            return observed;
        }

        public string RunAccess(DurableTaskAccessLog log, Step step)
        {
            _provider.Log = log;
            try
            {
                return step.Kind is StepKind.Read ? Read(step) : Write(step);
            }
            catch (Exception exception) when (exception is not DurableTaskAttemptCanceledException)
            {
                return $"{exception.GetType().Name}: {exception.Message}";
            }
            finally
            {
                _provider.Log = null;
            }
        }

        /// <summary>Applies a step's write outside the body, as another request does.</summary>
        public void Change(Step step)
        {
            _outside = true;
            try
            {
                Write(step);
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
            {
            }
            finally
            {
                _outside = false;
            }
        }

        private string Read(Step step)
        {
            var text = Texts[step.Argument];
            return (step.State, step.Operation % 4) switch
            {
                (0, 0) => Show(_dictionary[text]),
                (0, 1) => _dictionary.TryGetValue(text, out var found) ? $"found {found}" : "missing",
                (0, 2) => Show(_dictionary.ContainsKey(text)),
                (0, _) => Show(_dictionary.Count),

                // Keys compare by reference: a key which the body reads from the dictionary finds its entry live.
                (1, 0) => _keyedByReference.Keys.FirstOrDefault() is { } key ? Show(_keyedByReference[key]) : "empty",
                (1, 1) => _keyedByReference.Keys.FirstOrDefault() is { } key ? (_keyedByReference.TryGetValue(key, out var value) ? $"found {value}" : "missing") : "empty",
                (1, _) => Show(_keyedByReference.Count),

                (2, _) => Show(_text.Value),
                (3, _) => Show(_number.Value),
                (4, _) => _shape.Value is { } shape ? $"{shape.GetType().Name} {shape.Name}" : "null",
                (5, _) => $"{_time.Value.Ticks} {_time.Value.Kind}",

                (6, 0) => Show(_list[0]),
                (6, 1) => Show(_list.Count),
                (6, 2) => Show(_list.Contains(text)),
                (6, _) => string.Join(",", _list),

                (7, 0) => Show(_queue.Peek()),
                (7, 1) => _queue.TryPeek(out var head) ? $"head {head}" : "empty",
                (7, 2) => Show(_queue.Count),
                (7, _) => Show(_set.Contains(text)),

                (8, 0) => Show(_log[_log.FirstSequence + step.Argument]),
                (8, 1) => _log.TryGet(_log.FirstSequence + step.Argument, out var entry) ? Show(entry) : "missing",
                (8, 2) => Show(_log.Count),
                (8, _) => string.Join(",", _log),

                (9, _) => ShowCompletion(_completion.State),

                (_, 0) => Show(_numbers[0]),
                (_, _) => string.Join(",", _numbers),
            };
        }

        private static string ShowCompletion(DurableTaskCompletionSourceState<string> state) =>
            $"{state.Status} {state.Value} {state.Exception?.GetType().Name} {state.Exception?.Message}";

        private int NextValue() => _outside ? 1_000 + _createdOutside++ : _created++;

        private string Write(Step step)
        {
            var text = Texts[step.Argument];
            switch (step.State, step.Operation % 2)
            {
                case (0, 0):
                    _dictionary[text] = $"{text}{NextValue()}";
                    return "set";
                case (0, _):
                    return Show(_dictionary.Remove(text));
                case (1, 0):
                    _keyedByReference.Add(new ReplayKey { Name = text }, $"{text}{NextValue()}");
                    return "added";
                case (1, _):
                    return _keyedByReference.Keys.FirstOrDefault() is { } key ? Show(_keyedByReference.Remove(key)) : "empty";
                case (2, _):
                    _text.Value = $"{text}{NextValue()}";
                    return "set";
                case (3, _):
                    _number.Value = step.Argument + NextValue();
                    return "set";
                case (4, 0):
                    _shape.Value = new ReplayCircle { Name = text, Radius = NextValue() };
                    return "set";
                case (4, _):
                    _shape.Value = new ReplayShape { Name = text };
                    return "set";
                case (5, _):
                    _time.Value = new DateTime(2026, 10, 19, 13, step.Argument, NextValue() % 60, (DateTimeKind)step.Argument);
                    return "set";
                case (6, 0):
                    _list.Add(text);
                    return "added";
                case (6, _):
                    _list.RemoveAt(0);
                    return "removed";
                case (7, 0):
                    _queue.Enqueue(text);
                    return "enqueued";
                case (7, _):
                    return Show(_set.Add(text));
                case (8, 0):
                    return Show(_log.Append(new ReplayRecord(text, NextValue())));
                case (8, _):
                    return _queue.TryDequeue(out var dequeued) ? $"dequeued {dequeued}" : "empty";
                case (9, 0):
                    return Show(_completion.TrySetResult(text));
                case (9, _):
                    return Show(_completion.TrySetException(new InvalidOperationException(text)));
                case (_, 0):
                    _numbers.Add(step.Argument * 1_000_000_000L + NextValue());
                    return "added";
                default:
                    _numbers.RemoveAt(0);
                    return "removed";
            }
        }

        private static string Show<T>(T value) => value?.ToString() ?? "null";

        public ValueTask DisposeAsync() => _scope.DisposeAsync();
    }

    // Stores records in the activation's logs, as the activation does, without offering positions, so every read records its value.
    private sealed class ValueRecordingStorage(DurableTaskGrainStorage inner) : IDurableTaskAccessLogStorage
    {
        public ReadOnlyMemory<byte> GetAccessLog(TaskId taskId) => inner.GetAccessLog(taskId);

        public void AppendAccessLog(TaskId taskId, ReadOnlySpan<byte> records) => inner.AppendAccessLog(taskId, records);

        public void AddPendingRecords(IDurableTaskPendingAccessRecords source) => inner.AddPendingRecords(source);
    }

    private sealed class Provider : JournaledStateContextProvider
    {
        public DurableTaskAccessLog? Log;

        public override JournaledStateContext? Current => Log is { IsActive: true } log ? log : null;
    }

    private sealed class StorageControl
    {
        public bool RequestCompaction { get; set; }
    }

    private sealed class ControlledProvider(IJournalStorageProvider inner, StorageControl control) : IJournalStorageProvider
    {
        public IJournalStorage CreateStorage(JournalId journalId) => new ControlledStorage(inner.CreateStorage(journalId), control);
    }

    private sealed class ControlledStorage(IJournalStorage inner, StorageControl control) : IJournalStorage
    {
        public bool IsCompactionRequested => control.RequestCompaction || inner.IsCompactionRequested;

        public ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken) => inner.ReadAsync(consumer, cancellationToken);

        public ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken) => inner.ReplaceAsync(value, cancellationToken);

        public ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken) => inner.AppendAsync(value, cancellationToken);

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

/// <summary>A dictionary key which compares by reference.</summary>
[GenerateSerializer]
internal sealed class ReplayKey
{
    [Id(0)]
    public string Name { get; set; } = "";

    public override string ToString() => Name;
}

/// <summary>An unsealed, polymorphic value, whose runtime type JSON doesn't keep.</summary>
[GenerateSerializer]
internal class ReplayShape
{
    [Id(0)]
    public string Name { get; set; } = "";
}

[GenerateSerializer]
internal sealed class ReplayCircle : ReplayShape
{
    [Id(0)]
    public int Radius { get; set; }
}

[GenerateSerializer]
internal sealed record ReplayRecord([property: Id(0)] string Name, [property: Id(1)] int Number);

/// <summary>JSON metadata for the journaled types of the positional replay properties.</summary>
[JsonSerializable(typeof(ReplayKey))]
[JsonSerializable(typeof(ReplayShape))]
[JsonSerializable(typeof(ReplayRecord))]
[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(long))]
internal sealed partial class ReplayJsonContext : JsonSerializerContext;
