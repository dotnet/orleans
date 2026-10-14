using Xunit;
using static Orleans.Journaling.Tests.ObservationTestHelpers;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Tests the append-only durable log: sequencing, retention, committed and speculative reads, drafts, and recovery.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public class DurableLogTests : JournalingTestBase
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public Task Append_AssignsMonotonicSequences() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var log = CreateLog(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);

        Assert.Equal(0, log.Append("a"));
        Assert.Equal(1, log.Append("b"));
        Assert.Equal(2, log.Append("c"));
        Assert.Equal((0L, 3L, 0L, 3), (log.FirstSequence, log.NextSequence, log.CommittedSequence, log.Count));
        Assert.Equal("b", log[1]);
        Assert.True(log.TryGet(2, out var item));
        Assert.Equal("c", item);
        Assert.False(log.TryGet(3, out _));
        Assert.Equal(["a", "b", "c"], log);

        await sut.Manager.WriteStateAsync(CancellationToken);
        Assert.Equal(3, log.CommittedSequence);
    });

    [Fact]
    public Task TrimBefore_DiscardsEntriesAndPreservesSequence() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var log = CreateLog(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);
        log.Append("a");
        log.Append("b");
        log.Append("c");

        log.TrimBefore(2);
        log.TrimBefore(1);
        Assert.Equal((2L, 3L, 1), (log.FirstSequence, log.NextSequence, log.Count));
        Assert.Equal(["c"], log);
        Assert.Throws<ArgumentOutOfRangeException>(() => log[1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => log.TrimBefore(4));
        Assert.Throws<ArgumentOutOfRangeException>(() => log.TrimBefore(-1));

        log.TrimBefore(3);
        Assert.Empty(log);
        Assert.Equal(3, log.Append("d"));
    });

    [Fact]
    public Task CommittedRead_DeliversEntriesAfterTheirWrite() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var log = CreateLog(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);

        await using var read = log.ReadAsync(cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        log.Append("a");
        var next = read.MoveNextAsync();
        Assert.False(next.IsCompleted);

        await sut.Manager.WriteStateAsync(CancellationToken);
        Assert.True(await next);
        Assert.Equal(Entry(0, "a"), read.Current);

        log.Append("b");
        log.Append("c");
        await sut.Manager.WriteStateAsync(CancellationToken);
        Assert.Equal(Entry(1, "b"), await read.NextAsync());
        Assert.Equal(Entry(2, "c"), await read.NextAsync());
    });

    [Fact]
    public Task CommittedRead_ExcludesEntriesStagedDuringAnInFlightWrite() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var log = CreateLog(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);

        await using var read = log.ReadAsync(cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        log.Append("captured");
        sut.Storage.HoldNextWrite();
        var write = sut.Manager.WriteStateAsync(CancellationToken).AsTask();
        await sut.Storage.WriteStarted.Task;
        log.Append("staged");

        var next = read.MoveNextAsync();
        Assert.False(next.IsCompleted);
        sut.Storage.Release();
        await write;
        Assert.True(await next);
        Assert.Equal(Entry(0, "captured"), read.Current);
        Assert.Equal(1, log.CommittedSequence);

        next = read.MoveNextAsync();
        Assert.False(next.IsCompleted);
        await sut.Manager.WriteStateAsync(CancellationToken);
        Assert.True(await next);
        Assert.Equal(Entry(1, "staged"), read.Current);
    });

    [Fact]
    public Task SpeculativeRead_DeliversStagedEntriesThenWatermark() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var log = CreateLog(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);
        log.Append("committed");
        await sut.Manager.WriteStateAsync(CancellationToken);

        await using var read = log.ReadAsync(0, DurableWatchMode.Speculative, cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        Assert.Equal(Entry(0, "committed"), await read.NextAsync());

        log.Append("a");
        log.Append("b");
        Assert.Equal(Entry(1, "a", DurableChangeStatus.Staged), await read.NextAsync());
        Assert.Equal(Entry(2, "b", DurableChangeStatus.Staged), await read.NextAsync());

        var next = read.MoveNextAsync();
        Assert.False(next.IsCompleted);
        await sut.Manager.WriteStateAsync(CancellationToken);
        Assert.True(await next);
        Assert.Equal(new LogEvent<string> { Kind = LogEventKind.Watermark, Sequence = 3, Status = DurableChangeStatus.Committed }, read.Current);
    });

    [Fact]
    public Task Read_BeforeTrimPoint_Resets() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var log = CreateLog(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);
        for (var i = 0; i < 5; i++)
        {
            log.Append($"e{i}");
        }

        log.TrimBefore(3);
        await sut.Manager.WriteStateAsync(CancellationToken);

        await using var read = log.ReadAsync(1, cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        Assert.Equal(Reset(3), await read.NextAsync());
        Assert.Equal(Entry(3, "e3"), await read.NextAsync());

        // A trim which passes the reader's position resets it to the new first entry.
        log.TrimBefore(5);
        Assert.Equal(Reset(5), await read.NextAsync());
        log.Append("e5");
        await sut.Manager.WriteStateAsync(CancellationToken);
        Assert.Equal(Entry(5, "e5"), await read.NextAsync());
    });

    [Fact]
    public Task Read_AfterDeletion_Resets() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var log = CreateLog(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);
        log.Append("a");
        await sut.Manager.WriteStateAsync(CancellationToken);

        await using var read = log.ReadAsync(cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        Assert.Equal(Entry(0, "a"), await read.NextAsync());
        await sut.Manager.DeleteStateAsync(CancellationToken);
        Assert.Equal(Reset(0), await read.NextAsync());
        Assert.Equal((0L, 0L), (log.NextSequence, log.CommittedSequence));
    });

    [Fact]
    public Task Recovery_ReplaysJournalAndSnapshot() => RunOnActivationSchedulerAsync(async () =>
    {
        var storage = new GatedJournalStorage();
        var first = CreateSystem(storage);
        var log = CreateLog(first.Manager);
        await first.Lifecycle.OnStart(CancellationToken);
        log.Append("a");
        log.Append("b");
        log.Append("c");
        log.TrimBefore(1);
        await first.Manager.WriteStateAsync(CancellationToken);

        var appended = CreateSystem(storage);
        var appendedLog = CreateLog(appended.Manager);
        await appended.Lifecycle.OnStart(CancellationToken);
        Assert.Equal((1L, 3L, 3L), (appendedLog.FirstSequence, appendedLog.NextSequence, appendedLog.CommittedSequence));
        Assert.Equal(["b", "c"], appendedLog);

        appendedLog.Append("d");
        appendedLog.TrimBefore(2);
        storage.IsCompactionRequested = true;
        await appended.Manager.WriteStateAsync(CancellationToken);
        Assert.Equal(1, storage.ReplaceCount);
        Assert.Equal(4, appendedLog.CommittedSequence);

        var snapshotted = CreateSystem(storage);
        var snapshottedLog = CreateLog(snapshotted.Manager);
        await snapshotted.Lifecycle.OnStart(CancellationToken);
        Assert.Equal((2L, 4L, 4L), (snapshottedLog.FirstSequence, snapshottedLog.NextSequence, snapshottedLog.CommittedSequence));
        Assert.Equal(["c", "d"], snapshottedLog);
        Assert.Equal(4, snapshottedLog.Append("e"));
    });

    [Fact]
    public Task Draft_StreamsItemsAndCommitsEntry() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var log = CreateLog(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);

        await using var speculative = log.ReadAsync(0, DurableWatchMode.Speculative, cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        await using var committed = log.ReadAsync(cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        var committedNext = committed.MoveNextAsync();

        using var draft = log.BeginDraft();
        draft.Write("Hel");
        draft.Write("lo");
        Assert.Equal(2, draft.Count);
        Assert.Equal(DraftItem(draft.Id, 0, "Hel"), await speculative.NextAsync());
        Assert.Equal(DraftItem(draft.Id, 1, "lo"), await speculative.NextAsync());

        Assert.Equal(0, draft.Commit("Hello"));
        Assert.False(draft.IsOpen);
        Assert.Equal(0, draft.Count);
        Assert.Throws<InvalidOperationException>(() => draft.Write("!"));
        Assert.Throws<InvalidOperationException>(() => draft.Commit("!"));
        Assert.Equal(
            new LogEvent<string> { Kind = LogEventKind.DraftCommitted, Sequence = 0, DraftId = draft.Id, Status = DurableChangeStatus.Staged },
            await speculative.NextAsync());
        Assert.Equal(Entry(0, "Hello", DurableChangeStatus.Staged), await speculative.NextAsync());
        Assert.False(committedNext.IsCompleted);

        await sut.Manager.WriteStateAsync(CancellationToken);
        Assert.True(await committedNext);
        Assert.Equal(Entry(0, "Hello"), committed.Current);
        Assert.Equal(new LogEvent<string> { Kind = LogEventKind.Watermark, Sequence = 1, Status = DurableChangeStatus.Committed }, await speculative.NextAsync());
    });

    [Fact]
    public Task Draft_AbandonAndDispose_ReportAbandonment() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var log = CreateLog(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);

        await using var read = log.ReadAsync(0, DurableWatchMode.Speculative, cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        var failed = log.BeginDraft();
        failed.Write("partial");
        Assert.Equal(DraftItem(failed.Id, 0, "partial"), await read.NextAsync());
        failed.Abandon("The model stream failed.");
        failed.Abandon("ignored");
        Assert.Equal(Abandoned(failed.Id, "The model stream failed."), await read.NextAsync());

        var disposed = log.BeginDraft();
        disposed.Write("partial");
        Assert.Equal(DraftItem(disposed.Id, 0, "partial"), await read.NextAsync());
        disposed.Dispose();
        Assert.Equal(Abandoned(disposed.Id, "The draft was disposed before it was committed."), await read.NextAsync());

        var next = read.MoveNextAsync();
        Assert.False(next.IsCompleted);
        Assert.Equal(0, log.NextSequence);
        await sut.Manager.DisposeAsync();
        Assert.False(await next);
    });

    [Fact]
    public Task SpeculativeRead_StartingMidDraft_ReceivesItemsSoFar() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var log = CreateLog(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);
        log.Append("history");
        await sut.Manager.WriteStateAsync(CancellationToken);

        var draft = log.BeginDraft();
        draft.Write("a");
        draft.Write("b");

        await using var read = log.ReadAsync(0, DurableWatchMode.Speculative, cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        Assert.Equal(Entry(0, "history"), await read.NextAsync());
        Assert.Equal(DraftItem(draft.Id, 0, "a"), await read.NextAsync());
        Assert.Equal(DraftItem(draft.Id, 1, "b"), await read.NextAsync());
        draft.Write("c");
        Assert.Equal(DraftItem(draft.Id, 2, "c"), await read.NextAsync());

        // A committed reader does not observe drafts.
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        await using var committed = log.ReadAsync(1, cancellationToken: cancellation.Token).GetAsyncEnumerator(CancellationToken);
        var committedNext = committed.MoveNextAsync();
        Assert.False(committedNext.IsCompleted);
        draft.Abandon();
        Assert.Equal(Abandoned(draft.Id, "The draft was abandoned."), await read.NextAsync());
        Assert.False(committedNext.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await committedNext);
    });

    [Fact]
    public Task Reconnect_ToOpenDraft_RepeatsItsItems() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var log = CreateLog(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);
        var draft = log.BeginDraft();
        draft.Write("a");

        await using (var first = log.ReadAsync(0, DurableWatchMode.Speculative, cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken))
        {
            Assert.Equal(DraftItem(draft.Id, 0, "a"), await first.NextAsync());
        }

        draft.Write("b");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        await using var reconnected = log.ReadAsync(0, DurableWatchMode.Speculative, draft.Id, cancellation.Token).GetAsyncEnumerator(CancellationToken);
        Assert.Equal(DraftItem(draft.Id, 0, "a"), await reconnected.NextAsync());
        Assert.Equal(DraftItem(draft.Id, 1, "b"), await reconnected.NextAsync());
        var next = reconnected.MoveNextAsync();
        Assert.False(next.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await next);
    });

    [Fact]
    public Task Reconnect_ToClosedDraft_ReportsAbandonmentThenCommittedEntries() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var log = CreateLog(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);
        var draft = log.BeginDraft();
        draft.Write("Hel");
        draft.Commit("Hello");
        await sut.Manager.WriteStateAsync(CancellationToken);

        await using var read = log.ReadAsync(0, DurableWatchMode.Speculative, draft.Id, CancellationToken).GetAsyncEnumerator(CancellationToken);
        Assert.Equal(Abandoned(draft.Id, "The draft is no longer open."), await read.NextAsync());
        Assert.Equal(Entry(0, "Hello"), await read.NextAsync());
    });

    [Fact]
    public Task Reconnect_AfterActivationLoss_ReportsAbandonedDraft() => RunOnActivationSchedulerAsync(async () =>
    {
        var storage = new GatedJournalStorage();
        var lost = CreateSystem(storage);
        var lostLog = CreateLog(lost.Manager);
        await lost.Lifecycle.OnStart(CancellationToken);
        lostLog.Append("question");
        await lost.Manager.WriteStateAsync(CancellationToken);

        // The activation fails mid-response. Its draft and staged entries are lost.
        var draft = lostLog.BeginDraft();
        draft.Write("partial answer");
        lostLog.Append("staged");
        var committedCursor = lostLog.CommittedSequence;
        await lost.Manager.DisposeAsync();

        var recovered = CreateSystem(storage);
        var log = CreateLog(recovered.Manager);
        await recovered.Lifecycle.OnStart(CancellationToken);
        Assert.Equal(1, log.NextSequence);

        await using var read = log.ReadAsync(committedCursor, DurableWatchMode.Speculative, draft.Id, CancellationToken).GetAsyncEnumerator(CancellationToken);
        Assert.Equal(Abandoned(draft.Id, "The draft belonged to a previous activation."), await read.NextAsync());

        // The recovered activation regenerates the response from the last stored entry.
        using var retry = log.BeginDraft();
        Assert.NotEqual(draft.Id.ActivationId, retry.Id.ActivationId);
        retry.Write("full");
        Assert.Equal(DraftItem(retry.Id, 0, "full"), await read.NextAsync());
        retry.Commit("full answer");
        Assert.Equal(LogEventKind.DraftCommitted, (await read.NextAsync()).Kind);
        Assert.Equal(Entry(1, "full answer", DurableChangeStatus.Staged), await read.NextAsync());
    });

    [Fact]
    public Task DeletedLog_AbandonsOpenDrafts() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var log = CreateLog(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);
        await using var read = log.ReadAsync(0, DurableWatchMode.Speculative, cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        var draft = log.BeginDraft();
        draft.Write("a");
        Assert.Equal(DraftItem(draft.Id, 0, "a"), await read.NextAsync());

        await sut.Manager.DeleteStateAsync(CancellationToken);
        Assert.False(draft.IsOpen);
        Assert.Equal(Abandoned(draft.Id, "The log was reset."), await read.NextAsync());
    });

    [Fact]
    public Task ReadAsync_ValidatesArguments() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var log = CreateLog(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);
        Assert.Throws<ArgumentOutOfRangeException>(() => log.ReadAsync(-1, cancellationToken: CancellationToken));
        Assert.Throws<ArgumentOutOfRangeException>(() => log.ReadAsync(0, (DurableWatchMode)9, cancellationToken: CancellationToken));
    });

    [Fact]
    public void LogDraftId_FormatsAndParses()
    {
        var id = new LogDraftId(Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"), 42);
        Assert.Equal("0f8fad5bd9cb469fa16570867728950e.42", id.ToString());
        Assert.True(LogDraftId.TryParse(id.ToString(), out var parsed));
        Assert.Equal(id, parsed);
        Assert.False(LogDraftId.TryParse("0f8fad5bd9cb469fa16570867728950e", out _));
        Assert.False(LogDraftId.TryParse("not-a-guid.1", out _));
        Assert.False(LogDraftId.TryParse("0f8fad5bd9cb469fa16570867728950e.-1", out _));
    }

    [Fact]
    public void BinaryCodec_RoundTripsCommands()
    {
        var codec = new OrleansBinaryDurableLogCommandCodec<string>(CodecProvider.GetCodec<string>(), SessionPool);
        var handler = new RecordingLogHandler();

        codec.Apply(CodecTestHelpers.ReadBuffer(CodecTestHelpers.WriteEntry(writer => codec.WriteAppend("a", writer))), handler);
        codec.Apply(CodecTestHelpers.ReadBuffer(CodecTestHelpers.WriteEntry(writer => codec.WriteTrimBefore(long.MaxValue, writer))), handler);
        codec.Apply(CodecTestHelpers.ReadBuffer(CodecTestHelpers.WriteEntry(writer => codec.WriteSnapshot(7, ["b", "c"], writer))), handler);

        Assert.Equal(["append a", $"trim {long.MaxValue}", "reset 7 2", "append b", "append c"], handler.Commands);
    }

    private static LogEvent<string> Entry(long sequence, string item, DurableChangeStatus status = DurableChangeStatus.Committed) =>
        new() { Kind = LogEventKind.Entry, Sequence = sequence, Item = item, Status = status };

    private static LogEvent<string> Reset(long sequence) =>
        new() { Kind = LogEventKind.Reset, Sequence = sequence, Status = DurableChangeStatus.Committed };

    private static LogEvent<string> DraftItem(LogDraftId id, long position, string item) =>
        new() { Kind = LogEventKind.DraftItem, Sequence = position, Item = item, DraftId = id, Status = DurableChangeStatus.Draft };

    private static LogEvent<string> Abandoned(LogDraftId id, string reason) =>
        new() { Kind = LogEventKind.DraftAbandoned, DraftId = id, Reason = reason, Status = DurableChangeStatus.Draft };

    private (IJournaledStateManager Manager, GatedJournalStorage Storage, ILifecycleSubject Lifecycle) CreateSystem(GatedJournalStorage? storage = null)
    {
        storage ??= new GatedJournalStorage();
        storage.Inner.SetConfiguredJournalFormatKey(OrleansBinaryJournalFormat.JournalFormatKey);
        var sut = CreateTestSystem(storage);
        return (sut.Manager, storage, sut.Lifecycle);
    }

    private DurableLog<string> CreateLog(IJournaledStateManager manager, string name = "log") =>
        new(name, manager, new OrleansBinaryDurableLogCommandCodec<string>(CodecProvider.GetCodec<string>(), SessionPool));

    internal sealed class RecordingLogHandler : IDurableLogCommandHandler<string>
    {
        public List<string> Commands { get; } = [];

        public void ApplyAppend(string item) => Commands.Add($"append {item}");

        public void ApplyTrimBefore(long sequence) => Commands.Add($"trim {sequence}");

        public void Reset(long firstSequence, int capacityHint) => Commands.Add($"reset {firstSequence} {capacityHint}");
    }
}
