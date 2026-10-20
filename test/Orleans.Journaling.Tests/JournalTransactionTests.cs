using System.Buffers.Binary;
using CsCheck;
using Orleans.Journaling.Json;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Verifies local transactions over journaled state: a committed transaction is equivalent to applying its changes
/// directly and is durable when its call returns, an aborted one leaves no trace in live state or in the journal, and a
/// crash at any point recovers all of a transaction's changes or none.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class JournalTransactionTests
{
    private const string Binary = OrleansBinaryJournalFormat.JournalFormatKey;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly string[] Keys = ["a", "b", "c"];
    private static readonly string[] Values = ["x", "y", "z"];

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(TransactionTestHost.Formats), MemberType = typeof(TransactionTestHost))]
    public async Task CommittedTransactionIsDurableWhenItsCallReturns(string format)
    {
        using var host = new TransactionTestHost(format);
        var storage = new ControlledJournalStorage(format);
        var activation = await host.ActivateAsync(storage);
        activation.Dictionary["a"] = "x";

        var result = await activation.Manager.RunTransactionAsync(async _ =>
        {
            activation.Dictionary["b"] = "y";
            await Task.Yield();
            activation.Value.Value = "v";
            activation.List.Add("item");
            activation.Queue.Enqueue("queued");
            activation.Set.Add("member");
            activation.Log.Append("entry");
            Assert.True(activation.Completion.TrySetResult(7));
            return activation.Dictionary.Count;
        }, Cancellation);

        Assert.Equal(2, result);
        Assert.Equal(7, await activation.Completion.Task.WaitAsync(Timeout, Cancellation));

        // No further write: the call returned once a write which holds the commit completed.
        var recovered = await host.ActivateAsync(storage);
        Assert.Equal(activation.Capture(), recovered.Capture());
    }

    [Fact]
    public async Task CommittedTransactionAddsOnlyItsOwnEntriesInOneAppend()
    {
        using var host = new TransactionTestHost(Binary);
        var storage = new ControlledJournalStorage(Binary);
        var activation = await host.ActivateAsync(storage);
        await activation.Manager.WriteStateAsync(Cancellation);
        var segments = storage.Inner.Segments.Count;

        await activation.Manager.RunTransactionAsync(_ =>
        {
            activation.Dictionary["a"] = "x";
            activation.Value.Value = "v";
            return Task.CompletedTask;
        }, Cancellation);

        Assert.Equal(segments + 1, storage.Inner.Segments.Count);
        var streams = ReadStreamIds(storage.Inner.Segments[^1]);

        // The dictionary's set and the value's set.
        Assert.Equal(2, streams.Count);
        Assert.All(streams, static id => Assert.True(id >= 8, $"Stream {id} is reserved."));
    }

    /// <summary>
    /// A transaction's operations commit their entries into its segment, which stages them when the commit appends it to the
    /// journal: the staged change count advances then, and an abort, which discards the segment, leaves it unchanged. Messaging
    /// compares the count before and after a handler attempt, so a failed attempt fails its activation only when it staged
    /// changes which a retry's write would commit.
    /// </summary>
    [Fact]
    public async Task StagedChangeCountAdvancesWhenATransactionCommitsAndNotWhenItAborts()
    {
        using var host = new TransactionTestHost(Binary);
        var activation = await host.ActivateAsync(new ControlledJournalStorage(Binary));
        var manager = activation.Manager;
        var start = manager.StagedChangeCount;
        Assert.True(start >= 0, $"Staged change count {start} is unknown.");

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RunTransactionAsync(_ =>
        {
            activation.Dictionary["a"] = "x";
            activation.Log.Append("aborted");
            Assert.Equal(start, manager.StagedChangeCount);
            throw new InvalidOperationException("abort");
        }, Cancellation));
        Assert.Equal(start, manager.StagedChangeCount);

        await manager.RunTransactionAsync(_ =>
        {
            activation.Dictionary["a"] = "y";
            Assert.Equal(start, manager.StagedChangeCount);
            return Task.CompletedTask;
        }, Cancellation);
        Assert.True(manager.StagedChangeCount > start);
    }

    [Fact]
    public async Task ReadOnlyTransactionJournalsNothing()
    {
        using var host = new TransactionTestHost(Binary);
        var storage = new ControlledJournalStorage(Binary);
        var activation = await host.ActivateAsync(storage);
        activation.Dictionary["a"] = "x";
        await activation.Manager.WriteStateAsync(Cancellation);
        var appends = storage.Appends;

        var value = await activation.Manager.RunTransactionAsync(_ => Task.FromResult(activation.Dictionary["a"]), Cancellation);

        Assert.Equal("x", value);
        Assert.Equal(appends, storage.Appends);
    }

    [Theory]
    [MemberData(nameof(TransactionTestHost.Formats), MemberType = typeof(TransactionTestHost))]
    public async Task AbortedTransactionLeavesNoTraceLiveOrInTheJournal(string format)
    {
        using var host = new TransactionTestHost(format);
        var storage = new ControlledJournalStorage(format);
        var activation = await host.ActivateAsync(storage);
        activation.Dictionary["a"] = "x";
        activation.Value.Value = "durable";
        activation.Queue.Enqueue("first");
        activation.Log.Append("first");
        await activation.Manager.WriteStateAsync(Cancellation);

        // Changes from before the transaction which no write has captured yet survive its abort.
        activation.Dictionary["b"] = "pending";
        activation.Value.Value = "pending";
        var expected = activation.Capture();
        var appends = storage.Appends;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => activation.Manager.RunTransactionAsync(async _ =>
        {
            activation.Dictionary["a"] = "changed";
            activation.Dictionary.Remove("b");
            activation.Value.Value = "changed";
            activation.List.Add("item");
            Assert.Equal("first", activation.Queue.Dequeue());
            activation.Set.Add("member");
            activation.Log.Append("aborted");
            activation.Completion.TrySetResult(1);
            await Task.Yield();
            throw new InvalidOperationException("abort");
        }, Cancellation));

        Assert.Equal("abort", exception.Message);
        Assert.Equal(appends, storage.Appends);
        Assert.Equal(expected, activation.Capture());

        await activation.Manager.WriteStateAsync(Cancellation);
        var recovered = await host.ActivateAsync(storage);
        Assert.Equal(expected, recovered.Capture());
    }

    [Fact]
    public async Task ValueStagedBeforeAnAbortedTransactionIsAcknowledgedByTheWriteWhichPersistsIt()
    {
        using var host = new TransactionTestHost(Binary);
        var storage = new ControlledJournalStorage(Binary);
        var activation = await host.ActivateAsync(storage);
        var persisted = new List<(string? Value, int Appends)>();
        activation.Value.OnPersisted = () => persisted.Add((activation.Value.LiveValue, storage.Appends));
        activation.Value.Value = "durable";
        await activation.Manager.WriteStateAsync(Cancellation);

        // The transaction's first write stages the value set before it into the journal buffer, and its abort rebuilds the
        // value from storage and that buffer.
        activation.Value.Value = "staged";
        await Assert.ThrowsAsync<InvalidOperationException>(() => activation.Manager.RunTransactionAsync(_ =>
        {
            activation.Value.Value = "aborted";
            throw new InvalidOperationException("abort");
        }, Cancellation));
        Assert.Equal("staged", activation.Value.Value);

        // The next write persists the staged value, and acknowledges it once its append completes.
        persisted.Clear();
        var held = storage.HoldAppends();
        var write = activation.Manager.WriteStateAsync(Cancellation).AsTask();
        await held.WaitAsync(Timeout, Cancellation);
        Assert.Empty(persisted);
        var appends = storage.Appends;
        storage.ReleaseAppends();
        await write.WaitAsync(Timeout, Cancellation);
        Assert.Equal([("staged", appends + 1)], persisted);

        var recovered = await host.ActivateAsync(storage);
        Assert.Equal("staged", recovered.Value.Value);
    }
    [Fact]
    public async Task WriteDuringTransactionExcludesItsChanges()
    {
        using var host = new TransactionTestHost(Binary);
        var storage = new ControlledJournalStorage(Binary);
        var activation = await host.ActivateAsync(storage);
        activation.Dictionary["a"] = "x";

        await activation.Manager.RunTransactionAsync(async _ =>
        {
            activation.Dictionary["b"] = "y";
            activation.Value.Value = "v";

            // Another flow writes while the transaction is open, and the process crashes before the commit's write.
            await RunOutsideTransaction(() => activation.Manager.WriteStateAsync(Cancellation).AsTask());
            var crashed = await host.ActivateAsync(storage);
            Assert.Equal("x", crashed.Dictionary["a"]);
            Assert.False(crashed.Dictionary.ContainsKey("b"));
            Assert.Null(crashed.Value.Value);
        }, Cancellation);

        var recovered = await host.ActivateAsync(storage);
        Assert.Equal("y", recovered.Dictionary["b"]);
        Assert.Equal("v", recovered.Value.Value);
    }

    [Fact]
    public async Task OtherFlowsReadTheTransactionsChangesAndCantChangeItsStates()
    {
        using var host = new TransactionTestHost(Binary);
        var activation = await host.ActivateAsync(new ControlledJournalStorage(Binary));
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
        }, Cancellation);

        Assert.Equal("x", activation.Dictionary["a"]);
        Assert.Equal(["outside"], activation.List);
        activation.Dictionary["a"] = "after";
    }

    [Fact]
    public async Task SnapshotDuringTransactionBecomesAnAppend()
    {
        using var host = new TransactionTestHost(Binary);
        var storage = new ControlledJournalStorage(Binary);
        var activation = await host.ActivateAsync(storage);
        activation.Dictionary["a"] = "x";
        await activation.Manager.WriteStateAsync(Cancellation);

        await activation.Manager.RunTransactionAsync(async _ =>
        {
            activation.Dictionary["b"] = "y";
            activation.List.Add("pending");
            await RunOutsideTransaction(async () =>
            {
                activation.Value.Value = "outside";
                storage.RequestCompaction = true;
                await activation.Manager.WriteStateAsync(Cancellation);
            });

            Assert.Equal(0, storage.Replaces);
            var crashed = await host.ActivateAsync(storage);
            Assert.Equal("outside", crashed.Value.Value);
            Assert.False(crashed.Dictionary.ContainsKey("b"));
        }, Cancellation);

        // The commit's write is the requested snapshot.
        Assert.Equal(1, storage.Replaces);
        var recovered = await host.ActivateAsync(storage);
        Assert.Equal("y", recovered.Dictionary["b"]);
        Assert.Equal(["pending"], recovered.List);
    }

    [Fact]
    public async Task TransactionCodeCantWriteOrDeleteTheJournal()
    {
        using var host = new TransactionTestHost(Binary);
        var activation = await host.ActivateAsync(new ControlledJournalStorage(Binary));
        await activation.Manager.RunTransactionAsync(async _ =>
        {
            activation.Dictionary["a"] = "x";
            await Assert.ThrowsAsync<InvalidOperationException>(() => activation.Manager.WriteStateAsync(Cancellation).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => activation.Manager.DeleteStateAsync(Cancellation).AsTask());
        }, Cancellation);

        Assert.Equal("x", activation.Dictionary["a"]);
    }

    [Fact]
    public async Task FailedNestedTransactionDoomsItsEnclosingTransaction()
    {
        using var host = new TransactionTestHost(Binary);
        var activation = await host.ActivateAsync(new ControlledJournalStorage(Binary));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => activation.Manager.RunTransactionAsync(async _ =>
        {
            activation.Dictionary["a"] = "outer";
            try
            {
                await activation.Manager.RunTransactionAsync(_ =>
                {
                    activation.Dictionary["b"] = "inner";
                    throw new FormatException("inner");
                }, Cancellation);
            }
            catch (FormatException)
            {
            }

            // The doomed transaction changes nothing more.
            Assert.Throws<InvalidOperationException>(() => activation.Dictionary["c"] = "after");
        }, Cancellation));

        Assert.IsType<FormatException>(exception.InnerException);
        Assert.Empty(activation.Dictionary);
    }

    [Fact]
    public async Task TransactionsDontNestAcrossManagers()
    {
        using var host = new TransactionTestHost(Binary);
        var first = await host.ActivateAsync(new ControlledJournalStorage(Binary));
        var second = await host.ActivateAsync(new ControlledJournalStorage(Binary));

        await Assert.ThrowsAsync<InvalidOperationException>(() => first.Manager.RunTransactionAsync(async _ =>
        {
            first.Dictionary["a"] = "x";
            await Assert.ThrowsAsync<NotSupportedException>(() => second.Manager.RunTransactionAsync(_ => Task.CompletedTask, Cancellation));
            throw new InvalidOperationException("abort");
        }, Cancellation));

        Assert.Empty(first.Dictionary);
        Assert.Null(second.Manager.OpenTransaction);
    }

    [Fact]
    public async Task RecordedCodeRunsTransactionsThroughItsHost()
    {
        using var host = new TransactionTestHost(Binary);
        var activation = await host.ActivateAsync(new ControlledJournalStorage(Binary));
        activation.Manager.ContextProvider = new FixedContextProvider();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => activation.Manager.RunTransactionAsync(_ => Task.CompletedTask, Cancellation));

        Assert.Contains("DurableTaskTransactions.RunAsync", exception.Message, StringComparison.Ordinal);
        Assert.Null(activation.Manager.OpenTransaction);
    }

    [Fact]
    public async Task TransactionCodeCantWaitForItsOwnChanges()
    {
        using var host = new TransactionTestHost(Binary);
        var activation = await host.ActivateAsync(new ControlledJournalStorage(Binary));
        await activation.Manager.RunTransactionAsync(async _ =>
        {
            activation.Dictionary["a"] = "x";
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await foreach (var change in activation.Dictionary.WatchAsync(DurableWatchMode.Staged, Cancellation))
                {
                }
            });

            Assert.True(activation.Completion.TrySetResult(1));
            Assert.Throws<InvalidOperationException>(() => (object)activation.Completion.Task);

            // Other flows can wait for it; held states publish nothing until the commit is durable.
            await RunOutsideTransaction(() =>
            {
                Assert.False(activation.Completion.Task.IsCompleted);
                return Task.CompletedTask;
            });
        }, Cancellation);

        Assert.Equal(1, await activation.Completion.Task.WaitAsync(Timeout, Cancellation));
    }

    [Fact]
    public async Task CompletionSourceCompletesOnceTheCommitsWriteCompletes()
    {
        using var host = new TransactionTestHost(Binary);
        var storage = new ControlledJournalStorage(Binary);
        var activation = await host.ActivateAsync(storage);
        var held = storage.HoldAppends();

        var call = activation.Manager.RunTransactionAsync(_ =>
        {
            Assert.True(activation.Completion.TrySetResult(5));
            return Task.CompletedTask;
        }, Cancellation);

        // The commit's append waits at the gate, so neither the call nor the completion source's task completes.
        await held.WaitAsync(Timeout, Cancellation);
        Assert.False(call.IsCompleted);
        Assert.False(activation.Completion.Task.IsCompleted);

        storage.ReleaseAppends();
        await call.WaitAsync(Timeout, Cancellation);
        Assert.True(activation.Completion.Task.IsCompletedSuccessfully);
        Assert.Equal(5, await activation.Completion.Task);
    }

    [Theory]
    [MemberData(nameof(TransactionTestHost.Formats), MemberType = typeof(TransactionTestHost))]
    public async Task CommitReturnsOnlyAfterTheAppendWhichHoldsIt(string format)
    {
        using var host = new TransactionTestHost(format);
        var storage = new ControlledJournalStorage(format);
        var activation = await host.ActivateAsync(storage);
        activation.Dictionary["a"] = "x";
        await activation.Manager.WriteStateAsync(Cancellation);
        var held = storage.HoldAppends();

        var call = activation.Manager.RunTransactionAsync(_ =>
        {
            activation.Dictionary["b"] = "y";
            activation.Value.Value = "v";
            activation.Log.Append("entry");
            return Task.CompletedTask;
        }, Cancellation);

        // The commit's append is in flight: the call waits for it, and storage holds none of the transaction yet.
        await held.WaitAsync(Timeout, Cancellation);
        Assert.False(call.IsCompleted);
        var crashed = await host.ActivateAsync(storage);
        Assert.Equal("x", crashed.Dictionary["a"]);
        Assert.False(crashed.Dictionary.ContainsKey("b"));
        Assert.Null(crashed.Value.Value);
        Assert.Empty(crashed.Log);

        storage.ReleaseAppends();
        await call.WaitAsync(Timeout, Cancellation);
        var recovered = await host.ActivateAsync(storage);
        Assert.Equal(activation.Capture(), recovered.Capture());
    }

    [Theory]
    [MemberData(nameof(TransactionTestHost.Formats), MemberType = typeof(TransactionTestHost))]
    public async Task FailedCommitAppendFailsTheCallAndTheNextActivationRecoversNone(string format)
    {
        using var host = new TransactionTestHost(format);
        var storage = new ControlledJournalStorage(format);
        var activation = await host.ActivateAsync(storage);
        activation.Dictionary["a"] = "x";
        await activation.Manager.WriteStateAsync(Cancellation);
        var failure = new IOException("The append failed.");
        storage.FailNextAppend(failure);

        var thrown = await Assert.ThrowsAsync<IOException>(() => activation.Manager.RunTransactionAsync(_ =>
        {
            activation.Dictionary["b"] = "y";
            activation.List.Add("item");
            activation.Log.Append("entry");
            return Task.CompletedTask;
        }, Cancellation).WaitAsync(Timeout, Cancellation));

        Assert.Same(failure, thrown);

        // The failure fenced the journal, so the activation recovers from the journal, which holds none of the transaction.
        await Assert.ThrowsAsync<InvalidOperationException>(() => activation.Manager.WriteStateAsync(Cancellation).AsTask());
        var recovered = await host.ActivateAsync(storage);
        Assert.Equal("x", recovered.Dictionary["a"]);
        Assert.False(recovered.Dictionary.ContainsKey("b"));
        Assert.Empty(recovered.List);
        Assert.Empty(recovered.Log);
    }

    [Fact]
    public async Task FailedPreparationOfTheCommitsWriteFencesTheJournal()
    {
        using var host = new TransactionTestHost(Binary);
        var storage = new ControlledJournalStorage(Binary);
        var activation = await host.ActivateAsync(storage, static (manager, _, _) => new FailingPreparationState(manager));
        var preparation = (FailingPreparationState)activation.Extra!;
        activation.Dictionary["a"] = "x";
        await activation.Manager.WriteStateAsync(Cancellation);
        preparation.Fail = true;

        await Assert.ThrowsAsync<JournaledStatePreCommitException>(() => activation.Manager.RunTransactionAsync(_ =>
        {
            activation.Dictionary["b"] = "y";
            return Task.CompletedTask;
        }, Cancellation).WaitAsync(Timeout, Cancellation));

        // A later write would have made the transaction durable after its call failed; the fenced journal writes nothing.
        preparation.Fail = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => activation.Manager.WriteStateAsync(Cancellation).AsTask());
        var recovered = await host.ActivateAsync(storage);
        Assert.Equal("x", recovered.Dictionary["a"]);
        Assert.False(recovered.Dictionary.ContainsKey("b"));
    }

    [Fact]
    public async Task ConcurrentCommitsShareOneAppend()
    {
        using var host = new TransactionTestHost(Binary);
        var storage = new ControlledJournalStorage(Binary);
        var activation = await host.ActivateAsync(storage);
        await activation.Manager.WriteStateAsync(Cancellation);
        var appends = storage.Appends;
        var held = storage.HoldAppends();

        // An append is in flight, so the commits which follow queue the next write.
        activation.List.Add("before");
        var inFlight = activation.Manager.WriteStateAsync(Cancellation).AsTask();
        await held.WaitAsync(Timeout, Cancellation);
        var first = activation.Manager.RunTransactionAsync(_ =>
        {
            activation.Dictionary["a"] = "first";
            return Task.CompletedTask;
        }, Cancellation);
        var second = activation.Manager.RunTransactionAsync(_ =>
        {
            activation.Dictionary["b"] = "second";
            return Task.CompletedTask;
        }, Cancellation);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        storage.ReleaseAppends();
        await Task.WhenAll(inFlight, first, second).WaitAsync(Timeout, Cancellation);

        Assert.Equal(appends + 2, storage.Appends);
        Assert.Equal(2, ReadStreamIds(storage.Inner.Segments[^1]).Count);
        var recovered = await host.ActivateAsync(storage);
        Assert.Equal(activation.Capture(), recovered.Capture());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RoutedWriteHoldsEveryStateWhoseEntriesItJournals(bool abort)
    {
        using var host = new TransactionTestHost(Binary);
        var storage = new ControlledJournalStorage(Binary);
        var activation = await host.ActivateAsync(storage, static (manager, _, _) => new CountingState(manager));
        var counting = (CountingState)activation.Extra!;
        counting.Increment();
        await activation.Manager.WriteStateAsync(Cancellation);
        var target = new JournaledStateAccessTarget("dictionary", activation.Manager);

        var call = activation.Manager.RunTransactionAsync(async _ =>
        {
            // The dictionary's operation journals an entry of the counting state, as an outbox send journals the inbox's
            // acceptance of a message which the grain sends to itself.
            Assert.True(JournaledStateContext.TryGetCurrent(target, out var context));
            context.Write(target, JournaledStateOperation.DictionarySet, counting, static state => state.Increment());
            Assert.True(activation.Manager.IsHeldByTransaction(counting));

            await RunOutsideTransaction(() => activation.Manager.WriteStateAsync(Cancellation).AsTask());
            var crashed = await host.ActivateAsync(storage, static (manager, _, _) => new CountingState(manager));
            Assert.Equal(1, ((CountingState)crashed.Extra!).Count);
            if (abort)
            {
                throw new InvalidOperationException("abort");
            }
        }, Cancellation);

        if (abort)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => call);
        }
        else
        {
            await call;
        }

        var expected = abort ? 1 : 2;
        Assert.Equal(expected, counting.Count);
        var recovered = await host.ActivateAsync(storage, static (manager, _, _) => new CountingState(manager));
        Assert.Equal(expected, ((CountingState)recovered.Extra!).Count);
    }

    [Fact]
    public async Task OtherFlowsTakeNoPositionsAndCaptureNoHeldChanges()
    {
        using var host = new TransactionTestHost(Binary);
        var storage = new ControlledJournalStorage(Binary);
        var activation = await host.ActivateAsync(storage);
        activation.Value.Value = "durable";
        await activation.Manager.WriteStateAsync(Cancellation);
        Assert.True(activation.Manager.Positions.TryGetPosition(out _));

        await Assert.ThrowsAsync<InvalidOperationException>(() => activation.Manager.RunTransactionAsync(async _ =>
        {
            activation.Value.Value = "uncommitted";
            activation.Completion.TrySetResult(3);
            activation.List.Add("uncommitted");

            // A durable task body which reads a held state records its value, since the journal doesn't hold the
            // transaction's changes, and a write by another flow captures none of them.
            await RunOutsideTransaction(async () =>
            {
                Assert.False(activation.Manager.Positions.TryGetPosition(out var position));
                await activation.Manager.WriteStateAsync(Cancellation);
            });

            throw new InvalidOperationException("abort");
        }, Cancellation));

        Assert.Equal("durable", activation.Value.Value);
        await activation.Manager.WriteStateAsync(Cancellation);
        var recovered = await host.ActivateAsync(storage);
        Assert.Equal("durable", recovered.Value.Value);
        Assert.Equal(DurableTaskCompletionSourceStatus.Pending, recovered.Completion.State.Status);
        Assert.Empty(recovered.List);
    }

    [Fact]
    public async Task StateWhichDoesntTakePartInTransactionsCantChangeInOne()
    {
        using var host = new TransactionTestHost(Binary);
        var activation = await host.ActivateAsync(
            new ControlledJournalStorage(Binary),
            static (manager, shared, services) => (new CountingState(manager), new JournaledPersistentState<string>("persistent", manager, shared, services)));
        var (counting, persistent) = ((CountingState, JournaledPersistentState<string>))activation.Extra!;
        var storage = (IPersistentState<string>)persistent;

        await activation.Manager.RunTransactionAsync(async _ =>
        {
            Assert.Throws<InvalidOperationException>(counting.Increment);
            Assert.Throws<InvalidOperationException>(() => storage.State);
            Assert.Throws<InvalidOperationException>(() => storage.State = "changed");
            await Assert.ThrowsAsync<InvalidOperationException>(() => storage.ClearStateAsync(Cancellation));
        }, Cancellation);

        Assert.Equal(0, counting.Count);
        counting.Increment();
        storage.State = "after";
        await storage.WriteStateAsync(Cancellation);
        Assert.Equal(1, counting.Count);
    }

    [Fact]
    public async Task AbortAfterTheJournalFailedEndsTheTransactionWithTheFailure()
    {
        using var host = new TransactionTestHost(Binary);
        var storage = new ControlledJournalStorage(Binary);
        var activation = await host.ActivateAsync(storage);
        var failure = new IOException("The append failed.");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => activation.Manager.RunTransactionAsync(async _ =>
        {
            activation.Dictionary["a"] = "x";
            storage.FailNextAppend(failure);
            await RunOutsideTransaction(() =>
            {
                activation.List.Add("outside");
                return Assert.ThrowsAsync<IOException>(() => activation.Manager.WriteStateAsync(Cancellation).AsTask());
            });
            throw new FormatException("abort");
        }, Cancellation).WaitAsync(Timeout, Cancellation));

        Assert.Same(failure, exception.InnerException);
        Assert.Null(activation.Manager.OpenTransaction);
    }

    [Fact]
    public async Task AbortWhileTheManagerStopsEndsTheTransaction()
    {
        using var host = new TransactionTestHost(Binary);
        var activation = await host.ActivateAsync(new ControlledJournalStorage(Binary));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => activation.Manager.RunTransactionAsync(async _ =>
        {
            activation.Dictionary["a"] = "x";
            await RunOutsideTransaction(() => ((ILifecycleObserver)activation.Manager).OnStop(Cancellation));
            throw new FormatException("abort");
        }, Cancellation).WaitAsync(Timeout, Cancellation));

        Assert.Null(activation.Manager.OpenTransaction);
    }

    [Fact]
    public async Task TransactionWhichDefersTooManySnapshotsAborts()
    {
        using var host = new TransactionTestHost(Binary);
        var storage = new ControlledJournalStorage(Binary);
        var activation = await host.ActivateAsync(storage);
        activation.Dictionary["a"] = "x";
        await activation.Manager.WriteStateAsync(Cancellation);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => activation.Manager.RunTransactionAsync(async _ =>
        {
            activation.Dictionary["a"] = "uncommitted";
            storage.RequestCompaction = true;
            for (var i = 0; i <= JournalPositionTracker.MaxDeferredSnapshots; i++)
            {
                await RunOutsideTransaction(() => activation.Manager.WriteStateAsync(Cancellation).AsTask());
            }

            Assert.Equal(0, storage.Replaces);
            Assert.NotNull(activation.Manager.OpenTransaction!.DoomedBy);
            Assert.Throws<InvalidOperationException>(() => activation.Dictionary["b"] = "y");
        }, Cancellation));

        Assert.NotNull(exception.InnerException);
        Assert.Equal("x", activation.Dictionary["a"]);
        await activation.Manager.WriteStateAsync(Cancellation);
        Assert.Equal(1, storage.Replaces);
    }

    [Fact]
    public async Task AbortedLogAppendRollsBackAndKeepsOtherCodesDrafts()
    {
        using var host = new TransactionTestHost(Binary);
        var activation = await host.ActivateAsync(new ControlledJournalStorage(Binary));
        var log = activation.Log;
        log.Append("committed");
        await activation.Manager.WriteStateAsync(Cancellation);
        log.Append("pending");
        using var draft = log.BeginDraft();
        draft.Write("draft");

        await Assert.ThrowsAsync<InvalidOperationException>(() => activation.Manager.RunTransactionAsync(_ =>
        {
            log.Append("aborted");
            throw new InvalidOperationException("abort");
        }, Cancellation));

        Assert.Equal(["committed", "pending"], log);
        Assert.True(draft.IsOpen);
        Assert.Equal(2, draft.Commit("draft"));
        Assert.Equal(["committed", "pending", "draft"], log);
    }

    [Fact]
    public async Task WatchStartedDuringTransactionBeginsWithItsOutcome()
    {
        using var host = new TransactionTestHost(Binary);
        var activation = await host.ActivateAsync(new ControlledJournalStorage(Binary));
        activation.Dictionary["a"] = "x";
        await activation.Manager.WriteStateAsync(Cancellation);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        cts.CancelAfter(Timeout);
        Task<DictionaryChange<string, string>>? first = null;

        await Assert.ThrowsAsync<InvalidOperationException>(() => activation.Manager.RunTransactionAsync(async _ =>
        {
            activation.Dictionary["a"] = "uncommitted";
            await RunOutsideTransaction(() =>
            {
                first = FirstAsync(activation.Dictionary.WatchAsync(DurableWatchMode.Staged, cts.Token));
                return Task.CompletedTask;
            });

            throw new InvalidOperationException("abort");
        }, Cancellation));

        // The watch began after the abort, so its first change is the committed content.
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

    /// <summary>
    /// Runs random histories of direct changes to every kind of state, transactions which commit or abort (with nested
    /// transactions, writes by other flows to held and unheld states, and crashes while they're open), writes, snapshots
    /// and recoveries against a model which applies committed transactions directly and drops aborted ones. A committed
    /// transaction is durable when its call returns.
    /// </summary>
    [Theory]
    [MemberData(nameof(TransactionTestHost.Formats), MemberType = typeof(TransactionTestHost))]
    public async Task TransactionsMatchTheirDirectApplicationAcrossWritesSnapshotsAndCrashes(string format)
    {
        using var host = new TransactionTestHost(format);
        var change = Gen.Select(Gen.Enum<ChangeKind>(), Gen.Int[0, 2], Gen.Int[0, 2], static (kind, key, value) => new Change(kind, key, value));
        var transaction = Gen.Select(change.Array[0, 4], change.Array[0, 2], Gen.Bool, change.Array[0, 2], Gen.Bool, Gen.Bool)
            .Select(static values => new Step(StepKind.Transaction, values.Item1, values.Item2, values.Item3, values.Item4, values.Item5, values.Item6));
        var step = Gen.Frequency(
            (4, change.Select(static change => new Step(StepKind.Change, [change], [], false, [], false, false))),
            (5, transaction),
            (2, Gen.Const(new Step(StepKind.Write, [], [], false, [], false, false))),
            (1, Gen.Const(new Step(StepKind.Snapshot, [], [], false, [], false, false))),
            (1, Gen.Const(new Step(StepKind.Recover, [], [], false, [], false, false))));

        await step.Array[1, 25].SampleAsync(async steps =>
        {
            var storage = new ControlledJournalStorage(format);
            var activation = await host.ActivateAsync(storage);
            var model = new Model();
            var index = 0;
            foreach (var current in steps)
            {
                index++;
                switch (current.Kind)
                {
                    case StepKind.Change:
                        Assert.Equal(model.Apply(current.Changes[0]), Apply(activation, current.Changes[0]));
                        break;
                    case StepKind.Transaction:
                        model = await RunTransactionStepAsync(host, storage, activation, model, current);
                        break;
                    case StepKind.Write:
                        await activation.Manager.WriteStateAsync(Cancellation);
                        model.MarkDurable();
                        break;
                    case StepKind.Snapshot:
                        storage.RequestCompaction = true;
                        await activation.Manager.WriteStateAsync(Cancellation);
                        storage.RequestCompaction = false;
                        model.MarkDurable();
                        break;
                    default:
                        await activation.Manager.DisposeAsync();
                        activation = await host.ActivateAsync(storage);
                        model = model.Durable();
                        break;
                }

                Assert.True(model.Capture() == activation.Capture(), $"After step {index} of {string.Join("; ", steps.Select(static step => step.ToString()))}: expected {model.Capture()}, actual {activation.Capture()}");
            }

            await activation.Manager.WriteStateAsync(Cancellation);
            var recovered = await host.ActivateAsync(storage);
            Assert.Equal(model.Capture(), recovered.Capture());
        }, iter: 60);
    }

    private static async Task<Model> RunTransactionStepAsync(TransactionTestHost host, ControlledJournalStorage storage, TransactionActivation activation, Model model, Step step)
    {
        var attempt = model.Clone();
        var held = new HashSet<StateKind>();
        var journals = false;
        var writesAtCommit = 0;
        try
        {
            await activation.Manager.RunTransactionAsync(async _ =>
            {
                foreach (var change in step.Changes)
                {
                    ApplyInTransaction(change);
                }

                if (step.Inner.Length > 0 || step.InnerAborts)
                {
                    try
                    {
                        await activation.Manager.RunTransactionAsync(async _ =>
                        {
                            foreach (var change in step.Inner)
                            {
                                ApplyInTransaction(change);
                            }

                            await Task.Yield();
                            if (step.InnerAborts)
                            {
                                throw new AbortException();
                            }
                        }, Cancellation);
                    }
                    catch (AbortException)
                    {
                        // The failure dooms the outer transaction, which aborts when its body ends.
                    }
                }

                foreach (var change in step.Outside)
                {
                    // Another flow's write to a state which the transaction holds throws; otherwise it applies, and survives
                    // the transaction's abort. A change which writes nothing, such as removing from an empty list, does nothing.
                    var isHeld = held.Contains(StateOf(change.Kind));
                    var thrown = await RunOutsideTransaction(() => Task.FromResult(Record.Exception(() => Apply(activation, change))));
                    if (isHeld && attempt.WouldWrite(change))
                    {
                        Assert.IsType<InvalidOperationException>(thrown);
                    }
                    else
                    {
                        Assert.Null(thrown);
                        if (!isHeld)
                        {
                            attempt.Apply(change);
                            model.Apply(change);
                        }
                    }
                }

                if (step.CrashInside)
                {
                    // Another flow writes, which makes the changes from before the transaction durable, and a second
                    // activation recovers what storage holds meanwhile.
                    await RunOutsideTransaction(() => activation.Manager.WriteStateAsync(Cancellation).AsTask());
                    model.MarkDurable();
                    attempt.InheritDurable(model);
                    var crashed = await host.ActivateAsync(storage);
                    Assert.Equal(model.Capture(), crashed.Capture());
                    await crashed.Manager.DisposeAsync();
                }

                if (step.Abort)
                {
                    throw new AbortException();
                }

                writesAtCommit = storage.Appends + storage.Replaces;
            }, Cancellation);

            Assert.False(step.InnerAborts);

            // A commit which journals entries returns once its write, which holds the whole journal buffer, completed.
            var wrote = storage.Appends + storage.Replaces > writesAtCommit;
            Assert.True(wrote || !journals, "The commit journaled entries without writing them.");
            if (wrote)
            {
                attempt.MarkDurable();
            }

            return attempt;
        }
        catch (AbortException) when (step.Abort)
        {
            return model;
        }
        catch (InvalidOperationException exception) when (step.InnerAborts && exception.InnerException is AbortException)
        {
            return model;
        }

        void ApplyInTransaction(Change change)
        {
            if (Apply(activation, change))
            {
                held.Add(StateOf(change.Kind));

                // These changes always journal an entry; others, such as removing an absent key, may journal none.
                journals |= change.Kind is ChangeKind.ValueSet or ChangeKind.ListAdd or ChangeKind.ListRemoveFirst
                    or ChangeKind.QueueEnqueue or ChangeKind.QueueDequeue or ChangeKind.LogAppend;
            }

            attempt.Apply(change);
        }
    }

    // Applies a change and returns whether it wrote the state.
    private static bool Apply(TransactionActivation activation, Change change)
    {
        var key = Keys[change.Key];
        var value = Values[change.Value];
        switch (change.Kind)
        {
            case ChangeKind.DictionarySet:
                activation.Dictionary[key] = value;
                return true;
            case ChangeKind.DictionaryRemove:
                activation.Dictionary.Remove(key);
                return true;
            case ChangeKind.DictionaryClear:
                activation.Dictionary.Clear();
                return true;
            case ChangeKind.ValueSet:
                activation.Value.Value = value;
                return true;
            case ChangeKind.ListAdd:
                activation.List.Add(value);
                return true;
            case ChangeKind.ListRemoveFirst:
                if (activation.List.Count == 0)
                {
                    return false;
                }

                activation.List.RemoveAt(0);
                return true;
            case ChangeKind.QueueEnqueue:
                activation.Queue.Enqueue(value);
                return true;
            case ChangeKind.QueueDequeue:
                if (activation.Queue.Count == 0)
                {
                    return false;
                }

                activation.Queue.Dequeue();
                return true;
            case ChangeKind.SetAdd:
                activation.Set.Add(value);
                return true;
            case ChangeKind.SetRemove:
                activation.Set.Remove(value);
                return true;
            case ChangeKind.CompletionSet:
                activation.Completion.TrySetResult(change.Value);
                return true;
            default:
                activation.Log.Append(value);
                return true;
        }
    }

    private static StateKind StateOf(ChangeKind kind) => kind switch
    {
        ChangeKind.DictionarySet or ChangeKind.DictionaryRemove or ChangeKind.DictionaryClear => StateKind.Dictionary,
        ChangeKind.ValueSet => StateKind.Value,
        ChangeKind.ListAdd or ChangeKind.ListRemoveFirst => StateKind.List,
        ChangeKind.QueueEnqueue or ChangeKind.QueueDequeue => StateKind.Queue,
        ChangeKind.SetAdd or ChangeKind.SetRemove => StateKind.Set,
        ChangeKind.CompletionSet => StateKind.Completion,
        _ => StateKind.Log,
    };

    // Runs work in a flow which isn't the transaction's, as another request or host work would.
    private static Task<T> RunOutsideTransaction<T>(Func<Task<T>> work)
    {
        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(work);
        }
    }

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
        var offset = 0;
        while (offset < segment.Length)
        {
            result.Add(BinaryPrimitives.ReadUInt32LittleEndian(segment.AsSpan(offset + 5)));
            offset += 5 + (int)BinaryPrimitives.ReadUInt32LittleEndian(segment.AsSpan(offset + 1));
        }

        return result;
    }

    private enum ChangeKind
    {
        DictionarySet,
        DictionaryRemove,
        DictionaryClear,
        ValueSet,
        ListAdd,
        ListRemoveFirst,
        QueueEnqueue,
        QueueDequeue,
        SetAdd,
        SetRemove,
        CompletionSet,
        LogAppend,
    }

    private enum StateKind
    {
        Dictionary,
        Value,
        List,
        Queue,
        Set,
        Completion,
        Log,
    }

    private enum StepKind
    {
        Change,
        Transaction,
        Write,
        Snapshot,
        Recover,
    }

    private readonly record struct Change(ChangeKind Kind, int Key, int Value);

    private sealed record Step(StepKind Kind, Change[] Changes, Change[] Inner, bool InnerAborts, Change[] Outside, bool Abort, bool CrashInside)
    {
        public override string ToString() => Kind is StepKind.Transaction
            ? $"Transaction([{string.Join(", ", Changes)}], inner [{string.Join(", ", Inner)}]{(InnerAborts ? " aborts" : "")}, outside [{string.Join(", ", Outside)}]{(Abort ? ", abort" : "")}{(CrashInside ? ", crash" : "")})"
            : Kind is StepKind.Change ? $"Change({Changes[0]})" : Kind.ToString();
    }

    private sealed class AbortException : Exception;

    // The expected content: live, and as of the last write.
    private sealed class Model
    {
        private SortedDictionary<string, string> _dictionary = new(StringComparer.Ordinal);
        private string? _value;
        private List<string> _list = [];
        private Queue<string> _queue = new();
        private SortedSet<string> _set = new(StringComparer.Ordinal);
        private int? _completion;
        private List<string> _log = [];
        private Model? _durable;

        public bool WouldWrite(Change change) => change.Kind switch
        {
            ChangeKind.ListRemoveFirst => _list.Count > 0,
            ChangeKind.QueueDequeue => _queue.Count > 0,
            _ => true,
        };

        public bool Apply(Change change)
        {
            var key = Keys[change.Key];
            var value = Values[change.Value];
            switch (change.Kind)
            {
                case ChangeKind.DictionarySet:
                    _dictionary[key] = value;
                    return true;
                case ChangeKind.DictionaryRemove:
                    _dictionary.Remove(key);
                    return true;
                case ChangeKind.DictionaryClear:
                    _dictionary.Clear();
                    return true;
                case ChangeKind.ValueSet:
                    _value = value;
                    return true;
                case ChangeKind.ListAdd:
                    _list.Add(value);
                    return true;
                case ChangeKind.ListRemoveFirst:
                    if (_list.Count == 0)
                    {
                        return false;
                    }

                    _list.RemoveAt(0);
                    return true;
                case ChangeKind.QueueEnqueue:
                    _queue.Enqueue(value);
                    return true;
                case ChangeKind.QueueDequeue:
                    return _queue.TryDequeue(out _);
                case ChangeKind.SetAdd:
                    _set.Add(value);
                    return true;
                case ChangeKind.SetRemove:
                    _set.Remove(value);
                    return true;
                case ChangeKind.CompletionSet:
                    _completion ??= change.Value;
                    return true;
                default:
                    _log.Add(value);
                    return true;
            }
        }

        public Model Clone() => new()
        {
            _dictionary = new(_dictionary, StringComparer.Ordinal),
            _value = _value,
            _list = [.. _list],
            _queue = new(_queue),
            _set = new(_set, StringComparer.Ordinal),
            _completion = _completion,
            _log = [.. _log],
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

        public string Capture() => string.Join(
            '|',
            string.Join(',', _dictionary.Select(static pair => $"{pair.Key}={pair.Value}")),
            _value,
            string.Join(',', _list),
            string.Join(',', _queue),
            string.Join(',', _set),
            _completion is { } completion
                ? $"{DurableTaskCompletionSourceStatus.Completed}:{completion}"
                : $"{DurableTaskCompletionSourceStatus.Pending}:0",
            string.Join(',', _log));

        public override string ToString() => Capture();
    }

    private sealed class FixedContextProvider : JournaledStateContextProvider
    {
        private readonly PassThroughContext _context = new();

        public override JournaledStateContext? Current => _context;

        private sealed class PassThroughContext : JournaledStateContext
        {
            protected override TResult Access<TArgument, TState, TResult>(
                in JournaledStateAccess access,
                TArgument argument,
                TState state,
                Func<TState, TResult> operation,
                Action<TState, TResult>? apply)
            {
                var result = operation(state);
                apply?.Invoke(state, result);
                return result;
            }
        }
    }

    // A state whose preparation for a write fails on demand, before the write reaches storage.
    private sealed class FailingPreparationState : IStateMachine
    {
        public FailingPreparationState(IJournaledStateManager manager) => manager.RegisterStateMachine("failing-preparation", this);

        public bool Fail { get; set; }

        ValueTask IStateMachine.PrepareWriteAsync(CancellationToken cancellationToken) =>
            Fail ? ValueTask.FromException(new InvalidOperationException("The preparation failed.")) : ValueTask.CompletedTask;

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
}
