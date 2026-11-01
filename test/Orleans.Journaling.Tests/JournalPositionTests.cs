using System.Buffers;
using CsCheck;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Serialization;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Verifies the journal's positions and compaction barriers: a positional read resolves, by replaying the journal's
/// history, to the value which the state held at its position, whichever writes, snapshots and deferrals followed it.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class JournalPositionTests : JournalingTestBase
{
    private static readonly string[] Keys = ["a", "b", "c"];
    private static readonly string[] Values = ["x", "y", "z"];

    [Fact]
    public async Task PositionsCountCommittedEntriesAndSurviveRecovery()
    {
        var storage = new ControlledStorage(new VolatileJournalStorage(OrleansBinaryJournalFormat.JournalFormatKey));
        var first = await ActivateAsync(storage);
        Assert.True(first.Manager.Positions.TryGetPosition(out var start));
        first.Dictionary["a"] = "x";
        first.Dictionary["b"] = "y";
        Assert.True(first.Manager.Positions.TryGetPosition(out var afterSets));
        Assert.Equal(start + 2, afterSets);

        // The value journals at the next write; until then, reads of it record values.
        first.Value.Value = "v";
        Assert.True(first.Manager.Positions.TryGetPosition(out var afterValue));
        Assert.Equal(afterSets, afterValue);
        await first.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.True(first.Manager.Positions.TryGetPosition(out var written));

        var second = await ActivateAsync(storage);
        Assert.True(second.Manager.Positions.TryGetPosition(out var recovered));
        Assert.Equal(written, recovered);

        // A snapshot replaces storage's content; positions restart at its entries, in this activation and in recovery.
        storage.RequestCompaction = true;
        second.Dictionary["c"] = "z";
        await second.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        storage.RequestCompaction = false;
        Assert.True(second.Manager.Positions.TryGetPosition(out var afterSnapshot));
        var third = await ActivateAsync(storage);
        Assert.True(third.Manager.Positions.TryGetPosition(out var recoveredSnapshot));
        Assert.Equal(afterSnapshot, recoveredSnapshot);
    }

    [Fact]
    public async Task BarrierDefersSnapshotsThenResolvesReadsBeforeOne()
    {
        var storage = new ControlledStorage(new VolatileJournalStorage(OrleansBinaryJournalFormat.JournalFormatKey));
        var activation = await ActivateAsync(storage);
        activation.Dictionary["a"] = "x";
        var read = activation.Probe.Read(activation.Dictionary, "dictionary", JournaledStateOperation.DictionaryTryGetValue, "a", (true, "x"));
        activation.Dictionary["a"] = "changed";
        storage.RequestCompaction = true;

        for (var i = 0; i < JournalPositionTracker.MaxDeferredSnapshots; i++)
        {
            await activation.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, storage.Replaces);
            Assert.False(read.IsResolved);
        }

        await activation.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, storage.Replaces);
        Assert.True(read.IsResolved);
        Assert.True(read.Matches(activation.Serializer));
        Assert.False(activation.Manager.Positions.HasBarriers);
    }

    [Fact]
    public async Task RecoveryReleasesABarrierWhichNoReadNeedsWithoutRereadingHistory()
    {
        var storage = new ControlledStorage(new VolatileJournalStorage(OrleansBinaryJournalFormat.JournalFormatKey));
        var first = await ActivateAsync(storage);
        first.Value.Value = "v";
        await first.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        first.Probe.Read(first.Value, "value", JournaledStateOperation.ValueGet, default(JournaledStateAccessNoArgument), "v");
        await first.Manager.WriteStateAsync(TestContext.Current.CancellationToken);

        // The probe's reads aren't journaled, so the recovered barrier guards none.
        storage.Reads = 0;
        var second = await ActivateAsync(storage);
        Assert.Equal(1, storage.Reads);
        Assert.False(second.Manager.Positions.HasBarriers);

        // The release journals with the next write.
        await second.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        storage.Reads = 0;
        await ActivateAsync(storage);
        Assert.Equal(1, storage.Reads);
    }

    [Fact]
    public async Task PositionalReadsResolveToTheValuesTheyObserved()
    {
        var steps = Gen.Frequency(
            (6, Gen.Select(Gen.Int[0, 4], Gen.Int[0, 2], Gen.Int[0, 2], static (kind, key, value) => new Step(StepKind.Change, kind, key, value))),
            (6, Gen.Select(Gen.Int[0, 6], Gen.Int[0, 2], static (kind, key) => new Step(StepKind.Read, kind, key, 0))),
            (2, Gen.Const(new Step(StepKind.Write, 0, 0, 0))),
            (1, Gen.Const(new Step(StepKind.Snapshot, 0, 0, 0))));

        await steps.Array[1, 40].SampleAsync(async steps =>
        {
            var storage = new ControlledStorage(new VolatileJournalStorage(OrleansBinaryJournalFormat.JournalFormatKey));
            var activation = await ActivateAsync(storage);
            var reads = new List<ProbeRead>();
            foreach (var step in steps)
            {
                switch (step.Kind)
                {
                    case StepKind.Change:
                        activation.Change(step);
                        break;
                    case StepKind.Read:
                        if (activation.Read(step) is { } read)
                        {
                            reads.Add(read);
                        }
                        break;
                    case StepKind.Write:
                        await activation.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
                        break;
                    default:
                        storage.RequestCompaction = true;
                        await activation.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
                        storage.RequestCompaction = false;
                        break;
                }
            }

            // Storage asks for compaction until the barrier's deferrals run out and its reads resolve.
            storage.RequestCompaction = true;
            for (var i = 0; i <= JournalPositionTracker.MaxDeferredSnapshots && reads.Exists(static read => !read.IsResolved); i++)
            {
                activation.Dictionary["a"] = $"write-{i}";
                await activation.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
            }

            foreach (var read in reads)
            {
                Assert.True(read.IsResolved, $"{read} wasn't resolved.");
                Assert.True(read.Matches(activation.Serializer), $"{read} resolved to another value.");
            }
        }, iter: 100);
    }

    [Fact]
    public async Task UnresolvableReadKeepsItsBarrierAndIsReported()
    {
        var storage = new ControlledStorage(new VolatileJournalStorage(OrleansBinaryJournalFormat.JournalFormatKey));
        var logger = new RecordingLogger();
        var activation = await ActivateAsync(storage, logger);
        activation.Dictionary["a"] = "x";

        // A read which its state's replica can't evaluate: the key is absent at the read's position.
        var read = activation.Probe.Read(activation.Dictionary, "dictionary", JournaledStateOperation.DictionaryGet, "missing", "never");
        storage.RequestCompaction = true;
        for (var window = 1; window <= 2; window++)
        {
            for (var i = 0; i <= JournalPositionTracker.MaxDeferredSnapshots; i++)
            {
                await activation.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
            }

            // Each window ends with one resolution, which reports the read and keeps the history which it needs.
            Assert.Equal(0, storage.Replaces);
            Assert.True(activation.Manager.Positions.HasBarriers);
            Assert.False(read.IsResolved);
            Assert.Equal(window, logger.Errors.Count(static message => message.Contains("couldn't be resolved", StringComparison.Ordinal)));
        }
    }

    /// <summary>
    /// A compaction barrier is the manager's bookkeeping for a read which records a position, so holding one leaves the
    /// staged change count known and unchanged, and the write which journals it stages nothing new. Messaging therefore
    /// retries a failed handler attempt which only read state in the same activation.
    /// </summary>
    [Fact]
    public async Task HoldingABarrierLeavesTheStagedChangeCountKnownAndUnchanged()
    {
        var storage = new ControlledStorage(new VolatileJournalStorage(OrleansBinaryJournalFormat.JournalFormatKey));
        var activation = await ActivateAsync(storage);
        activation.Dictionary["a"] = "x";
        await activation.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        var before = activation.Manager.StagedChangeCount;
        Assert.True(before >= 0, $"Staged change count {before} is unknown.");

        _ = activation.Probe.Read(activation.Dictionary, "dictionary", JournaledStateOperation.DictionaryTryGetValue, "a", (true, "x"));
        Assert.True(activation.Manager.Positions.HasBarriers);
        Assert.Equal(before, activation.Manager.StagedChangeCount);

        await activation.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.True(activation.Manager.Positions.HasBarriers);
        Assert.Equal(before, activation.Manager.StagedChangeCount);
    }

    private async Task<Activation> ActivateAsync(ControlledStorage storage, ILogger<JournaledStateManager>? logger = null)
    {
        var sut = CreateTestSystem(storage, logger: logger);
        var manager = (JournaledStateManager)sut.Manager;
        var dictionary = new DurableDictionary<string, string>(
            "dictionary",
            sut.Manager,
            new OrleansBinaryDurableDictionaryCommandCodec<string, string>(CodecProvider.GetCodec<string>(), CodecProvider.GetCodec<string>(), SessionPool));
        var value = new DurableValue<string>("value", sut.Manager, new OrleansBinaryDurableValueCommandCodec<string>(CodecProvider.GetCodec<string>(), SessionPool));
        var list = new DurableList<string>("list", sut.Manager, new OrleansBinaryDurableListCommandCodec<string>(CodecProvider.GetCodec<string>(), SessionPool));
        var serializer = ServiceProvider.GetRequiredService<Serializer>();
        var probe = new Probe(manager, serializer);
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        return new(manager, dictionary, value, list, probe, serializer);
    }

    private enum StepKind
    {
        Change,
        Read,
        Write,
        Snapshot,
    }

    private readonly record struct Step(StepKind Kind, int Operation, int Key, int Value);

    private sealed record Activation(
        JournaledStateManager Manager,
        DurableDictionary<string, string> Dictionary,
        DurableValue<string> Value,
        DurableList<string> List,
        Probe Probe,
        Serializer Serializer)
    {
        public void Change(Step step)
        {
            switch (step.Operation)
            {
                case 0:
                    Dictionary[Keys[step.Key]] = Values[step.Value];
                    break;
                case 1:
                    Dictionary.Remove(Keys[step.Key]);
                    break;
                case 2:
                    Value.Value = Values[step.Value];
                    break;
                case 3:
                    List.Add(Values[step.Value]);
                    break;
                default:
                    if (List.Count > 0)
                    {
                        List.RemoveAt(0);
                    }

                    break;
            }
        }

        public ProbeRead? Read(Step step)
        {
            var key = Keys[step.Key];
            return step.Operation switch
            {
                0 => Probe.TryRead(Value, "value", JournaledStateOperation.ValueGet, default(JournaledStateAccessNoArgument), Value.Value),
                1 => Probe.TryRead(Dictionary, "dictionary", JournaledStateOperation.DictionaryTryGetValue, key, Dictionary.TryGetValue(key, out var found) ? (true, found) : (false, default(string)!)),
                2 => Probe.TryRead(Dictionary, "dictionary", JournaledStateOperation.DictionaryContainsKey, key, Dictionary.ContainsKey(key)),
                3 => Probe.TryRead(Dictionary, "dictionary", JournaledStateOperation.DictionaryCount, default(JournaledStateAccessNoArgument), Dictionary.Count),
                4 => Probe.TryRead(List, "list", JournaledStateOperation.ListCount, default(JournaledStateAccessNoArgument), List.Count),
                5 => Probe.TryRead(List, "list", JournaledStateOperation.ListSnapshot, default(JournaledStateAccessNoArgument), List.ToArray()),
                _ => Probe.TryRead(List, "list", JournaledStateOperation.ListContains, Values[step.Key], List.Contains(Values[step.Key])),
            };
        }
    }

    /// <summary>A positional read and the value which the state held when it was made.</summary>
    private abstract class ProbeRead(long position, int stateId, JournaledStateOperation operation, byte[] argument)
    {
        public long Position { get; } = position;

        public int StateId { get; } = stateId;

        public JournaledStateOperation Operation { get; } = operation;

        public byte[] Argument { get; } = argument;

        public byte[]? Result { get; set; }

        public bool IsResolved => Result is not null;

        public abstract bool Matches(Serializer serializer);

        public override string ToString() => $"{Operation} at {Position}";
    }

    private sealed class ProbeRead<TResult>(long position, int stateId, JournaledStateOperation operation, byte[] argument, TResult expected)
        : ProbeRead(position, stateId, operation, argument)
    {
        public override bool Matches(Serializer serializer)
        {
            var actual = serializer.Deserialize<TResult>(Result!);
            return actual is Array array
                ? array.Cast<object>().SequenceEqual(((Array)(object)expected!).Cast<object>())
                : EqualityComparer<TResult>.Default.Equals(actual, expected);
        }
    }

    /// <summary>Holds positional reads, as an access log does, and keeps their results when the manager resolves them.</summary>
    // Like an access log, the probe holds its reads in memory and journals nothing, so it reports every change it stages.
    private sealed class Probe : IStateMachine, IReportsStagedChanges, IJournalPositionHolder
    {
        private readonly JournaledStateManager _manager;
        private readonly Serializer _serializer;
        private readonly List<ProbeRead> _reads = [];
        private List<ProbeRead>? _collected;

        public Probe(JournaledStateManager manager, Serializer serializer)
        {
            _manager = manager;
            _serializer = serializer;
            manager.RegisterStateMachine("probe", this);
        }

        /// <summary>Records a positional read, as an access log does, or returns <see langword="null"/> when the read records its value.</summary>
        public ProbeRead? TryRead<TArgument, TResult>(IReplicableState state, string name, JournaledStateOperation operation, TArgument argument, TResult expected)
        {
            var target = new JournaledStateAccessTarget(name, _manager, state);
            if (!_manager.Positions.TryGetPosition(target, out var position))
            {
                return null;
            }

            var encoded = new ArrayBufferWriter<byte>();
            if (typeof(TArgument) != typeof(JournaledStateAccessNoArgument))
            {
                _serializer.Serialize(argument, encoded);
            }

            var read = new ProbeRead<TResult>(position, target.Id, operation, encoded.WrittenSpan.ToArray(), expected);
            _reads.Add(read);
            _manager.Positions.HoldBarrier("probe", position);
            return read;
        }

        public ProbeRead Read<TArgument, TResult>(IReplicableState state, string name, JournaledStateOperation operation, TArgument argument, TResult expected) =>
            TryRead(state, name, operation, argument, expected) ?? throw new InvalidOperationException($"The read of '{name}' records its value.");

        bool IJournalPositionHolder.HoldsPositionalReads => _reads.Exists(static read => !read.IsResolved);

        JournalPositionalReads? IJournalPositionHolder.CollectPositionalReads(long fromPosition)
        {
            JournalPositionalReads? batch = null;
            _collected = [];
            foreach (var read in _reads)
            {
                if (!read.IsResolved && read.Position >= fromPosition)
                {
                    (batch ??= new(_serializer)).Add(read.Position, read.StateId, read.Operation, read.Argument);
                    _collected.Add(read);
                }
            }

            return batch;
        }

        void IJournalPositionHolder.ApplyResolvedReads(JournalPositionalReads reads)
        {
            for (var i = 0; i < reads.Count; i++)
            {
                if (reads.TryGetResult(i, out var result))
                {
                    _collected![i].Result = result.ToArray();
                }
            }
        }


        void IStateMachine.ReplayEntry(JournalEntry entry, JournalReplayContext context) => throw new NotSupportedException();

        void IStateMachine.Reset(JournalStreamWriter writer)
        {
        }

        void IStateMachine.WritePendingEntries(JournalStreamWriter writer)
        {
        }

        void IStateMachine.WriteSnapshot(JournalStreamWriter writer)
        {
        }
    }

    /// <summary>Volatile storage which counts reads and replacements and requests compaction on demand.</summary>
    private sealed class ControlledStorage(IJournalStorage inner) : IJournalStorage
    {
        public bool RequestCompaction { get; set; }

        public int Reads { get; set; }

        public int Replaces { get; private set; }

        public bool IsCompactionRequested => RequestCompaction || inner.IsCompactionRequested;

        public ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken)
        {
            Reads++;
            return inner.ReadAsync(consumer, cancellationToken);
        }

        public ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
        {
            Replaces++;
            return inner.ReplaceAsync(value, cancellationToken);
        }

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

    /// <summary>Records the messages of errors which the manager logs.</summary>
    private sealed class RecordingLogger : ILogger<JournaledStateManager>
    {
        public List<string> Errors { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error)
            {
                Errors.Add(formatter(state, exception));
            }
        }
    }
}
