using System.Buffers;
using CsCheck;
using Microsoft.Extensions.DependencyInjection;
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

        // The value journals at the next write, unless a positional read stages it first.
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
                        reads.Add(activation.Read(step));
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

    private async Task<Activation> ActivateAsync(ControlledStorage storage)
    {
        var sut = CreateTestSystem(storage);
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

        public ProbeRead Read(Step step)
        {
            var key = Keys[step.Key];
            return step.Operation switch
            {
                0 => Probe.Read(Value, "value", JournaledStateOperation.ValueGet, default(JournaledStateAccessNoArgument), Value.Value),
                1 => Probe.Read(Dictionary, "dictionary", JournaledStateOperation.DictionaryTryGetValue, key, Dictionary.TryGetValue(key, out var found) ? (true, found) : (false, default(string)!)),
                2 => Probe.Read(Dictionary, "dictionary", JournaledStateOperation.DictionaryContainsKey, key, Dictionary.ContainsKey(key)),
                3 => Probe.Read(Dictionary, "dictionary", JournaledStateOperation.DictionaryCount, default(JournaledStateAccessNoArgument), Dictionary.Count),
                4 => Probe.Read(List, "list", JournaledStateOperation.ListCount, default(JournaledStateAccessNoArgument), List.Count),
                5 => Probe.Read(List, "list", JournaledStateOperation.ListSnapshot, default(JournaledStateAccessNoArgument), List.ToArray()),
                _ => Probe.Read(List, "list", JournaledStateOperation.ListContains, Values[step.Key], List.Contains(Values[step.Key])),
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
    private sealed class Probe : IStateMachine, IJournalPositionHolder
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

        public ProbeRead Read<TArgument, TResult>(IReplicableState state, string name, JournaledStateOperation operation, TArgument argument, TResult expected)
        {
            state.StagePendingChanges();
            Assert.True(_manager.Positions.TryGetPosition(out var position));
            var encoded = new ArrayBufferWriter<byte>();
            if (typeof(TArgument) != typeof(JournaledStateAccessNoArgument))
            {
                _serializer.Serialize(argument, encoded);
            }

            var read = new ProbeRead<TResult>(position, new JournaledStateAccessTarget(name).Id, operation, encoded.WrittenSpan.ToArray(), expected);
            _reads.Add(read);
            _manager.Positions.HoldBarrier("probe", position);
            return read;
        }

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

        void IJournalPositionHolder.OnPositionsResolved()
        {
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
}
