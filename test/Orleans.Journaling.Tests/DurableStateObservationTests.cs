using CsCheck;
using Xunit;
using static Orleans.Journaling.Tests.ObservationTestHelpers;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Tests watch streams for durable values and collections, including commit watermarks, speculative delivery,
/// bounded buffering, and manager failure.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public class DurableStateObservationTests : JournalingTestBase
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public Task UnobservedState_CreatesNoObserverHub() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var value = CreateValue(sut.Manager);
        var dictionary = CreateDictionary(sut.Manager);
        var list = CreateList(sut.Manager);
        var set = CreateSet(sut.Manager);
        var queue = CreateQueue(sut.Manager);
        var log = CreateLog(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);

        value.Value = "value";
        dictionary["key"] = 1;
        list.Add(1);
        set.Add(1);
        queue.Enqueue(1);
        log.Append("entry");
        using (var draft = log.BeginDraft())
        {
            draft.Write("draft item");
        }

        // Creating a stream without enumerating it does not subscribe.
        _ = dictionary.WatchAsync(cancellationToken: CancellationToken);
        _ = log.ReadAsync(cancellationToken: CancellationToken);
        await sut.Manager.WriteStateAsync(CancellationToken);

        Assert.Null(sut.Manager.AsJournaledStateManager().Observers);
    });

    [Fact]
    public Task EndedWatch_RemovesItsRegistration() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var dictionary = CreateDictionary(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);

        await using (var watch = dictionary.WatchAsync(cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken))
        {
            await watch.NextAsync();
            var observers = sut.Manager.AsJournaledStateManager().Observers;
            Assert.NotNull(observers);
            Assert.True(observers.TryGetTopic(dictionary, out _));
        }

        Assert.False(sut.Manager.AsJournaledStateManager().Observers!.TryGetTopic(dictionary, out _));
    });

    [Fact]
    public Task ValueWatch_CommittedMode_DeliversValuesAfterTheirWrite() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var value = CreateValue(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);
        value.Value = "initial";
        await sut.Manager.WriteStateAsync(CancellationToken);

        await using var watch = value.WatchAsync(cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        Assert.Equal(Committed("initial", version: 1), await watch.NextAsync());

        value.Value = "next";
        var next = watch.MoveNextAsync();
        Assert.False(next.IsCompleted);

        await sut.Manager.WriteStateAsync(CancellationToken);
        Assert.True(await next);
        Assert.Equal(Committed("next", version: 2), watch.Current);
    });

    [Fact]
    public Task ValueWatch_StagedMode_DeliversStagedValueThenItsCommit() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var value = CreateValue(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);

        await using var watch = value.WatchAsync(DurableWatchMode.Staged, CancellationToken).GetAsyncEnumerator(CancellationToken);
        Assert.Equal(Committed(null, version: 0), await watch.NextAsync());

        value.Value = "staged";
        Assert.Equal(new ValueChange<string> { Value = "staged", Status = DurableChangeStatus.Staged, Version = 1 }, await watch.NextAsync());

        var next = watch.MoveNextAsync();
        Assert.False(next.IsCompleted);
        await sut.Manager.WriteStateAsync(CancellationToken);
        Assert.True(await next);
        Assert.Equal(Committed("staged", version: 1), watch.Current);
    });

    [Fact]
    public Task ValueWatch_DraftsWithoutStaged_DeliversOnlyCommittedValues() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var value = CreateValue(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);

        // Values have no drafts, so the flag selects committed values only.
        await using var watch = value.WatchAsync(DurableWatchMode.Drafts, CancellationToken).GetAsyncEnumerator(CancellationToken);
        Assert.Equal(Committed(null, version: 0), await watch.NextAsync());

        value.Value = "staged";
        var next = watch.MoveNextAsync();
        Assert.False(next.IsCompleted);
        await sut.Manager.WriteStateAsync(CancellationToken);
        Assert.True(await next);
        Assert.Equal(Committed("staged", version: 1), watch.Current);
    });

    [Fact]
    public Task ValueWatch_CollapsesToLatestValue() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var value = CreateValue(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);

        await using var committed = value.WatchAsync(cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        await using var speculative = value.WatchAsync(DurableWatchMode.Staged, CancellationToken).GetAsyncEnumerator(CancellationToken);
        await committed.NextAsync();
        await speculative.NextAsync();

        value.Value = "a";
        value.Value = "b";
        value.Value = "c";
        Assert.Equal(new ValueChange<string> { Value = "c", Status = DurableChangeStatus.Staged, Version = 1 }, await speculative.NextAsync());

        await sut.Manager.WriteStateAsync(CancellationToken);
        Assert.Equal(Committed("c", version: 1), await committed.NextAsync());
        Assert.Equal(Committed("c", version: 1), await speculative.NextAsync());

        var next = committed.MoveNextAsync();
        Assert.False(next.IsCompleted);
        value.Value = "d";
        await sut.Manager.WriteStateAsync(CancellationToken);
        Assert.True(await next);
        Assert.Equal(Committed("d", version: 2), committed.Current);
    });

    [Fact]
    public Task CommittedWatch_ExcludesChangesStagedDuringAnInFlightWrite() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var value = CreateValue(sut.Manager);
        var dictionary = CreateDictionary(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);

        await using var valueWatch = value.WatchAsync(cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        await using var dictionaryWatch = dictionary.WatchAsync(cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        await valueWatch.NextAsync();
        Assert.Empty((await dictionaryWatch.NextAsync()).Snapshot!);

        value.Value = "captured";
        dictionary["captured"] = 1;
        sut.Storage.HoldNextWrite();
        var firstWrite = sut.Manager.WriteStateAsync(CancellationToken).AsTask();
        await sut.Storage.WriteStarted.Task;

        // Another turn stages changes after the write captured its entries.
        value.Value = "staged";
        dictionary["staged"] = 2;

        // A watch which begins now waits for the write which makes its snapshot durable.
        await using var lateWatch = dictionary.WatchAsync(cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        var lateNext = lateWatch.MoveNextAsync();
        var valueNext = valueWatch.MoveNextAsync();
        var dictionaryNext = dictionaryWatch.MoveNextAsync();
        Assert.False(lateNext.IsCompleted);
        Assert.False(valueNext.IsCompleted);
        Assert.False(dictionaryNext.IsCompleted);

        sut.Storage.Release();
        await firstWrite;
        Assert.True(await valueNext);
        Assert.Equal(Committed("captured", version: 1), valueWatch.Current);
        Assert.True(await dictionaryNext);
        Assert.Equal(DictionaryChangeKind.Set, dictionaryWatch.Current.Kind);
        Assert.Equal("captured", dictionaryWatch.Current.Key);
        Assert.Equal(DurableChangeStatus.Committed, dictionaryWatch.Current.Status);

        valueNext = valueWatch.MoveNextAsync();
        dictionaryNext = dictionaryWatch.MoveNextAsync();
        Assert.False(valueNext.IsCompleted);
        Assert.False(dictionaryNext.IsCompleted);
        Assert.False(lateNext.IsCompleted);

        await sut.Manager.WriteStateAsync(CancellationToken);
        Assert.True(await valueNext);
        Assert.Equal(Committed("staged", version: 2), valueWatch.Current);
        Assert.True(await dictionaryNext);
        Assert.Equal("staged", dictionaryWatch.Current.Key);
        Assert.Equal(2, dictionaryWatch.Current.Version);
        Assert.True(await lateNext);
        Assert.Equal(DictionaryChangeKind.Resync, lateWatch.Current.Kind);
        Assert.Equal(DurableChangeStatus.Committed, lateWatch.Current.Status);
        Assert.Equal(
            [new("captured", 1), new("staged", 2)],
            lateWatch.Current.Snapshot!.OrderBy(static pair => pair.Key, StringComparer.Ordinal));
    });

    [Fact]
    public Task ValueWatch_SnapshotAcknowledgesCapturedValueAndPreservesLaterChanges() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var value = CreateValue(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);
        value.Value = "snapshot";
        sut.Storage.IsCompactionRequested = true;
        sut.Storage.HoldNextWrite();
        var write = sut.Manager.WriteStateAsync(CancellationToken).AsTask();
        await sut.Storage.WriteStarted.Task;

        await using var during = value.WatchAsync(cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        var next = during.MoveNextAsync();
        Assert.False(next.IsCompleted);
        sut.Storage.Release();
        await write;
        Assert.True(await next);
        Assert.Equal(Committed("snapshot", version: 1), during.Current);

        await using var after = value.WatchAsync(cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        Assert.Equal(Committed("snapshot", version: 1), await after.NextAsync());

        value.Value = "captured";
        sut.Storage.IsCompactionRequested = true;
        sut.Storage.HoldNextWrite();
        write = sut.Manager.WriteStateAsync(CancellationToken).AsTask();
        await sut.Storage.WriteStarted.Task;
        value.Value = "later";
        sut.Storage.Release();
        await write;
        Assert.Equal(Committed("captured", version: 2), await during.NextAsync());
        next = during.MoveNextAsync();
        Assert.False(next.IsCompleted);
        await sut.Manager.WriteStateAsync(CancellationToken);
        Assert.True(await next);
        Assert.Equal(Committed("later", version: 3), during.Current);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PrefixFlush_AcknowledgesOnFullCaptureAfterEntryAbort(bool dirtyValue) => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var dictionary = CreateDictionary(sut.Manager);
        var log = CreateLog(sut.Manager);
        var value = CreateValue(sut.Manager);
        var manual = new OpenEntryState();
        sut.Manager.RegisterStateMachine("manual", manual);
        await sut.Lifecycle.OnStart(CancellationToken);
        await sut.Manager.WriteStateAsync(CancellationToken);
        await using var watch = dictionary.WatchAsync(cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        await using var read = log.ReadAsync(cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        await using var valueWatch = value.WatchAsync(cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        await watch.NextAsync();
        await read.NextAsync();
        await valueWatch.NextAsync();

        dictionary["flushed"] = 1;
        log.Append("flushed");
        if (dirtyValue)
        {
            value.Value = "uncaptured";
        }

        var change = watch.MoveNextAsync();
        var entry = read.MoveNextAsync();
        var valueChange = valueWatch.MoveNextAsync().AsTask();
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var abort = new ManualResetEventSlim();
        var openEntry = Task.Run(() =>
        {
            using var scope = manual.Writer.BeginEntry();
            opened.SetResult();
            abort.Wait(CancellationToken);
        }, CancellationToken);
        try
        {
            await opened.Task;
            await sut.Manager.WriteStateAsync(CancellationToken);
            Assert.False(change.IsCompleted);
            Assert.False(entry.IsCompleted);
            Assert.False(valueChange.IsCompleted);
            Assert.Equal(0, log.CommittedSequence);
        }
        finally
        {
            abort.Set();
            await openEntry;
        }

        // With no dirty value this capture has no bytes, but the completed prefix is already durable.
        await sut.Manager.WriteStateAsync(CancellationToken);
        Assert.True(await change);
        Assert.Equal(("flushed", DurableChangeStatus.Committed), (watch.Current.Key, watch.Current.Status));
        Assert.True(await entry);
        Assert.Equal((LogEventKind.Entry, "flushed", DurableChangeStatus.Committed), (read.Current.Kind, read.Current.Item, read.Current.Status));
        Assert.Equal(1, log.CommittedSequence);
        if (dirtyValue)
        {
            Assert.True(await valueChange);
            Assert.Equal(Committed("uncaptured", version: 2), valueWatch.Current);
        }
        else
        {
            await sut.Manager.DisposeAsync();
            Assert.False(await valueChange);
        }
    });

    [Theory]
    [InlineData("dictionary-set")]
    [InlineData("dictionary-add")]
    [InlineData("dictionary-remove")]
    [InlineData("dictionary-clear")]
    [InlineData("list-add")]
    [InlineData("list-set")]
    [InlineData("list-insert")]
    [InlineData("list-remove")]
    [InlineData("list-clear")]
    [InlineData("queue-enqueue")]
    [InlineData("queue-dequeue")]
    [InlineData("queue-try-dequeue")]
    [InlineData("queue-clear")]
    [InlineData("set-add")]
    [InlineData("set-remove")]
    [InlineData("set-clear")]
    [InlineData("set-intersect")]
    [InlineData("set-symmetric")]
    public Task CollectionWatch_EncodingFailurePreservesDurability(string operation) => RunOnActivationSchedulerAsync(async () =>
    {
        foreach (var staged in new[] { false, true })
        {
            var sut = CreateSystem();
            var codec = new FailingCollectionCodec(
                new OrleansBinaryDurableDictionaryCommandCodec<string, int>(CodecProvider.GetCodec<string>(), CodecProvider.GetCodec<int>(), SessionPool),
                new OrleansBinaryDurableListCommandCodec<int>(CodecProvider.GetCodec<int>(), SessionPool),
                new OrleansBinaryDurableQueueCommandCodec<int>(CodecProvider.GetCodec<int>(), SessionPool),
                new OrleansBinaryDurableSetCommandCodec<int>(CodecProvider.GetCodec<int>(), SessionPool));
            var dictionary = new DurableDictionary<string, int>("dictionary", sut.Manager, codec);
            var list = new DurableList<int>("list", sut.Manager, codec);
            var queue = new DurableQueue<int>("queue", sut.Manager, codec);
            var set = new DurableSet<int>("set", sut.Manager, codec);
            await sut.Lifecycle.OnStart(CancellationToken);
            dictionary["a"] = 1;
            list.Add(1);
            queue.Enqueue(1);
            set.Add(1);
            await sut.Manager.WriteStateAsync(CancellationToken);
            if (staged)
            {
                dictionary["b"] = 2;
                list.Add(2);
                queue.Enqueue(2);
                set.Add(2);
            }

            codec.Fail = true;
            Assert.Throws<InvalidOperationException>(() =>
            {
                switch (operation)
                {
                    case "dictionary-set": dictionary["a"] = 3; break;
                    case "dictionary-add": dictionary.Add("c", 3); break;
                    case "dictionary-remove": dictionary.Remove("a"); break;
                    case "dictionary-clear": dictionary.Clear(); break;
                    case "list-add": list.Add(3); break;
                    case "list-set": list[0] = 3; break;
                    case "list-insert": list.Insert(0, 3); break;
                    case "list-remove": list.RemoveAt(0); break;
                    case "list-clear": list.Clear(); break;
                    case "queue-enqueue": queue.Enqueue(3); break;
                    case "queue-dequeue": queue.Dequeue(); break;
                    case "queue-try-dequeue": queue.TryDequeue(out _); break;
                    case "queue-clear": queue.Clear(); break;
                    case "set-add": set.Add(3); break;
                    case "set-remove": set.Remove(1); break;
                    case "set-clear": set.Clear(); break;
                    case "set-intersect": set.IntersectWith([]); break;
                    case "set-symmetric": set.SymmetricExceptWith([3]); break;
                    default: throw new ArgumentOutOfRangeException(nameof(operation));
                }
            });
            codec.Fail = false;

            await using var dictionaryWatch = dictionary.WatchAsync(cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
            await using var listWatch = list.WatchAsync(cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
            await using var queueWatch = queue.WatchAsync(cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
            await using var setWatch = set.WatchAsync(cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
            var next = new[]
            {
                dictionaryWatch.MoveNextAsync().AsTask(), listWatch.MoveNextAsync().AsTask(),
                queueWatch.MoveNextAsync().AsTask(), setWatch.MoveNextAsync().AsTask(),
            };
            if (staged)
            {
                Assert.All(next, static task => Assert.False(task.IsCompleted));
            }
            else
            {
                Assert.All(next, static task => Assert.True(task.IsCompletedSuccessfully));
            }

            await sut.Manager.WriteStateAsync(CancellationToken);
            Assert.All(await Task.WhenAll(next), Assert.True);
            Assert.Equal(DurableChangeStatus.Committed, dictionaryWatch.Current.Status);
            Assert.Equal(dictionary.OrderBy(static pair => pair.Key), dictionaryWatch.Current.Snapshot!.OrderBy(static pair => pair.Key));
            Assert.Equal(list, listWatch.Current.Snapshot!);
            Assert.Equal(queue, queueWatch.Current.Snapshot!);
            Assert.Equal(set.Order(), setWatch.Current.Snapshot!.Order());
            await sut.Manager.DisposeAsync();
        }
    });

    [Fact]
    public Task DictionaryWatch_StagedMode_DeliversWatermarkAfterWrite() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var dictionary = CreateDictionary(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);

        await using var watch = dictionary.WatchAsync(DurableWatchMode.Staged, CancellationToken).GetAsyncEnumerator(CancellationToken);
        var resync = await watch.NextAsync();
        Assert.Equal(DictionaryChangeKind.Resync, resync.Kind);
        Assert.Equal(DurableChangeStatus.Committed, resync.Status);

        dictionary["a"] = 1;
        dictionary.Remove("a");
        Assert.Equal(new DictionaryChange<string, int> { Kind = DictionaryChangeKind.Set, Key = "a", Value = 1, Status = DurableChangeStatus.Staged, Version = 1 }, await watch.NextAsync());
        Assert.Equal(new DictionaryChange<string, int> { Kind = DictionaryChangeKind.Removed, Key = "a", Status = DurableChangeStatus.Staged, Version = 1 }, await watch.NextAsync());

        var next = watch.MoveNextAsync();
        Assert.False(next.IsCompleted);
        await sut.Manager.WriteStateAsync(CancellationToken);
        Assert.True(await next);
        Assert.Equal(new DictionaryChange<string, int> { Kind = DictionaryChangeKind.Watermark, Status = DurableChangeStatus.Committed, Version = 1 }, watch.Current);
    });

    [Fact]
    public Task ChangeWatch_Overflow_DeliversResyncSnapshot() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var dictionary = CreateDictionary(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);

        await using var watch = dictionary.WatchAsync(DurableWatchMode.Committed, capacity: 4, CancellationToken).GetAsyncEnumerator(CancellationToken);
        var view = new Dictionary<string, int>();
        Apply(view, await watch.NextAsync());

        for (var i = 0; i < 10; i++)
        {
            dictionary[$"key{i}"] = i;
        }

        await sut.Manager.WriteStateAsync(CancellationToken);

        // The buffer overflowed at the fifth and ninth changes. The latest snapshot includes nine entries.
        var resync = await watch.NextAsync();
        Assert.Equal(DictionaryChangeKind.Resync, resync.Kind);
        Assert.Equal(DurableChangeStatus.Committed, resync.Status);
        Assert.Equal(9, resync.Snapshot!.Count);
        Apply(view, resync);
        var last = await watch.NextAsync();
        Assert.Equal(new DictionaryChange<string, int> { Kind = DictionaryChangeKind.Set, Key = "key9", Value = 9, Status = DurableChangeStatus.Committed, Version = 1 }, last);
        Apply(view, last);
        Assert.Equal(dictionary.OrderBy(static pair => pair.Key), view.OrderBy(static pair => pair.Key));
    });

    [Fact]
    public Task CollectionWatches_DeliverTypedChanges() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var list = CreateList(sut.Manager);
        var set = CreateSet(sut.Manager);
        var queue = CreateQueue(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);
        list.Add(1);
        set.Add(1);
        queue.Enqueue(1);
        await sut.Manager.WriteStateAsync(CancellationToken);

        await using var listWatch = list.WatchAsync(DurableWatchMode.Staged, CancellationToken).GetAsyncEnumerator(CancellationToken);
        await using var setWatch = set.WatchAsync(DurableWatchMode.Staged, CancellationToken).GetAsyncEnumerator(CancellationToken);
        await using var queueWatch = queue.WatchAsync(DurableWatchMode.Staged, CancellationToken).GetAsyncEnumerator(CancellationToken);
        Assert.Equal([1], (await listWatch.NextAsync()).Snapshot!);
        Assert.Equal([1], (await setWatch.NextAsync()).Snapshot!);
        Assert.Equal([1], (await queueWatch.NextAsync()).Snapshot!);

        list.Add(2);
        list.Insert(0, 0);
        list[1] = 10;
        list.RemoveAt(2);
        list.Clear();
        Assert.Equal((ListChangeKind.Added, 1, 2), ToTuple(await listWatch.NextAsync()));
        Assert.Equal((ListChangeKind.Inserted, 0, 0), ToTuple(await listWatch.NextAsync()));
        Assert.Equal((ListChangeKind.Set, 1, 10), ToTuple(await listWatch.NextAsync()));
        Assert.Equal((ListChangeKind.RemovedAt, 2, 0), ToTuple(await listWatch.NextAsync()));
        Assert.Equal((ListChangeKind.Cleared, 0, 0), ToTuple(await listWatch.NextAsync()));

        set.Add(2);
        set.Add(2);
        set.Remove(1);
        set.IntersectWith([2, 3]);
        set.SymmetricExceptWith([2]);
        set.Add(4);
        set.Clear();
        Assert.Equal((SetChangeKind.Added, 2), (await setWatch.NextAsync() is var added ? (added.Kind, added.Item) : default));
        Assert.Equal((SetChangeKind.Removed, 1), (await setWatch.NextAsync() is var removed ? (removed.Kind, removed.Item) : default));
        var symmetricResync = await setWatch.NextAsync();
        Assert.Equal(SetChangeKind.Resync, symmetricResync.Kind);
        Assert.Empty(symmetricResync.Snapshot!);
        Assert.Equal(DurableChangeStatus.Staged, symmetricResync.Status);
        Assert.Equal(SetChangeKind.Added, (await setWatch.NextAsync()).Kind);
        Assert.Equal(SetChangeKind.Cleared, (await setWatch.NextAsync()).Kind);

        queue.Enqueue(2);
        Assert.Equal(1, queue.Dequeue());
        Assert.True(queue.TryDequeue(out _));
        queue.Clear();
        Assert.Equal((QueueChangeKind.Enqueued, 2), (await queueWatch.NextAsync() is var enqueued ? (enqueued.Kind, enqueued.Item) : default));
        Assert.Equal((QueueChangeKind.Dequeued, 1), (await queueWatch.NextAsync() is var dequeued ? (dequeued.Kind, dequeued.Item) : default));
        Assert.Equal((QueueChangeKind.Dequeued, 2), (await queueWatch.NextAsync() is var tryDequeued ? (tryDequeued.Kind, tryDequeued.Item) : default));
        Assert.Equal(QueueChangeKind.Cleared, (await queueWatch.NextAsync()).Kind);

        await sut.Manager.WriteStateAsync(CancellationToken);
        Assert.Equal(ListChangeKind.Watermark, (await listWatch.NextAsync()).Kind);
        Assert.Equal(QueueChangeKind.Watermark, (await queueWatch.NextAsync()).Kind);
        var setWatermark = await setWatch.NextAsync();
        Assert.Equal(SetChangeKind.Watermark, setWatermark.Kind);
        Assert.Equal(2, setWatermark.Version);

        static (ListChangeKind, int, int) ToTuple(ListChange<int> change) => (change.Kind, change.Index, change.Item);
    });

    [Fact]
    public Task SeveralWatches_ShareOneHubAndOneWatermark() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var value = CreateValue(sut.Manager);
        var dictionary = CreateDictionary(sut.Manager);
        var list = CreateList(sut.Manager);
        var log = CreateLog(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);

        await using var valueWatch = value.WatchAsync(cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        await using var dictionaryWatch = dictionary.WatchAsync(cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        await using var listWatch = list.WatchAsync(cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        await using var logRead = log.ReadAsync(cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        await valueWatch.NextAsync();
        await dictionaryWatch.NextAsync();
        await listWatch.NextAsync();
        Assert.Equal(LogEventKind.CaughtUp, (await logRead.NextAsync()).Kind);
        var observers = sut.Manager.AsJournaledStateManager().Observers;
        Assert.NotNull(observers);

        value.Value = "v";
        dictionary["k"] = 1;
        list.Add(2);
        log.Append("e");
        var pending = new[] { valueWatch.MoveNextAsync().AsTask(), dictionaryWatch.MoveNextAsync().AsTask(), listWatch.MoveNextAsync().AsTask(), logRead.MoveNextAsync().AsTask() };
        Assert.All(pending, static task => Assert.False(task.IsCompleted));

        await sut.Manager.WriteStateAsync(CancellationToken);
        Assert.All(await Task.WhenAll(pending), Assert.True);
        Assert.Same(observers, sut.Manager.AsJournaledStateManager().Observers);
        Assert.Equal(Committed("v", version: 1), valueWatch.Current);
        Assert.Equal((DictionaryChangeKind.Set, 1L), (dictionaryWatch.Current.Kind, dictionaryWatch.Current.Version));
        Assert.Equal((ListChangeKind.Added, 1L), (listWatch.Current.Kind, listWatch.Current.Version));
        Assert.Equal((LogEventKind.Entry, 0L, "e", DurableChangeStatus.Committed), (logRead.Current.Kind, logRead.Current.Sequence, logRead.Current.Item, logRead.Current.Status));
    });

    [Fact]
    public Task GetStateMachines_EnumeratesRegisteredNamesAndInstances() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var value = CreateValue(sut.Manager, "status");
        var dictionary = CreateDictionary(sut.Manager, "history");
        await sut.Lifecycle.OnStart(CancellationToken);

        var stateMachines = sut.Manager.GetStateMachines().OrderBy(static pair => pair.Key, StringComparer.Ordinal).ToArray();
        Assert.Equal(["history", "status"], stateMachines.Select(static pair => pair.Key));
        Assert.Same(dictionary, stateMachines[0].Value);
        Assert.Same(value, stateMachines[1].Value);
    });

    [Fact]
    public Task GetStateMachines_ExcludesRetiredState() => RunOnActivationSchedulerAsync(async () =>
    {
        var storage = new GatedJournalStorage();
        var first = CreateSystem(storage);
        var retired = CreateValue(first.Manager, "retired");
        var firstStatus = CreateValue(first.Manager, "status");
        await first.Lifecycle.OnStart(CancellationToken);
        retired.Value = "old";
        firstStatus.Value = "current";
        await first.Manager.WriteStateAsync(CancellationToken);

        var second = CreateSystem(storage);
        CreateValue(second.Manager, "status");
        await second.Lifecycle.OnStart(CancellationToken);
        Assert.Equal(["status"], second.Manager.GetStateMachines().Select(static pair => pair.Key));

        // Registering the state again restores it.
        var third = CreateSystem(storage);
        var restored = CreateValue(third.Manager, "retired");
        CreateValue(third.Manager, "status");
        await third.Lifecycle.OnStart(CancellationToken);
        Assert.Equal(["retired", "status"], third.Manager.GetStateMachines().Select(static pair => pair.Key).Order(StringComparer.Ordinal));
        Assert.Equal("old", restored.Value);
    });

    [Fact]
    public Task RecoveredState_IsDeliveredAsCommitted() => RunOnActivationSchedulerAsync(async () =>
    {
        var storage = new GatedJournalStorage();
        var first = CreateSystem(storage);
        var firstValue = CreateValue(first.Manager);
        var firstQueue = CreateQueue(first.Manager);
        await first.Lifecycle.OnStart(CancellationToken);
        firstValue.Value = "recovered";
        firstQueue.Enqueue(7);
        await first.Manager.WriteStateAsync(CancellationToken);

        var second = CreateSystem(storage);
        var value = CreateValue(second.Manager);
        var queue = CreateQueue(second.Manager);
        await second.Lifecycle.OnStart(CancellationToken);

        await using var valueWatch = value.WatchAsync(cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        await using var queueWatch = queue.WatchAsync(cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        Assert.Equal(Committed("recovered", version: 0), await valueWatch.NextAsync());
        var resync = await queueWatch.NextAsync();
        Assert.Equal((QueueChangeKind.Resync, DurableChangeStatus.Committed), (resync.Kind, resync.Status));
        Assert.Equal([7], resync.Snapshot!);
    });

    [Fact]
    public Task FailedWrite_FaultsWatchesWithoutDeliveringStagedChanges() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var dictionary = CreateDictionary(sut.Manager);
        var log = CreateLog(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);

        await using var watch = dictionary.WatchAsync(cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        await using var read = log.ReadAsync(cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        await watch.NextAsync();
        Assert.Equal(LogEventKind.CaughtUp, (await read.NextAsync()).Kind);
        var watchNext = watch.MoveNextAsync().AsTask();
        var readNext = read.MoveNextAsync().AsTask();

        var failure = new IOException("Storage is unavailable.");
        sut.Storage.Failure = failure;
        dictionary["lost"] = 1;
        log.Append("lost");
        await Assert.ThrowsAsync<IOException>(async () => await sut.Manager.WriteStateAsync(CancellationToken));

        var watchException = await Assert.ThrowsAsync<InvalidOperationException>(() => watchNext);
        Assert.Same(failure, watchException.InnerException);
        var readException = await Assert.ThrowsAsync<InvalidOperationException>(() => readNext);
        Assert.Same(failure, readException.InnerException);

        // The fenced manager accepts no new watchers.
        await using var lateWatch = dictionary.WatchAsync(cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await lateWatch.MoveNextAsync());
    });

    [Fact]
    public Task DisposedManager_CompletesWatches() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var value = CreateValue(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);

        await using var watch = value.WatchAsync(cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        await watch.NextAsync();
        var next = watch.MoveNextAsync();
        await sut.Manager.DisposeAsync();
        Assert.False(await next);
    });

    [Fact]
    public Task CanceledWatch_ThrowsAndUnsubscribes() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var dictionary = CreateDictionary(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);

        using var cancellation = new CancellationTokenSource();
        await using var watch = dictionary.WatchAsync(cancellationToken: cancellation.Token).GetAsyncEnumerator(CancellationToken);
        await watch.NextAsync();
        var next = watch.MoveNextAsync().AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
        Assert.False(sut.Manager.AsJournaledStateManager().Observers!.TryGetTopic(dictionary, out _));
    });

    [Fact]
    public Task DeletedState_ResynchronizesWatches() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var dictionary = CreateDictionary(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);
        dictionary["a"] = 1;
        await sut.Manager.WriteStateAsync(CancellationToken);

        await using var watch = dictionary.WatchAsync(cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        Assert.Single((await watch.NextAsync()).Snapshot!);
        await sut.Manager.DeleteStateAsync(CancellationToken);

        var resync = await watch.NextAsync();
        Assert.Equal((DictionaryChangeKind.Resync, DurableChangeStatus.Committed), (resync.Kind, resync.Status));
        Assert.Empty(resync.Snapshot!);
    });

    [Fact]
    public Task UnsupportedWatchArguments_Throw() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var dictionary = CreateDictionary(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);

        Assert.Throws<ArgumentOutOfRangeException>(() => dictionary.WatchAsync((DurableWatchMode)7, CancellationToken));
        Assert.Throws<ArgumentOutOfRangeException>(() => dictionary.WatchAsync(DurableWatchMode.Committed, capacity: 0, CancellationToken));
        Assert.Throws<NotSupportedException>(() => new UnsupportedValue().WatchAsync(cancellationToken: CancellationToken));
    });

    /// <summary>
    /// Compares committed and speculative dictionary watch streams with a model of in-memory and durable state across
    /// random sequences of mutations and writes.
    /// </summary>
    [Fact]
    public async Task DictionaryWatches_MatchModel_ForOperationSequences()
    {
        var operations = Gen.Select(Gen.Int[0, 3], Gen.Int[0, 4], Gen.Int[0, 99]).Array[1, 40];
        await operations.SampleAsync(sequence => RunOnActivationSchedulerAsync(() => RunDictionaryModelAsync(sequence)), iter: 50);
    }

    private async Task RunDictionaryModelAsync((int Operation, int Key, int Value)[] operations)
    {
        var sut = CreateSystem();
        var dictionary = CreateDictionary(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);

        // The first write persists the state registration, so the model's changes begin at version two.
        await sut.Manager.WriteStateAsync(CancellationToken);

        await using var committed = dictionary.WatchAsync(cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        await using var speculative = dictionary.WatchAsync(DurableWatchMode.Staged, CancellationToken).GetAsyncEnumerator(CancellationToken);
        var committedView = new Dictionary<string, int>();
        var speculativeView = new Dictionary<string, int>();
        Apply(committedView, await committed.NextAsync());
        Apply(speculativeView, await speculative.NextAsync());

        var model = new Dictionary<string, int>();
        var stagedChanges = 0;
        var version = 2L;
        foreach (var (operation, keyIndex, value) in operations)
        {
            var key = $"k{keyIndex}";
            var changed = true;
            switch (operation)
            {
                case 0:
                    dictionary[key] = value;
                    model[key] = value;
                    break;
                case 1:
                    changed = dictionary.Remove(key);
                    Assert.Equal(model.Remove(key), changed);
                    break;
                case 2:
                    dictionary.Clear();
                    model.Clear();
                    break;
                default:
                    changed = false;
                    await sut.Manager.WriteStateAsync(CancellationToken);
                    for (var i = 0; i < stagedChanges; i++)
                    {
                        var change = await committed.NextAsync();
                        Assert.Equal((DurableChangeStatus.Committed, version), (change.Status, change.Version));
                        Apply(committedView, change);
                    }

                    Assert.Equal(Sorted(model), Sorted(committedView));
                    if (stagedChanges > 0)
                    {
                        Assert.Equal(new DictionaryChange<string, int> { Kind = DictionaryChangeKind.Watermark, Status = DurableChangeStatus.Committed, Version = version }, await speculative.NextAsync());
                        version++;
                    }

                    stagedChanges = 0;
                    break;
            }

            if (changed)
            {
                var change = await speculative.NextAsync();
                Assert.Equal((DurableChangeStatus.Staged, version), (change.Status, change.Version));
                Apply(speculativeView, change);
                Assert.Equal(Sorted(model), Sorted(speculativeView));
                stagedChanges++;
            }
        }

        static KeyValuePair<string, int>[] Sorted(Dictionary<string, int> values) =>
            [.. values.OrderBy(static pair => pair.Key, StringComparer.Ordinal)];
    }

    private static void Apply(Dictionary<string, int> view, DictionaryChange<string, int> change)
    {
        switch (change.Kind)
        {
            case DictionaryChangeKind.Resync:
                view.Clear();
                foreach (var (key, value) in change.Snapshot!)
                {
                    view[key] = value;
                }

                break;
            case DictionaryChangeKind.Set:
                view[change.Key!] = change.Value;
                break;
            case DictionaryChangeKind.Removed:
                view.Remove(change.Key!);
                break;
            case DictionaryChangeKind.Cleared:
                view.Clear();
                break;
        }
    }

    private static ValueChange<string> Committed(string? value, long version) =>
        new() { Value = value, Status = DurableChangeStatus.Committed, Version = version };

    private (IJournaledStateManager Manager, GatedJournalStorage Storage, ILifecycleSubject Lifecycle) CreateSystem(GatedJournalStorage? storage = null)
    {
        storage ??= new GatedJournalStorage();
        storage.Inner.SetConfiguredJournalFormatKey(OrleansBinaryJournalFormat.JournalFormatKey);
        var sut = CreateTestSystem(storage);
        return (sut.Manager, storage, sut.Lifecycle);
    }

    private DurableValue<string> CreateValue(IJournaledStateManager manager, string name = "value") =>
        new(name, manager, new OrleansBinaryDurableValueCommandCodec<string>(CodecProvider.GetCodec<string>(), SessionPool));

    private DurableDictionary<string, int> CreateDictionary(IJournaledStateManager manager, string name = "dictionary") =>
        new(name, manager, new OrleansBinaryDurableDictionaryCommandCodec<string, int>(CodecProvider.GetCodec<string>(), CodecProvider.GetCodec<int>(), SessionPool));

    private DurableList<int> CreateList(IJournaledStateManager manager, string name = "list") =>
        new(name, manager, new OrleansBinaryDurableListCommandCodec<int>(CodecProvider.GetCodec<int>(), SessionPool));

    private DurableSet<int> CreateSet(IJournaledStateManager manager, string name = "set") =>
        new(name, manager, new OrleansBinaryDurableSetCommandCodec<int>(CodecProvider.GetCodec<int>(), SessionPool));

    private DurableQueue<int> CreateQueue(IJournaledStateManager manager, string name = "queue") =>
        new(name, manager, new OrleansBinaryDurableQueueCommandCodec<int>(CodecProvider.GetCodec<int>(), SessionPool));

    private DurableLog<string> CreateLog(IJournaledStateManager manager, string name = "log") =>
        new(name, manager, new OrleansBinaryDurableLogCommandCodec<string>(CodecProvider.GetCodec<string>(), SessionPool));

    private sealed class UnsupportedValue : IDurableValue<int>
    {
        public int Value { get; set; }
    }

    private sealed class OpenEntryState : IStateMachine
    {
        public JournalStreamWriter Writer { get; private set; }
        public void Reset(JournalStreamWriter writer) => Writer = writer;
        public void ReplayEntry(JournalEntry entry, JournalReplayContext context) { }
        public void WritePendingEntries(JournalStreamWriter writer) { }
        public void WriteSnapshot(JournalStreamWriter writer) { }
    }

    private sealed class FailingCollectionCodec(
        IDurableDictionaryCommandCodec<string, int> dictionary,
        IDurableListCommandCodec<int> list,
        IDurableQueueCommandCodec<int> queue,
        IDurableSetCommandCodec<int> set) :
        IDurableDictionaryCommandCodec<string, int>, IDurableListCommandCodec<int>, IDurableQueueCommandCodec<int>, IDurableSetCommandCodec<int>
    {
        public bool Fail { get; set; }

        private void BeforeWrite(JournalStreamWriter writer)
        {
            if (Fail)
            {
                using var entry = writer.BeginEntry();
                entry.Writer.GetSpan(1)[0] = 1;
                entry.Writer.Advance(1);
                throw new InvalidOperationException("Expected encoding failure.");
            }
        }

        public void WriteSet(string key, int value, JournalStreamWriter writer) { BeforeWrite(writer); dictionary.WriteSet(key, value, writer); }
        public void WriteRemove(string key, JournalStreamWriter writer) { BeforeWrite(writer); dictionary.WriteRemove(key, writer); }
        void IDurableDictionaryCommandCodec<string, int>.WriteClear(JournalStreamWriter writer) { BeforeWrite(writer); dictionary.WriteClear(writer); }
        public void WriteSnapshot(IReadOnlyCollection<KeyValuePair<string, int>> items, JournalStreamWriter writer) { BeforeWrite(writer); dictionary.WriteSnapshot(items, writer); }
        public void Apply(JournalBufferReader input, IDurableDictionaryCommandHandler<string, int> consumer) => dictionary.Apply(input, consumer);
        void IDurableListCommandCodec<int>.WriteAdd(int item, JournalStreamWriter writer) { BeforeWrite(writer); list.WriteAdd(item, writer); }
        public void WriteSet(int index, int item, JournalStreamWriter writer) { BeforeWrite(writer); list.WriteSet(index, item, writer); }
        public void WriteInsert(int index, int item, JournalStreamWriter writer) { BeforeWrite(writer); list.WriteInsert(index, item, writer); }
        public void WriteRemoveAt(int index, JournalStreamWriter writer) { BeforeWrite(writer); list.WriteRemoveAt(index, writer); }
        void IDurableListCommandCodec<int>.WriteClear(JournalStreamWriter writer) { BeforeWrite(writer); list.WriteClear(writer); }
        void IDurableListCommandCodec<int>.WriteSnapshot(IReadOnlyCollection<int> items, JournalStreamWriter writer) { BeforeWrite(writer); list.WriteSnapshot(items, writer); }
        public void Apply(JournalBufferReader input, IDurableListCommandHandler<int> consumer) => list.Apply(input, consumer);
        public void WriteEnqueue(int item, JournalStreamWriter writer) { BeforeWrite(writer); queue.WriteEnqueue(item, writer); }
        public void WriteDequeue(JournalStreamWriter writer) { BeforeWrite(writer); queue.WriteDequeue(writer); }
        void IDurableQueueCommandCodec<int>.WriteClear(JournalStreamWriter writer) { BeforeWrite(writer); queue.WriteClear(writer); }
        void IDurableQueueCommandCodec<int>.WriteSnapshot(IReadOnlyCollection<int> items, JournalStreamWriter writer) { BeforeWrite(writer); queue.WriteSnapshot(items, writer); }
        public void Apply(JournalBufferReader input, IDurableQueueCommandHandler<int> consumer) => queue.Apply(input, consumer);
        void IDurableSetCommandCodec<int>.WriteAdd(int item, JournalStreamWriter writer) { BeforeWrite(writer); set.WriteAdd(item, writer); }
        public void WriteRemove(int item, JournalStreamWriter writer) { BeforeWrite(writer); set.WriteRemove(item, writer); }
        void IDurableSetCommandCodec<int>.WriteClear(JournalStreamWriter writer) { BeforeWrite(writer); set.WriteClear(writer); }
        void IDurableSetCommandCodec<int>.WriteSnapshot(IReadOnlyCollection<int> items, JournalStreamWriter writer) { BeforeWrite(writer); set.WriteSnapshot(items, writer); }
        public void Apply(JournalBufferReader input, IDurableSetCommandHandler<int> consumer) => set.Apply(input, consumer);
    }
}
