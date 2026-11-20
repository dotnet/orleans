using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Xunit;
using static Orleans.Journaling.Tests.ObservationTestHelpers;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Tests watch streams for durable values and log reads, including delivery after acknowledged writes, recovery,
/// deletion, cancellation, and manager failure.
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
        _ = value.WatchAsync(CancellationToken);
        _ = log.ReadAsync(cancellationToken: CancellationToken);
        await sut.Manager.WriteStateAsync(CancellationToken);

        Assert.Null(sut.Manager.AsJournaledStateManager().Observers);
    });

    [Fact]
    public Task Watch_BeforeRecovery_Throws() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var value = CreateValue(sut.Manager);
        var log = CreateLog(sut.Manager);

        // An unrecovered manager has no durable state to report.
        await using var watch = value.WatchAsync(cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await watch.MoveNextAsync());
        await using var read = log.ReadAsync(cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await read.MoveNextAsync());
        Assert.Null(sut.Manager.AsJournaledStateManager().Observers);

        await sut.Lifecycle.OnStart(CancellationToken);
        await using var recovered = value.WatchAsync(cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        Assert.Equal(Committed(null, version: 0), await recovered.NextAsync());
    });

    [Fact]
    public Task EndedWatch_RemovesItsRegistration() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var value = CreateValue(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);

        await using (var watch = value.WatchAsync(CancellationToken).GetAsyncEnumerator(CancellationToken))
        {
            await watch.NextAsync();
            var observers = sut.Manager.AsJournaledStateManager().Observers;
            Assert.NotNull(observers);
            Assert.True(observers.TryGetTopic(value, out _));
        }

        Assert.False(sut.Manager.AsJournaledStateManager().Observers!.TryGetTopic(value, out _));
    });

    [Fact]
    public Task ValueWatch_DeliversValuesAfterTheirWrite() => RunOnActivationSchedulerAsync(async () =>
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
    public Task ValueWatch_CollapsesToLatestValue() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var value = CreateValue(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);

        await using var committed = value.WatchAsync(CancellationToken).GetAsyncEnumerator(CancellationToken);
        await committed.NextAsync();

        value.Value = "a";
        value.Value = "b";
        value.Value = "c";
        var next = committed.MoveNextAsync();
        Assert.False(next.IsCompleted);
        await sut.Manager.WriteStateAsync(CancellationToken);
        Assert.True(await next);
        Assert.Equal(Committed("c", version: 1), committed.Current);

        next = committed.MoveNextAsync();
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
        var log = CreateLog(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);

        await using var valueWatch = value.WatchAsync(CancellationToken).GetAsyncEnumerator(CancellationToken);
        await using var read = log.ReadAsync(cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        await valueWatch.NextAsync();
        Assert.Equal(LogEventKind.CaughtUp, (await read.NextAsync()).Kind);

        value.Value = "captured";
        log.Append("captured");
        sut.Storage.HoldNextWrite();
        var firstWrite = sut.Manager.WriteStateAsync(CancellationToken).AsTask();
        await sut.Storage.WriteStarted.Task;

        // Another turn stages changes after the write captured its entries.
        value.Value = "staged";
        log.Append("staged");

        // A watch which begins now waits for the write which makes its value durable.
        await using var lateWatch = value.WatchAsync(CancellationToken).GetAsyncEnumerator(CancellationToken);
        var lateNext = lateWatch.MoveNextAsync();
        var valueNext = valueWatch.MoveNextAsync();
        var readNext = read.MoveNextAsync();
        Assert.False(lateNext.IsCompleted);
        Assert.False(valueNext.IsCompleted);
        Assert.False(readNext.IsCompleted);

        sut.Storage.Release();
        await firstWrite;
        Assert.True(await valueNext);
        Assert.Equal(Committed("captured", version: 1), valueWatch.Current);
        Assert.True(await readNext);
        Assert.Equal((LogEventKind.Entry, 0L, "captured"), (read.Current.Kind, read.Current.Sequence, read.Current.Item));

        valueNext = valueWatch.MoveNextAsync();
        readNext = read.MoveNextAsync();
        Assert.False(valueNext.IsCompleted);
        Assert.False(readNext.IsCompleted);
        Assert.False(lateNext.IsCompleted);

        await sut.Manager.WriteStateAsync(CancellationToken);
        Assert.True(await valueNext);
        Assert.Equal(Committed("staged", version: 2), valueWatch.Current);
        Assert.True(await readNext);
        Assert.Equal((LogEventKind.Entry, 1L, "staged"), (read.Current.Kind, read.Current.Sequence, read.Current.Item));
        Assert.True(await lateNext);
        Assert.Equal(Committed("staged", version: 2), lateWatch.Current);
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
    public Task OpenEntry_DefersTheWriteUntilItCapturesEveryState(bool dirtyValue) => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var dictionary = CreateDictionary(sut.Manager);
        var log = CreateLog(sut.Manager);
        var value = CreateValue(sut.Manager);
        var manual = new OpenEntryState();
        sut.Manager.RegisterStateMachine("manual", manual);
        await sut.Lifecycle.OnStart(CancellationToken);
        await sut.Manager.WriteStateAsync(CancellationToken);
        await using var read = log.ReadAsync(cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        await using var valueWatch = value.WatchAsync(CancellationToken).GetAsyncEnumerator(CancellationToken);
        await read.NextAsync();
        await valueWatch.NextAsync();

        dictionary["staged"] = 1;
        log.Append("staged");
        if (dirtyValue)
        {
            value.Value = "staged";
        }

        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var close = new ManualResetEventSlim();
        var openEntry = Task.Run(() =>
        {
            using var scope = manual.Writer.BeginEntry();
            opened.SetResult();
            close.Wait(CancellationToken);
        }, CancellationToken);
        await opened.Task;
        var segmentCount = sut.Storage.Inner.Segments.Count;

        // An entry which another thread holds open occupies the journal buffer, so the write waits for it rather than
        // acknowledging changes which it has not captured.
        var write = sut.Manager.WriteStateAsync(CancellationToken).AsTask();
        await Task.Delay(TimeSpan.FromMilliseconds(100), CancellationToken);
        Assert.False(write.IsCompleted);
        Assert.Equal(segmentCount, sut.Storage.Inner.Segments.Count);
        Assert.Equal(0, log.CommittedSequence);

        close.Set();
        await openEntry;
        await write;
        Assert.Equal((LogEventKind.Entry, "staged"), ((await read.NextAsync()).Kind, read.Current.Item));
        Assert.Equal(1, log.CommittedSequence);
        if (dirtyValue)
        {
            Assert.Equal(Committed("staged", version: 2), await valueWatch.NextAsync());
        }

        var recovered = CreateSystem(sut.Storage);
        var recoveredDictionary = CreateDictionary(recovered.Manager);
        var recoveredValue = CreateValue(recovered.Manager);
        var recoveredLog = CreateLog(recovered.Manager);
        recovered.Manager.RegisterStateMachine("manual", new OpenEntryState());
        await recovered.Lifecycle.OnStart(CancellationToken);
        Assert.Equal([new("staged", 1)], recoveredDictionary);
        Assert.Equal(["staged"], recoveredLog);
        Assert.Equal(dirtyValue ? "staged" : null, recoveredValue.Value);
    });

    [Fact]
    public Task DurableValue_OnPersisted_AcknowledgesOnlyTheCapturedValue() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var value = CreateValue(sut.Manager);
        var other = CreateDictionary(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);
        var persisted = 0;
        value.OnPersisted = () => persisted++;

        // A write which does not capture the value does not acknowledge it.
        other["a"] = 1;
        await sut.Manager.WriteStateAsync(CancellationToken);
        Assert.Equal(0, persisted);

        value.Value = "captured";
        sut.Storage.HoldNextWrite();
        var write = sut.Manager.WriteStateAsync(CancellationToken).AsTask();
        await sut.Storage.WriteStarted.Task;
        value.Value = "later";
        sut.Storage.Release();
        await write;
        Assert.Equal(1, persisted);

        // The value set during the write is acknowledged by the write which captures it.
        other["b"] = 2;
        sut.Storage.HoldNextWrite();
        write = sut.Manager.WriteStateAsync(CancellationToken).AsTask();
        await sut.Storage.WriteStarted.Task;
        Assert.Equal(1, persisted);
        sut.Storage.Release();
        await write;
        Assert.Equal(2, persisted);
    });

    [Fact]
    public Task DurableTaskCompletionSource_CompletesAfterTheWriteWhichCapturesIt() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var tcs = new DurableTaskCompletionSource<int>(
            "tcs",
            sut.Manager,
            new OrleansBinaryDurableTaskCompletionSourceCommandCodec<int>(CodecProvider.GetCodec<int>(), CodecProvider.GetCodec<Exception>(), SessionPool),
            ServiceProvider.GetRequiredService<DeepCopier<int>>(),
            ServiceProvider.GetRequiredService<DeepCopier<Exception>>());
        var other = CreateDictionary(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);

        // The write in flight captured the pending state, so its acknowledgement does not complete the task.
        other["a"] = 1;
        sut.Storage.HoldNextWrite();
        var write = sut.Manager.WriteStateAsync(CancellationToken).AsTask();
        await sut.Storage.WriteStarted.Task;
        Assert.True(tcs.TrySetResult(42));
        sut.Storage.Release();
        await write;
        Assert.False(tcs.Task.IsCompleted);

        sut.Storage.HoldNextWrite();
        write = sut.Manager.WriteStateAsync(CancellationToken).AsTask();
        await sut.Storage.WriteStarted.Task;
        Assert.False(tcs.Task.IsCompleted);
        sut.Storage.Release();
        await write;
        Assert.Equal(42, await tcs.Task);
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
    public Task CollectionEncodingFailure_PreservesDurability(string operation) => RunOnActivationSchedulerAsync(async () =>
    {
        foreach (var pending in new[] { false, true })
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
            if (pending)
            {
                dictionary["b"] = 2;
                list.Add(2);
                queue.Enqueue(2);
                set.Add(2);
            }

            var expectedDictionary = dictionary.OrderBy(static pair => pair.Key).ToArray();
            var expectedList = list.ToArray();
            var expectedQueue = queue.ToArray();
            var expectedSet = set.Order().ToArray();
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

            // The failed operation changes neither the state nor the journal, so the next write and a recovery agree with it.
            Assert.Equal(expectedDictionary, dictionary.OrderBy(static pair => pair.Key));
            Assert.Equal(expectedList, list);
            Assert.Equal(expectedQueue, queue);
            Assert.Equal(expectedSet, set.Order());
            await sut.Manager.WriteStateAsync(CancellationToken);

            var recovered = CreateSystem(sut.Storage);
            var recoveredDictionary = CreateDictionary(recovered.Manager);
            var recoveredList = CreateList(recovered.Manager);
            var recoveredQueue = CreateQueue(recovered.Manager);
            var recoveredSet = CreateSet(recovered.Manager);
            await recovered.Lifecycle.OnStart(CancellationToken);
            Assert.Equal(expectedDictionary, recoveredDictionary.OrderBy(static pair => pair.Key));
            Assert.Equal(expectedList, recoveredList);
            Assert.Equal(expectedQueue, recoveredQueue);
            Assert.Equal(expectedSet, recoveredSet.Order());
            await sut.Manager.DisposeAsync();
            await recovered.Manager.DisposeAsync();
        }
    });

    [Fact]
    public Task SeveralWatches_ShareOneHubAndOneWatermark() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var value = CreateValue(sut.Manager);
        var other = CreateValue(sut.Manager, "other");
        var log = CreateLog(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);

        await using var valueWatch = value.WatchAsync(CancellationToken).GetAsyncEnumerator(CancellationToken);
        await using var otherWatch = other.WatchAsync(CancellationToken).GetAsyncEnumerator(CancellationToken);
        await using var logRead = log.ReadAsync(cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        await valueWatch.NextAsync();
        await otherWatch.NextAsync();
        Assert.Equal(LogEventKind.CaughtUp, (await logRead.NextAsync()).Kind);
        var observers = sut.Manager.AsJournaledStateManager().Observers;
        Assert.NotNull(observers);

        value.Value = "v";
        other.Value = "o";
        log.Append("e");
        var pending = new[] { valueWatch.MoveNextAsync().AsTask(), otherWatch.MoveNextAsync().AsTask(), logRead.MoveNextAsync().AsTask() };
        Assert.All(pending, static task => Assert.False(task.IsCompleted));

        await sut.Manager.WriteStateAsync(CancellationToken);
        Assert.All(await Task.WhenAll(pending), Assert.True);
        Assert.Same(observers, sut.Manager.AsJournaledStateManager().Observers);
        Assert.Equal(Committed("v", version: 1), valueWatch.Current);
        Assert.Equal(Committed("o", version: 1), otherWatch.Current);
        Assert.Equal((LogEventKind.Entry, 0L, "e"), (logRead.Current.Kind, logRead.Current.Sequence, logRead.Current.Item));
    });

    [Fact]
    public Task RecoveredState_IsDeliveredAsCommitted() => RunOnActivationSchedulerAsync(async () =>
    {
        var storage = new GatedJournalStorage();
        var first = CreateSystem(storage);
        var firstValue = CreateValue(first.Manager);
        var firstLog = CreateLog(first.Manager);
        await first.Lifecycle.OnStart(CancellationToken);
        firstValue.Value = "recovered";
        firstLog.Append("recovered");
        await first.Manager.WriteStateAsync(CancellationToken);

        var second = CreateSystem(storage);
        var value = CreateValue(second.Manager);
        var log = CreateLog(second.Manager);
        await second.Lifecycle.OnStart(CancellationToken);

        await using var valueWatch = value.WatchAsync(CancellationToken).GetAsyncEnumerator(CancellationToken);
        await using var read = log.ReadAsync(cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        Assert.Equal(Committed("recovered", version: 0), await valueWatch.NextAsync());
        Assert.Equal((LogEventKind.Entry, 0L, "recovered"), ((await read.NextAsync()).Kind, read.Current.Sequence, read.Current.Item));
        Assert.Equal((LogEventKind.CaughtUp, 1L), ((await read.NextAsync()).Kind, read.Current.Sequence));
    });

    [Fact]
    public Task FailedWrite_FaultsWatchesWithoutDeliveringStagedChanges() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var value = CreateValue(sut.Manager);
        var log = CreateLog(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);

        await using var watch = value.WatchAsync(CancellationToken).GetAsyncEnumerator(CancellationToken);
        await using var read = log.ReadAsync(cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        await watch.NextAsync();
        Assert.Equal(LogEventKind.CaughtUp, (await read.NextAsync()).Kind);
        var watchNext = watch.MoveNextAsync().AsTask();
        var readNext = read.MoveNextAsync().AsTask();

        var failure = new IOException("Storage is unavailable.");
        sut.Storage.Failure = failure;
        value.Value = "lost";
        log.Append("lost");
        await Assert.ThrowsAsync<IOException>(async () => await sut.Manager.WriteStateAsync(CancellationToken));

        var watchException = await Assert.ThrowsAsync<InvalidOperationException>(() => watchNext);
        Assert.Same(failure, watchException.InnerException);
        var readException = await Assert.ThrowsAsync<InvalidOperationException>(() => readNext);
        Assert.Same(failure, readException.InnerException);

        // The fenced manager accepts no new watchers.
        await using var lateWatch = value.WatchAsync(CancellationToken).GetAsyncEnumerator(CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await lateWatch.MoveNextAsync());
        await using var lateRead = log.ReadAsync(cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await lateRead.MoveNextAsync());
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
        var value = CreateValue(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);

        using var cancellation = new CancellationTokenSource();
        await using var watch = value.WatchAsync(cancellation.Token).GetAsyncEnumerator(CancellationToken);
        await watch.NextAsync();
        var next = watch.MoveNextAsync().AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
        Assert.False(sut.Manager.AsJournaledStateManager().Observers!.TryGetTopic(value, out _));
    });

    [Fact]
    public Task DeletedState_DeliversTheResetValue() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var value = CreateValue(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);
        value.Value = "a";
        await sut.Manager.WriteStateAsync(CancellationToken);

        await using var watch = value.WatchAsync(CancellationToken).GetAsyncEnumerator(CancellationToken);
        Assert.Equal("a", (await watch.NextAsync()).Value);
        await sut.Manager.DeleteStateAsync(CancellationToken);

        Assert.Null((await watch.NextAsync()).Value);
    });

    [Fact]
    public void UnsupportedValue_Throws() =>
        Assert.Throws<NotSupportedException>(() => new UnsupportedValue().WatchAsync(CancellationToken));

    private static ValueChange<string> Committed(string? value, long version) =>
        new() { Value = value, Version = version };

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
