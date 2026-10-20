using System.Buffers.Binary;
using CsCheck;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Verifies local transactions over journaled state: a committed transaction is equivalent to applying its changes
/// directly, an aborted one leaves no trace in live state or in the journal, and a crash at any point recovers all of a
/// transaction's changes or none.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class JournalTransactionTests : JournalingTestBase
{
    private static readonly string[] Keys = ["a", "b", "c"];
    private static readonly string[] Values = ["x", "y", "z"];

    [Fact]
    public async Task CommittedTransactionAppliesItsChangesAndRecovers()
    {
        var storage = new VolatileJournalStorage(OrleansBinaryJournalFormat.JournalFormatKey);
        var activation = await ActivateAsync(storage);
        activation.Dictionary["a"] = "x";

        var result = await activation.Manager.RunTransactionAsync(async _ =>
        {
            activation.Dictionary["b"] = "y";
            await Task.Yield();
            activation.Value.Value = "v";
            activation.List.Add("item");
            return activation.Dictionary.Count;
        }, TestContext.Current.CancellationToken);

        Assert.Equal(2, result);
        Assert.Equal("y", activation.Dictionary["b"]);
        Assert.Equal("v", activation.Value.Value);
        Assert.Equal(["item"], activation.List);
        await activation.Manager.WriteStateAsync(TestContext.Current.CancellationToken);

        var recovered = await ActivateAsync(storage);
        Assert.Equal(activation.Dictionary.OrderBy(static pair => pair.Key), recovered.Dictionary.OrderBy(static pair => pair.Key));
        Assert.Equal("v", recovered.Value.Value);
        Assert.Equal(["item"], recovered.List);
    }

    [Fact]
    public async Task CommittedTransactionAddsOnlyItsOwnEntriesInOneAppend()
    {
        var storage = new VolatileJournalStorage(OrleansBinaryJournalFormat.JournalFormatKey);
        var activation = await ActivateAsync(storage);
        await activation.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        var segments = storage.Segments.Count;

        await activation.Manager.RunTransactionAsync(_ =>
        {
            activation.Dictionary["a"] = "x";
            activation.Value.Value = "v";
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);
        await activation.Manager.WriteStateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(segments + 1, storage.Segments.Count);
        var streams = ReadStreamIds(storage.Segments[^1]);
        // The dictionary's set and the value's set, with no markers.
        Assert.Equal(2, streams.Count);
        Assert.All(streams, static id => Assert.True(id >= 8, $"Stream {id} is reserved."));
    }

    [Fact]
    public async Task ReadOnlyTransactionJournalsNothing()
    {
        var storage = new VolatileJournalStorage(OrleansBinaryJournalFormat.JournalFormatKey);
        var activation = await ActivateAsync(storage);
        activation.Dictionary["a"] = "x";
        await activation.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        var segments = storage.Segments.Count;

        var value = await activation.Manager.RunTransactionAsync(_ => Task.FromResult(activation.Dictionary["a"]), TestContext.Current.CancellationToken);
        await activation.Manager.WriteStateAsync(TestContext.Current.CancellationToken);

        Assert.Equal("x", value);
        Assert.Equal(segments, storage.Segments.Count);
    }

    [Fact]
    public async Task AbortedTransactionLeavesNoTraceLiveOrInTheJournal()
    {
        var storage = new VolatileJournalStorage(OrleansBinaryJournalFormat.JournalFormatKey);
        var activation = await ActivateAsync(storage);
        activation.Dictionary["a"] = "x";
        activation.Value.Value = "durable";
        await activation.Manager.WriteStateAsync(TestContext.Current.CancellationToken);

        // Changes from before the transaction which no write has captured yet survive its abort.
        activation.Dictionary["b"] = "pending";
        activation.Value.Value = "pending";
        var bytesBefore = storage.Segments.Sum(static segment => segment.Length);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => activation.Manager.RunTransactionAsync(async _ =>
        {
            activation.Dictionary["a"] = "changed";
            activation.Dictionary.Remove("b");
            activation.Value.Value = "changed";
            activation.List.Add("item");
            await Task.Yield();
            throw new InvalidOperationException("abort");
        }, TestContext.Current.CancellationToken));

        Assert.Equal("abort", exception.Message);
        Assert.Equal(storage.Segments.Sum(static segment => segment.Length), bytesBefore);
        Assert.Equal("x", activation.Dictionary["a"]);
        Assert.Equal("pending", activation.Dictionary["b"]);
        Assert.Equal("pending", activation.Value.Value);
        Assert.Empty(activation.List);

        await activation.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        var recovered = await ActivateAsync(storage);
        Assert.Equal("x", recovered.Dictionary["a"]);
        Assert.Equal("pending", recovered.Dictionary["b"]);
        Assert.Equal("pending", recovered.Value.Value);
        Assert.Empty(recovered.List);
    }

    [Fact]
    public async Task ValueStagedBeforeAnAbortedTransactionIsAcknowledgedByTheWriteWhichPersistsIt()
    {
        var events = new List<string>();
        var storage = new GatedStorage(new VolatileJournalStorage(OrleansBinaryJournalFormat.JournalFormatKey), events);
        var activation = await ActivateAsync(storage);
        activation.Value.OnPersisted = () => events.Add($"persisted:{activation.Value.LiveValue}");
        activation.Value.Value = "durable";
        await activation.Manager.WriteStateAsync(TestContext.Current.CancellationToken);

        // The transaction's first write stages the value set before it into the journal buffer, and its abort rebuilds the
        // value from storage and that buffer.
        activation.Value.Value = "staged";
        await Assert.ThrowsAsync<InvalidOperationException>(() => activation.Manager.RunTransactionAsync(_ =>
        {
            activation.Value.Value = "aborted";
            throw new InvalidOperationException("abort");
        }, TestContext.Current.CancellationToken));
        Assert.Equal("staged", activation.Value.Value);

        // The next write persists the staged value, and acknowledges it once its append completes.
        events.Clear();
        var appending = storage.HoldNextAppend();
        var write = activation.Manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        await appending.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Empty(events);
        storage.ReleaseAppend();
        await write;
        Assert.Equal(["append", "persisted:staged"], events);

        var recovered = await ActivateAsync(storage);
        Assert.Equal("staged", recovered.Value.Value);
    }

    [Fact]
    public async Task WriteDuringTransactionExcludesItsChanges()
    {
        var storage = new VolatileJournalStorage(OrleansBinaryJournalFormat.JournalFormatKey);
        var activation = await ActivateAsync(storage);
        activation.Dictionary["a"] = "x";

        await activation.Manager.RunTransactionAsync(async _ =>
        {
            activation.Dictionary["b"] = "y";
            activation.Value.Value = "v";

            // Another flow writes while the transaction is open, and the process crashes before the commit's write.
            await RunOutsideTransaction(() => activation.Manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask());
            var crashed = await ActivateAsync(storage);
            Assert.Equal("x", crashed.Dictionary["a"]);
            Assert.False(crashed.Dictionary.ContainsKey("b"));
            Assert.Null(crashed.Value.Value);
        }, TestContext.Current.CancellationToken);

        await activation.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        var recovered = await ActivateAsync(storage);
        Assert.Equal("y", recovered.Dictionary["b"]);
        Assert.Equal("v", recovered.Value.Value);
    }

    [Fact]
    public async Task OtherFlowsReadTheTransactionsChangesAndCantChangeItsStates()
    {
        var activation = await ActivateAsync(new VolatileJournalStorage(OrleansBinaryJournalFormat.JournalFormatKey));
        await activation.Manager.RunTransactionAsync(async _ =>
        {
            activation.Dictionary["a"] = "x";
            await RunOutsideTransaction(() =>
            {
                Assert.Equal("x", activation.Dictionary["a"]);
                Assert.Throws<InvalidOperationException>(() => activation.Dictionary["a"] = "other");

                // States which the transaction didn't change stay writable.
                activation.List.Add("outside");
                return Task.CompletedTask;
            });
        }, TestContext.Current.CancellationToken);

        Assert.Equal("x", activation.Dictionary["a"]);
        Assert.Equal(["outside"], activation.List);
        activation.Dictionary["a"] = "after";
    }

    [Fact]
    public async Task SnapshotDuringTransactionBecomesAnAppend()
    {
        var storage = new SnapshotStorage(new VolatileJournalStorage(OrleansBinaryJournalFormat.JournalFormatKey));
        var activation = await ActivateAsync(storage);
        activation.Dictionary["a"] = "x";
        await activation.Manager.WriteStateAsync(TestContext.Current.CancellationToken);

        await activation.Manager.RunTransactionAsync(async _ =>
        {
            activation.Dictionary["b"] = "y";
            activation.List.Add("pending");
            await RunOutsideTransaction(async () =>
            {
                activation.Value.Value = "outside";
                storage.RequestCompaction = true;
                await activation.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
            });

            Assert.Equal(0, storage.Replaces);
        }, TestContext.Current.CancellationToken);

        var crashed = await ActivateAsync(storage);
        Assert.Equal("outside", crashed.Value.Value);
        Assert.False(crashed.Dictionary.ContainsKey("b"));

        // Once the transaction ends, the requested snapshot is written.
        await activation.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, storage.Replaces);
        var recovered = await ActivateAsync(storage);
        Assert.Equal("y", recovered.Dictionary["b"]);
        Assert.Equal(["pending"], recovered.List);
    }

    [Fact]
    public async Task TransactionCodeCantWriteOrDeleteTheJournal()
    {
        var activation = await ActivateAsync(new VolatileJournalStorage(OrleansBinaryJournalFormat.JournalFormatKey));
        await activation.Manager.RunTransactionAsync(async _ =>
        {
            activation.Dictionary["a"] = "x";
            await Assert.ThrowsAsync<InvalidOperationException>(() => activation.Manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => activation.Manager.DeleteStateAsync(TestContext.Current.CancellationToken).AsTask());
        }, TestContext.Current.CancellationToken);

        Assert.Equal("x", activation.Dictionary["a"]);
    }

    [Fact]
    public async Task FailedNestedTransactionAbortsItsEnclosingTransaction()
    {
        var activation = await ActivateAsync(new VolatileJournalStorage(OrleansBinaryJournalFormat.JournalFormatKey));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => activation.Manager.RunTransactionAsync(async _ =>
        {
            activation.Dictionary["a"] = "outer";
            try
            {
                await activation.Manager.RunTransactionAsync(_ =>
                {
                    activation.Dictionary["b"] = "inner";
                    throw new FormatException("inner");
                }, TestContext.Current.CancellationToken);
            }
            catch (FormatException)
            {
            }
        }, TestContext.Current.CancellationToken));

        Assert.IsType<FormatException>(exception.InnerException);
        Assert.Empty(activation.Dictionary);
    }

    [Fact]
    public async Task CompletionSourcePublishesOnlyAfterTheCommitsWrite()
    {
        var storage = new VolatileJournalStorage(OrleansBinaryJournalFormat.JournalFormatKey);
        var activation = await ActivateAsync(storage);
        await activation.Manager.RunTransactionAsync(async _ =>
        {
            Assert.True(activation.Completion.TrySetResult(5));

            // A write during the transaction doesn't make its completion durable.
            await RunOutsideTransaction(() => activation.Manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask());
            Assert.False(activation.Completion.Task.IsCompleted);
        }, TestContext.Current.CancellationToken);

        Assert.False(activation.Completion.Task.IsCompleted);
        await activation.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(5, await activation.Completion.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AbortedLogAppendRollsBackAndKeepsOtherCodesDrafts()
    {
        var sut = CreateTestSystem(new VolatileJournalStorage(OrleansBinaryJournalFormat.JournalFormatKey));
        var manager = (JournaledStateManager)sut.Manager;
        var log = new DurableLog<string>("log", sut.Manager, new OrleansBinaryDurableLogCommandCodec<string>(CodecProvider.GetCodec<string>(), SessionPool));
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        log.Append("committed");
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        log.Append("pending");
        using var draft = log.BeginDraft();
        draft.Write("draft");

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RunTransactionAsync(_ =>
        {
            log.Append("aborted");
            throw new InvalidOperationException("abort");
        }, TestContext.Current.CancellationToken));

        Assert.Equal(["committed", "pending"], log);
        Assert.True(draft.IsOpen);
        Assert.Equal(2, draft.Commit("draft"));
        Assert.Equal(["committed", "pending", "draft"], log);
    }

    [Fact]
    public async Task WatchStartedDuringTransactionBeginsWithItsOutcome()
    {
        var activation = await ActivateAsync(new VolatileJournalStorage(OrleansBinaryJournalFormat.JournalFormatKey));
        activation.Dictionary["a"] = "x";
        await activation.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        Task<DictionaryChange<string, string>>? first = null;

        await Assert.ThrowsAsync<InvalidOperationException>(() => activation.Manager.RunTransactionAsync(async _ =>
        {
            activation.Dictionary["a"] = "uncommitted";
            await RunOutsideTransaction(() =>
            {
                first = FirstAsync(activation.Dictionary.WatchAsync(DurableWatchMode.Staged, cts.Token));
                return Task.CompletedTask;
            });

            await Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.False(first!.IsCompleted);
            throw new InvalidOperationException("abort");
        }, TestContext.Current.CancellationToken));

        var change = await first!;
        Assert.Equal(DictionaryChangeKind.Resync, change.Kind);
        Assert.Equal([KeyValuePair.Create("a", "x")], change.Snapshot);

        static async Task<DictionaryChange<string, string>> FirstAsync(IAsyncEnumerable<DictionaryChange<string, string>> changes)
        {
            await foreach (var change in changes)
            {
                return change;
            }

            throw new InvalidOperationException("The watch ended.");
        }
    }

    [Fact]
    public async Task CrashBeforeTheCommitsWriteLosesTheWholeTransactionAndAfterItKeepsIt()
    {
        var storage = new VolatileJournalStorage(OrleansBinaryJournalFormat.JournalFormatKey);
        var activation = await ActivateAsync(storage);
        activation.Dictionary["a"] = "x";
        await activation.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await activation.Manager.RunTransactionAsync(_ =>
        {
            activation.Dictionary["b"] = "y";
            activation.Value.Value = "v";
            activation.List.Add("item");
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);

        // Committed, but no write has captured the commit: a crash recovers none of the transaction.
        var beforeWrite = await ActivateAsync(storage);
        Assert.Equal("x", beforeWrite.Dictionary["a"]);
        Assert.False(beforeWrite.Dictionary.ContainsKey("b"));
        Assert.Null(beforeWrite.Value.Value);
        Assert.Empty(beforeWrite.List);

        // The next write appends the whole transaction at once: a crash afterwards recovers all of it.
        await activation.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        var afterWrite = await ActivateAsync(storage);
        Assert.Equal("y", afterWrite.Dictionary["b"]);
        Assert.Equal("v", afterWrite.Value.Value);
        Assert.Equal(["item"], afterWrite.List);
    }

    /// <summary>
    /// Runs random histories of direct changes, transactions which commit or abort (with writes and crashes from other
    /// flows while they're open), writes, snapshots and recoveries against a model which applies committed transactions
    /// directly and drops aborted ones.
    /// </summary>
    [Fact]
    public async Task TransactionsMatchTheirDirectApplicationAcrossWritesSnapshotsAndCrashes()
    {
        var change = Gen.Select(Gen.Int[0, 5], Gen.Int[0, 2], Gen.Int[0, 2], static (kind, key, value) => new Change(kind, key, value));
        var step = Gen.Frequency(
            (4, change.Select(static change => new Step(StepKind.Change, [change], false, false))),
            (4, Gen.Select(change.Array[0, 5], Gen.Bool, Gen.Bool, static (changes, abort, crashInside) => new Step(StepKind.Transaction, changes, abort, crashInside))),
            (2, Gen.Const(new Step(StepKind.Write, [], false, false))),
            (1, Gen.Const(new Step(StepKind.Snapshot, [], false, false))),
            (1, Gen.Const(new Step(StepKind.Recover, [], false, false))));

        await step.Array[1, 30].SampleAsync(async steps =>
        {
            var storage = new SnapshotStorage(new VolatileJournalStorage(OrleansBinaryJournalFormat.JournalFormatKey));
            var activation = await ActivateAsync(storage);
            var model = new Model();
            var durable = new Model();
            foreach (var current in steps)
            {
                switch (current.Kind)
                {
                    case StepKind.Change:
                        activation.Apply(current.Changes[0]);
                        model.Apply(current.Changes[0]);
                        break;
                    case StepKind.Transaction:
                        var attempt = model.Clone();
                        try
                        {
                            await activation.Manager.RunTransactionAsync(async _ =>
                            {
                                foreach (var change in current.Changes)
                                {
                                    activation.Apply(change);
                                    attempt.Apply(change);
                                }

                                if (current.CrashInside)
                                {
                                    // Another flow writes, which makes the changes from before the transaction durable,
                                    // and a second activation recovers what storage holds meanwhile.
                                    await RunOutsideTransaction(() => activation.Manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask());
                                    model.MarkDurable();
                                    attempt.InheritDurable(model);
                                    var crashed = await ActivateAsync(storage);
                                    Assert.Equal(model.Capture(), crashed.Capture());
                                }

                                if (current.Abort)
                                {
                                    throw new AbortException();
                                }
                            }, TestContext.Current.CancellationToken);
                            model = attempt;
                        }
                        catch (AbortException)
                        {
                        }

                        break;
                    case StepKind.Write:
                        await activation.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
                        model.MarkDurable();
                        break;
                    case StepKind.Snapshot:
                        storage.RequestCompaction = true;
                        await activation.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
                        storage.RequestCompaction = false;
                        model.MarkDurable();
                        break;
                    default:
                        activation = await ActivateAsync(storage);
                        model = model.Durable();
                        break;
                }

                Assert.Equal(model.Capture(), activation.Capture());
            }

            await activation.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
            var recovered = await ActivateAsync(storage);
            Assert.Equal(model.Capture(), recovered.Capture());
        }, iter: 100);
    }

    // Runs work in a flow which isn't the transaction's, as another request or host work would.
    private static Task RunOutsideTransaction(Func<Task> work)
    {
        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(work);
        }
    }

    private static List<uint> ReadStreamIds(byte[] segment)
    {
        var result = new List<uint>();
        foreach (var start in GetEntryBoundaries(segment).SkipLast(1))
        {
            result.Add(BinaryPrimitives.ReadUInt32LittleEndian(segment.AsSpan(start + 5)));
        }

        return result;
    }

    // The offsets at which the binary format's entries start, followed by the segment's length.
    private static List<int> GetEntryBoundaries(byte[] segment)
    {
        var result = new List<int>();
        var offset = 0;
        while (offset < segment.Length)
        {
            result.Add(offset);
            offset += 5 + (int)BinaryPrimitives.ReadUInt32LittleEndian(segment.AsSpan(offset + 1));
        }

        result.Add(offset);
        return result;
    }

    private async Task<Activation> ActivateAsync(IJournalStorage storage)
    {
        var sut = CreateTestSystem(storage);
        var manager = (JournaledStateManager)sut.Manager;
        var dictionary = new DurableDictionary<string, string>(
            "dictionary",
            sut.Manager,
            new OrleansBinaryDurableDictionaryCommandCodec<string, string>(CodecProvider.GetCodec<string>(), CodecProvider.GetCodec<string>(), SessionPool));
        var value = new DurableValue<string>("value", sut.Manager, new OrleansBinaryDurableValueCommandCodec<string>(CodecProvider.GetCodec<string>(), SessionPool));
        var list = new DurableList<string>("list", sut.Manager, new OrleansBinaryDurableListCommandCodec<string>(CodecProvider.GetCodec<string>(), SessionPool));
        var completion = new DurableTaskCompletionSource<int>(
            "completion",
            sut.Manager,
            new OrleansBinaryDurableTaskCompletionSourceCommandCodec<int>(CodecProvider.GetCodec<int>(), CodecProvider.GetCodec<Exception>(), SessionPool),
            ServiceProvider.GetRequiredService<DeepCopier<int>>(),
            ServiceProvider.GetRequiredService<DeepCopier<Exception>>());
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        return new(manager, dictionary, value, list, completion);
    }

    private enum StepKind
    {
        Change,
        Transaction,
        Write,
        Snapshot,
        Recover,
    }

    private readonly record struct Change(int Kind, int Key, int Value);

    private sealed record Step(StepKind Kind, Change[] Changes, bool Abort, bool CrashInside);

    private sealed class AbortException : Exception;

    private sealed record Activation(
        JournaledStateManager Manager,
        DurableDictionary<string, string> Dictionary,
        DurableValue<string> Value,
        DurableList<string> List,
        DurableTaskCompletionSource<int> Completion)
    {
        public void Apply(Change change)
        {
            switch (change.Kind)
            {
                case 0:
                    Dictionary[Keys[change.Key]] = Values[change.Value];
                    break;
                case 1:
                    Dictionary.Remove(Keys[change.Key]);
                    break;
                case 2:
                    Value.Value = Values[change.Value];
                    break;
                case 3:
                    List.Add(Values[change.Value]);
                    break;
                case 4:
                    if (List.Count > 0)
                    {
                        List.RemoveAt(0);
                    }

                    break;
                default:
                    Dictionary.Clear();
                    break;
            }
        }

        public string Capture() =>
            $"{string.Join(',', Dictionary.OrderBy(static pair => pair.Key).Select(static pair => $"{pair.Key}={pair.Value}"))}|{Value.Value}|{string.Join(',', List)}";
    }

    // The expected content: live, and as of the last write.
    private sealed class Model
    {
        private SortedDictionary<string, string> _dictionary = new(StringComparer.Ordinal);
        private string? _value;
        private List<string> _list = [];
        private Model? _durable;

        public void Apply(Change change)
        {
            switch (change.Kind)
            {
                case 0:
                    _dictionary[Keys[change.Key]] = Values[change.Value];
                    break;
                case 1:
                    _dictionary.Remove(Keys[change.Key]);
                    break;
                case 2:
                    _value = Values[change.Value];
                    break;
                case 3:
                    _list.Add(Values[change.Value]);
                    break;
                case 4:
                    if (_list.Count > 0)
                    {
                        _list.RemoveAt(0);
                    }

                    break;
                default:
                    _dictionary.Clear();
                    break;
            }
        }

        public Model Clone() => new()
        {
            _dictionary = new(_dictionary, StringComparer.Ordinal),
            _value = _value,
            _list = [.. _list],
            _durable = _durable,
        };

        public void MarkDurable() => _durable = Clone();

        public void InheritDurable(Model other) => _durable = other._durable;

        public Model Durable()
        {
            var durable = (_durable ?? new Model()).Clone();
            durable._durable = durable.Clone();
            return durable;
        }

        public string Capture() =>
            $"{string.Join(',', _dictionary.Select(static pair => $"{pair.Key}={pair.Value}"))}|{_value}|{string.Join(',', _list)}";

        public override bool Equals(object? obj) => obj is Model other && other.Capture() == Capture();

        public override int GetHashCode() => Capture().GetHashCode(StringComparison.Ordinal);

        public override string ToString() => Capture();
    }

    /// <summary>Volatile storage which holds an append on demand and logs each completed append.</summary>
    private sealed class GatedStorage(VolatileJournalStorage inner, List<string> events) : IJournalStorage
    {
        private TaskCompletionSource? _started;
        private TaskCompletionSource? _gate;
        private bool _holdNext;

        public bool IsCompactionRequested => inner.IsCompactionRequested;

        /// <summary>Holds the next append until <see cref="ReleaseAppend"/>.</summary>
        /// <returns>A task which completes once the append waits.</returns>
        public Task HoldNextAppend()
        {
            _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _holdNext = true;
            return _started.Task;
        }

        public void ReleaseAppend() => _gate!.SetResult();

        public ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken) => inner.ReadAsync(consumer, cancellationToken);

        public async ValueTask AppendAsync(System.Buffers.ReadOnlySequence<byte> value, CancellationToken cancellationToken)
        {
            if (_holdNext)
            {
                _holdNext = false;
                _started!.SetResult();
                await _gate!.Task.WaitAsync(cancellationToken);
            }

            await inner.AppendAsync(value, cancellationToken);
            events.Add("append");
        }

        public ValueTask ReplaceAsync(System.Buffers.ReadOnlySequence<byte> value, CancellationToken cancellationToken) => inner.ReplaceAsync(value, cancellationToken);

        public ValueTask DeleteAsync(CancellationToken cancellationToken) => inner.DeleteAsync(cancellationToken);
    }

    /// <summary>Volatile storage which requests compaction on demand and counts replacements.</summary>
    private sealed class SnapshotStorage(VolatileJournalStorage inner) : IJournalStorage
    {
        public bool RequestCompaction { get; set; }

        public int Replaces { get; private set; }

        public bool IsCompactionRequested => RequestCompaction || inner.IsCompactionRequested;


        public ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken) => inner.ReadAsync(consumer, cancellationToken);


        public ValueTask ReplaceAsync(System.Buffers.ReadOnlySequence<byte> value, CancellationToken cancellationToken)
        {
            Replaces++;
            RequestCompaction = false;
            return inner.ReplaceAsync(value, cancellationToken);
        }

        public ValueTask AppendAsync(System.Buffers.ReadOnlySequence<byte> value, CancellationToken cancellationToken) => inner.AppendAsync(value, cancellationToken);

        public ValueTask DeleteAsync(CancellationToken cancellationToken) => inner.DeleteAsync(cancellationToken);
    }
}
