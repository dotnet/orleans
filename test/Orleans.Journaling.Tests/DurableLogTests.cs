using CsCheck;
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
        Assert.Equal(CaughtUp(0), await read.NextAsync());
        log.Append("a");
        var next = read.MoveNextAsync();
        Assert.False(next.IsCompleted);

        await sut.Manager.WriteStateAsync(CancellationToken);
        Assert.True(await next);
        Assert.Equal(Entry(0, "a"), read.Current with { Epoch = 0 });

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
        Assert.Equal(CaughtUp(0), await read.NextAsync());
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
        Assert.Equal(Entry(0, "captured"), read.Current with { Epoch = 0 });
        Assert.Equal(1, log.CommittedSequence);

        next = read.MoveNextAsync();
        Assert.False(next.IsCompleted);
        await sut.Manager.WriteStateAsync(CancellationToken);
        Assert.True(await next);
        Assert.Equal(Entry(1, "staged"), read.Current with { Epoch = 0 });
    });

    [Fact]
    public Task StagedRead_DeliversStagedEntriesThenWatermark() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var log = CreateLog(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);
        log.Append("committed");
        await sut.Manager.WriteStateAsync(CancellationToken);

        await using var read = log.ReadAsync(0, mode: DurableWatchMode.Staged | DurableWatchMode.Drafts, cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        Assert.Equal(Entry(0, "committed"), await read.NextAsync());
        Assert.Equal(CaughtUp(1), await read.NextAsync());

        log.Append("a");
        log.Append("b");
        Assert.Equal(Entry(1, "a", DurableChangeStatus.Staged), await read.NextAsync());
        Assert.Equal(Entry(2, "b", DurableChangeStatus.Staged), await read.NextAsync());

        var next = read.MoveNextAsync();
        Assert.False(next.IsCompleted);
        await sut.Manager.WriteStateAsync(CancellationToken);
        Assert.True(await next);
        Assert.Equal(new LogEvent<string> { Kind = LogEventKind.Watermark, Sequence = 3, Status = DurableChangeStatus.Committed }, read.Current with { Epoch = 0 });
    });

    [Fact]
    public Task Read_BeforeTrimPoint_ReportsTrim() => RunOnActivationSchedulerAsync(async () =>
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
        Assert.Equal(Trimmed(3), await read.NextAsync());
        Assert.Equal(Entry(3, "e3"), await read.NextAsync());
        Assert.Equal(Entry(4, "e4"), await read.NextAsync());
        Assert.Equal(CaughtUp(5), await read.NextAsync());

        // A committed reader observes a trim once it is durable.
        log.TrimBefore(4);
        var next = read.MoveNextAsync();
        Assert.False(next.IsCompleted);
        await sut.Manager.WriteStateAsync(CancellationToken);
        Assert.True(await next);
        Assert.Equal(Trimmed(4), read.Current with { Epoch = 0 });

        // A trim which reaches the reader's position is reported, so a caught-up reader observes every trim.
        log.TrimBefore(5);
        await sut.Manager.WriteStateAsync(CancellationToken);
        Assert.Equal(Trimmed(5), await read.NextAsync());
        log.Append("e5");
        await sut.Manager.WriteStateAsync(CancellationToken);
        Assert.Equal(Entry(5, "e5"), await read.NextAsync());
    });

    [Fact]
    public Task CommittedRead_PositionedInUncommittedTrim_AwaitsTheTrim() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var log = CreateLog(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);
        log.Append("a");
        log.Append("b");
        log.Append("c");
        await sut.Manager.WriteStateAsync(CancellationToken);

        await using var read = log.ReadAsync(0, cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        Assert.Equal(Entry(0, "a"), await read.NextAsync());

        // The trimmed entries are durable, but the trim is not, so the reader reports neither until the trim's write.
        log.TrimBefore(2);
        var next = read.MoveNextAsync();
        Assert.False(next.IsCompleted);
        await sut.Manager.WriteStateAsync(CancellationToken);
        Assert.True(await next);
        Assert.Equal(Trimmed(2), read.Current with { Epoch = 0 });
        Assert.Equal(Entry(2, "c"), await read.NextAsync());
        Assert.Equal(CaughtUp(3), await read.NextAsync());
    });

    [Fact]
    public Task StagedRead_ReportsStagedTrim() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var log = CreateLog(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);
        log.Append("a");
        await sut.Manager.WriteStateAsync(CancellationToken);

        await using var read = log.ReadAsync(0, mode: DurableWatchMode.Staged | DurableWatchMode.Drafts, cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        Assert.Equal(Entry(0, "a"), await read.NextAsync());
        Assert.Equal(CaughtUp(1), await read.NextAsync());

        log.TrimBefore(1);
        Assert.Equal(Trimmed(1, DurableChangeStatus.Staged), await read.NextAsync());
        var next = read.MoveNextAsync();
        Assert.False(next.IsCompleted);
        await sut.Manager.WriteStateAsync(CancellationToken);
        Assert.True(await next);
        Assert.Equal(Trimmed(1), read.Current with { Epoch = 0 });
    });

    [Theory]
    [InlineData("clear")]
    [InlineData("delete")]
    [InlineData("cursor")]
    public Task CommittedReset_PrecedesStagedTrim(string cause) => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var log = CreateLog(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);
        log.Append("a");
        log.Append("b");
        await sut.Manager.WriteStateAsync(CancellationToken);
        await using var read = log.ReadAsync(cause == "cursor" ? 100 : 0, mode: DurableWatchMode.Staged, cancellationToken: CancellationToken)
            .GetAsyncEnumerator(CancellationToken);
        if (cause != "cursor")
        {
            Assert.Equal(Entry(0, "a"), await read.NextAsync());
        }

        long resetSequence;
        long trimSequence;
        if (cause == "clear")
        {
            log.Clear();
            log.Append("summary");
            log.Append("next");
            sut.Storage.HoldNextWrite();
            var write = sut.Manager.WriteStateAsync(CancellationToken).AsTask();
            await sut.Storage.WriteStarted.Task;
            log.TrimBefore(4);
            sut.Storage.Release();
            await write;
            resetSequence = 3;
            trimSequence = 4;
        }
        else
        {
            if (cause == "delete")
            {
                await sut.Manager.DeleteStateAsync(CancellationToken);
                log.Append("replacement-a");
                log.Append("replacement-b");
            }

            log.TrimBefore(1);
            resetSequence = 0;
            trimSequence = 1;
        }

        Assert.Equal(Reset(resetSequence), await read.NextAsync());
        Assert.Equal(Trimmed(trimSequence, DurableChangeStatus.Staged), await read.NextAsync());
    });

    [Fact]
    public Task CommittedClear_IsVisibleWhileLaterClearIsStaged() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var log = CreateLog(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);
        log.Append("a");
        log.Append("b");
        await sut.Manager.WriteStateAsync(CancellationToken);
        await using var read = log.ReadAsync(cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        Assert.Equal(Entry(0, "a"), await read.NextAsync());
        Assert.Equal(Entry(1, "b"), await read.NextAsync());
        Assert.Equal(CaughtUp(2), await read.NextAsync());

        log.Clear();
        sut.Storage.HoldNextWrite();
        var write = sut.Manager.WriteStateAsync(CancellationToken).AsTask();
        await sut.Storage.WriteStarted.Task;
        log.Clear();
        sut.Storage.Release();
        await write;
        Assert.Equal(Reset(3), await read.NextAsync());
        var next = read.MoveNextAsync();
        Assert.False(next.IsCompleted);
        await sut.Manager.WriteStateAsync(CancellationToken);
        Assert.True(await next);
        Assert.Equal(Reset(4), read.Current with { Epoch = 0 });
    });

    [Fact]
    public Task Clear_ReplacesHistoryAndResetsReaders() => RunOnActivationSchedulerAsync(async () =>
    {
        var storage = new GatedJournalStorage();
        var sut = CreateSystem(storage);
        var log = CreateLog(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);
        log.Append("a");
        log.Append("b");
        await sut.Manager.WriteStateAsync(CancellationToken);

        await using var committed = log.ReadAsync(0, cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        await using var speculative = log.ReadAsync(0, mode: DurableWatchMode.Staged | DurableWatchMode.Drafts, cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        Assert.Equal(Entry(0, "a"), await committed.NextAsync());
        Assert.Equal(Entry(1, "b"), await committed.NextAsync());
        Assert.Equal(CaughtUp(2), await committed.NextAsync());
        Assert.Equal(Entry(0, "a"), await speculative.NextAsync());
        Assert.Equal(Entry(1, "b"), await speculative.NextAsync());
        Assert.Equal(CaughtUp(2), await speculative.NextAsync());

        // The clear consumes sequence 2. Compacted history follows it.
        log.Clear();
        Assert.Equal((3L, 3L, 0), (log.FirstSequence, log.NextSequence, log.Count));
        Assert.Equal(3, log.Append("summary"));
        Assert.Equal(Reset(3, DurableChangeStatus.Staged), await speculative.NextAsync());
        Assert.Equal(Entry(3, "summary", DurableChangeStatus.Staged), await speculative.NextAsync());

        // The committed reader observes the clear and its replacement once they are durable.
        var next = committed.MoveNextAsync();
        Assert.False(next.IsCompleted);
        await sut.Manager.WriteStateAsync(CancellationToken);
        Assert.True(await next);
        Assert.Equal(Reset(3), committed.Current with { Epoch = 0 });
        Assert.Equal(Entry(3, "summary"), await committed.NextAsync());
        Assert.Equal(Watermark(4), await speculative.NextAsync());

        // Positions at or before the clear observed replaced history, including in a later activation.
        var recovered = CreateSystem(storage);
        var recoveredLog = CreateLog(recovered.Manager);
        await recovered.Lifecycle.OnStart(CancellationToken);
        Assert.Equal((3L, 4L), (recoveredLog.FirstSequence, recoveredLog.NextSequence));
        await using (var resumed = recoveredLog.ReadAsync(2, cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken))
        {
            Assert.Equal(Reset(3), await resumed.NextAsync());
            Assert.Equal(Entry(3, "summary"), await resumed.NextAsync());
        }

        await using (var resumed = recoveredLog.ReadAsync(3, cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken))
        {
            Assert.Equal(Entry(3, "summary"), await resumed.NextAsync());
        }

        // Trimming after a clear is still reported as a clear to positions before it.
        recoveredLog.Append("next");
        recoveredLog.TrimBefore(4);
        storage.IsCompactionRequested = true;
        await recovered.Manager.WriteStateAsync(CancellationToken);
        var snapshotted = CreateSystem(storage);
        var snapshottedLog = CreateLog(snapshotted.Manager);
        await snapshotted.Lifecycle.OnStart(CancellationToken);
        await using (var resumed = snapshottedLog.ReadAsync(1, cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken))
        {
            Assert.Equal(Reset(4), await resumed.NextAsync());
        }

        await using (var resumed = snapshottedLog.ReadAsync(3, cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken))
        {
            Assert.Equal(Trimmed(4), await resumed.NextAsync());
        }
    });

    [Fact]
    public Task CaughtUp_FollowsEntriesCommittedWhenTheReadBegan() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var log = CreateLog(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);
        log.Append("a");
        await sut.Manager.WriteStateAsync(CancellationToken);
        log.Append("staged");

        await using var read = log.ReadAsync(0, mode: DurableWatchMode.Staged | DurableWatchMode.Drafts, cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        Assert.Equal(Entry(0, "a"), await read.NextAsync());
        Assert.Equal(CaughtUp(1), await read.NextAsync());
        Assert.Equal(Entry(1, "staged", DurableChangeStatus.Staged), await read.NextAsync());

        // Entries appended later do not repeat the event.
        await sut.Manager.WriteStateAsync(CancellationToken);
        Assert.Equal(Watermark(2), await read.NextAsync());
        log.Append("later");
        Assert.Equal(Entry(2, "later", DurableChangeStatus.Staged), await read.NextAsync());
    });

    [Fact]
    public Task Seal_EndsReadersAfterTheFinalEntry() => RunOnActivationSchedulerAsync(async () =>
    {
        var storage = new GatedJournalStorage();
        var sut = CreateSystem(storage);
        var log = CreateLog(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);

        await using var committed = log.ReadAsync(0, cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        await using var speculative = log.ReadAsync(0, mode: DurableWatchMode.Staged | DurableWatchMode.Drafts, cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        Assert.Equal(CaughtUp(0), await committed.NextAsync());
        Assert.Equal(CaughtUp(0), await speculative.NextAsync());

        var draft = log.BeginDraft();
        draft.Write("partial");
        Assert.Equal(DraftItem(draft.Id, 0, "partial"), await speculative.NextAsync());
        log.Append("terminated");
        log.Seal();
        log.Seal();
        Assert.True(log.IsSealed);
        Assert.False(draft.IsOpen);
        Assert.Contains("sealed", Assert.Throws<InvalidOperationException>(() => draft.Commit()).Message);
        Assert.Throws<InvalidOperationException>(() => log.Append("late"));
        Assert.Throws<InvalidOperationException>(() => log.Clear());
        Assert.Throws<InvalidOperationException>(() => log.BeginDraft());

        Assert.Equal(Abandoned(draft.Id, LogDraftAbandonReason.Sealed), await speculative.NextAsync());
        Assert.Equal(Entry(0, "terminated", DurableChangeStatus.Staged), await speculative.NextAsync());

        // Readers end once the seal is durable.
        var next = committed.MoveNextAsync();
        Assert.False(next.IsCompleted);
        await sut.Manager.WriteStateAsync(CancellationToken);
        Assert.True(await next);
        Assert.Equal(Entry(0, "terminated"), committed.Current with { Epoch = 0 });
        Assert.Equal(Sealed(1), await committed.NextAsync());
        Assert.False(await committed.MoveNextAsync());
        Assert.Equal(Watermark(1), await speculative.NextAsync());
        Assert.Equal(Sealed(1), await speculative.NextAsync());
        Assert.False(await speculative.MoveNextAsync());

        // A sealed log can be trimmed, and its seal is durable.
        log.TrimBefore(1);
        await sut.Manager.WriteStateAsync(CancellationToken);
        var recovered = CreateSystem(storage);
        var recoveredLog = CreateLog(recovered.Manager);
        await recovered.Lifecycle.OnStart(CancellationToken);
        Assert.True(recoveredLog.IsSealed);
        await using var late = recoveredLog.ReadAsync(0, cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        Assert.Equal(Trimmed(1), await late.NextAsync());
        Assert.Equal(CaughtUp(1), await late.NextAsync());
        Assert.Equal(Sealed(1), await late.NextAsync());
        Assert.False(await late.MoveNextAsync());

        // Deleting the journal unseals the log.
        await recovered.Manager.DeleteStateAsync(CancellationToken);
        Assert.False(recoveredLog.IsSealed);
        Assert.Equal(0, recoveredLog.Append("restarted"));
    });

    [Fact]
    public Task Read_AfterDeletion_Resets() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var log = CreateLog(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);
        log.Append("a");
        log.Append("b");
        await sut.Manager.WriteStateAsync(CancellationToken);

        await using var read = log.ReadAsync(cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        Assert.Equal(Entry(0, "a"), await read.NextAsync());
        await sut.Manager.DeleteStateAsync(CancellationToken);
        Assert.Equal((0L, 0L), (log.NextSequence, log.CommittedSequence));

        // New history which reaches the reader's position before it resumes is reported as a reset.
        log.Append("x");
        log.Append("y");
        await sut.Manager.WriteStateAsync(CancellationToken);
        Assert.Equal(Reset(0), await read.NextAsync());
        Assert.Equal(CaughtUp(0), await read.NextAsync());
        Assert.Equal(Entry(0, "x"), await read.NextAsync());
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

        await using var speculative = log.ReadAsync(0, mode: DurableWatchMode.Staged | DurableWatchMode.Drafts, cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        await using var committed = log.ReadAsync(cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        Assert.Equal(CaughtUp(0), await speculative.NextAsync());
        Assert.Equal(CaughtUp(0), await committed.NextAsync());
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
            Committed(draft.Id, 0, 1, DurableChangeStatus.Staged),
            await speculative.NextAsync());
        Assert.Equal(Entry(0, "Hello", DurableChangeStatus.Staged), await speculative.NextAsync());
        Assert.False(committedNext.IsCompleted);

        await sut.Manager.WriteStateAsync(CancellationToken);
        Assert.True(await committedNext);
        Assert.Equal(Entry(0, "Hello"), committed.Current with { Epoch = 0 });
        Assert.Equal(new LogEvent<string> { Kind = LogEventKind.Watermark, Sequence = 1, Status = DurableChangeStatus.Committed }, await speculative.NextAsync());
    });

    [Fact]
    public Task Draft_AbandonAndDispose_ReportAbandonment() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var log = CreateLog(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);

        await using var read = log.ReadAsync(0, mode: DurableWatchMode.Staged | DurableWatchMode.Drafts, cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        Assert.Equal(CaughtUp(0), await read.NextAsync());
        var failed = log.BeginDraft();
        failed.Write("partial");
        Assert.Equal(DraftItem(failed.Id, 0, "partial"), await read.NextAsync());
        failed.Abandon("The model stream failed.");
        failed.Abandon("ignored");
        Assert.Equal(Abandoned(failed.Id, LogDraftAbandonReason.Abandoned, "The model stream failed."), await read.NextAsync());

        var disposed = log.BeginDraft();
        disposed.Write("partial");
        Assert.Equal(DraftItem(disposed.Id, 0, "partial"), await read.NextAsync());
        disposed.Dispose();
        Assert.Equal(Abandoned(disposed.Id, LogDraftAbandonReason.Disposed), await read.NextAsync());

        var next = read.MoveNextAsync();
        Assert.False(next.IsCompleted);
        Assert.Equal(0, log.NextSequence);
        await sut.Manager.DisposeAsync();
        Assert.False(await next);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Draft_CleanupWhileCancellationPropagates_AbandonsTheDraft(bool dispose) => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var log = CreateLog(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);

        await using var read = log.ReadAsync(0, mode: DurableWatchMode.Staged | DurableWatchMode.Drafts, cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        Assert.Equal(CaughtUp(0), await read.NextAsync());
        var draft = log.BeginDraft();
        draft.Write("partial");
        Assert.Equal(DraftItem(draft.Id, 0, "partial"), await read.NextAsync());

        // A using statement or finally block abandons the draft while the cancellation propagates unchanged.
        var canceled = new OperationCanceledException("The operation was canceled.");
        Assert.Same(canceled, Assert.Throws<OperationCanceledException>(CleanUp));

        Assert.False(draft.IsOpen);
        Assert.Equal(0, draft.Count);
        draft.Abandon("already closed");
        draft.Dispose();
        var expected = dispose
            ? Abandoned(draft.Id, LogDraftAbandonReason.Disposed)
            : Abandoned(draft.Id, LogDraftAbandonReason.Abandoned, "canceled");
        Assert.Equal(expected, await read.NextAsync());
        Assert.Empty(log);
        Assert.Equal(0, log.NextSequence);

        void CleanUp()
        {
            using (draft)
            {
                try
                {
                    throw canceled;
                }
                finally
                {
                    if (!dispose)
                    {
                        draft.Abandon("canceled");
                    }
                }
            }
        }
    });

    [Fact]
    public Task Draft_CommitDuringCleanup_CommitsAndTheNextDraftContinuesNumbering() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var log = CreateLog(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);

        await using var read = log.ReadAsync(0, mode: DurableWatchMode.Staged | DurableWatchMode.Drafts, cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        Assert.Equal(CaughtUp(0), await read.NextAsync());
        var draft = log.BeginDraft();
        draft.Write("partial");
        Assert.Equal(DraftItem(draft.Id, 0, "partial"), await read.NextAsync());

        // Cleanup which runs after a cancellation commits the draft, and the next journal write records its entry.
        Assert.Equal(0, draft.Commit("partial"));
        Assert.False(draft.IsOpen);
        await sut.Manager.WriteStateAsync(CancellationToken);
        Assert.Equal((1L, 1L), (log.NextSequence, log.CommittedSequence));
        Assert.Equal(["partial"], log);

        // The activation's next draft follows the committed one.
        using var following = log.BeginDraft();
        Assert.Equal((draft.Id.InstanceId, draft.Id.Number + 1), (following.Id.InstanceId, following.Id.Number));
        following.Write("next");
        LogEvent<string> item;
        do
        {
            item = await read.NextAsync();
        }
        while (item.Kind is not LogEventKind.DraftItem);

        Assert.Equal(DraftItem(following.Id, 0, "next"), item);
    });

    [Fact]
    public Task CommittedWithDraftsRead_DeliversDraftsAndOnlyDurableEntries() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var log = CreateLog(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);

        await using var read = log.ReadAsync(0, mode: DurableWatchMode.Committed | DurableWatchMode.Drafts, cancellationToken: CancellationToken)
            .GetAsyncEnumerator(CancellationToken);
        Assert.Equal(CaughtUp(0), await read.NextAsync());
        log.Append("question");
        var draft = log.BeginDraft();
        draft.Write("Hel");
        Assert.Equal(DraftItem(draft.Id, 0, "Hel"), await read.NextAsync());

        // The commit is reported with its entry once the entry is durable.
        draft.Commit("Hello");
        var next = read.MoveNextAsync();
        Assert.False(next.IsCompleted);
        await sut.Manager.WriteStateAsync(CancellationToken);
        Assert.True(await next);
        Assert.Equal(
            Committed(draft.Id, 1, 1),
            read.Current with { Epoch = 0 });
        Assert.Equal(Entry(0, "question"), await read.NextAsync());
        Assert.Equal(Entry(1, "Hello"), await read.NextAsync());

        // The reader receives no watermarks.
        next = read.MoveNextAsync();
        await sut.Manager.WriteStateAsync(CancellationToken);
        log.Append("later");
        Assert.False(next.IsCompleted);
        await sut.Manager.WriteStateAsync(CancellationToken);
        Assert.True(await next);
        Assert.Equal(Entry(2, "later"), read.Current with { Epoch = 0 });
    });

    [Fact]
    public Task DraftRead_StartingMidDraft_ReceivesItemsSoFar() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var log = CreateLog(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);
        log.Append("history");
        await sut.Manager.WriteStateAsync(CancellationToken);

        var draft = log.BeginDraft();
        draft.Write("a");
        draft.Write("b");

        await using var read = log.ReadAsync(0, mode: DurableWatchMode.Staged | DurableWatchMode.Drafts, cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        Assert.Equal(Entry(0, "history"), await read.NextAsync());
        Assert.Equal(CaughtUp(1), await read.NextAsync());
        Assert.Equal(DraftItem(draft.Id, 0, "a"), await read.NextAsync());
        Assert.Equal(DraftItem(draft.Id, 1, "b"), await read.NextAsync());
        draft.Write("c");
        Assert.Equal(DraftItem(draft.Id, 2, "c"), await read.NextAsync());

        // A committed reader does not observe drafts.
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        await using var committed = log.ReadAsync(1, cancellationToken: cancellation.Token).GetAsyncEnumerator(CancellationToken);
        Assert.Equal(CaughtUp(1), await committed.NextAsync());
        var committedNext = committed.MoveNextAsync();
        Assert.False(committedNext.IsCompleted);
        draft.Abandon();
        Assert.Equal(Abandoned(draft.Id, LogDraftAbandonReason.Abandoned), await read.NextAsync());
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

        await using (var first = log.ReadAsync(0, mode: DurableWatchMode.Staged | DurableWatchMode.Drafts, cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken))
        {
            Assert.Equal(CaughtUp(0), await first.NextAsync());
            Assert.Equal(DraftItem(draft.Id, 0, "a"), await first.NextAsync());
        }

        draft.Write("b");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        await using var reconnected = log.ReadAsync(0, mode: DurableWatchMode.Staged | DurableWatchMode.Drafts, resumeDraftId: draft.Id, cancellationToken: cancellation.Token).GetAsyncEnumerator(CancellationToken);
        Assert.Equal(CaughtUp(0), await reconnected.NextAsync());
        Assert.Equal(DraftItem(draft.Id, 0, "a"), await reconnected.NextAsync());
        Assert.Equal(DraftItem(draft.Id, 1, "b"), await reconnected.NextAsync());
        var next = reconnected.MoveNextAsync();
        Assert.False(next.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await next);
    });

    [Fact]
    public Task Reconnect_ToCommittedDraft_ReportsItsEntries() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var log = CreateLog(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);
        var draft = log.BeginDraft();
        draft.Write("Hel");
        draft.Commit("Hello");
        await sut.Manager.WriteStateAsync(CancellationToken);

        await using var read = log.ReadAsync(0, mode: DurableWatchMode.Staged | DurableWatchMode.Drafts, resumeDraftId: draft.Id, cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        Assert.Equal(Committed(draft.Id, 0, 1), await read.NextAsync());
        Assert.Equal(Entry(0, "Hello"), await read.NextAsync());
    });

    [Fact]
    public Task Reconnect_ToClosedDrafts_ReportsRecordedOutcomes() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var log = CreateLog(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);
        log.Append("question");
        await sut.Manager.WriteStateAsync(CancellationToken);

        var failed = log.BeginDraft();
        failed.Abandon("The model stream failed.");
        var disposed = log.BeginDraft();
        disposed.Dispose();
        var staged = log.BeginDraft();
        staged.Commit("answer", "follow-up");

        Assert.Equal(Abandoned(failed.Id, LogDraftAbandonReason.Abandoned, "The model stream failed."), await ResumeAsync(failed.Id));
        Assert.Equal(Abandoned(disposed.Id, LogDraftAbandonReason.Disposed), await ResumeAsync(disposed.Id));
        Assert.Equal(Committed(staged.Id, 1, 2, DurableChangeStatus.Staged), await ResumeAsync(staged.Id));
        Assert.Equal(Abandoned(new(staged.Id.InstanceId, 99), LogDraftAbandonReason.Unknown), await ResumeAsync(new(staged.Id.InstanceId, 99)));

        // A reader without staged entries learns of the commit once its entries are durable.
        await using (var read = log.ReadAsync(1, mode: DurableWatchMode.Drafts, resumeDraftId: staged.Id, cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken))
        {
            Assert.Equal(CaughtUp(1), await read.NextAsync());
            var next = read.MoveNextAsync();
            Assert.False(next.IsCompleted);
            await sut.Manager.WriteStateAsync(CancellationToken);
            Assert.True(await next);
            Assert.Equal(Committed(staged.Id, 1, 2), read.Current with { Epoch = 0 });
            Assert.Equal(Entry(1, "answer"), await read.NextAsync());
            Assert.Equal(Entry(2, "follow-up"), await read.NextAsync());
        }

        // The log retains the reasons for a bounded number of abandoned drafts. Commits remain with their entries.
        for (var i = 0; i < 16; i++)
        {
            log.BeginDraft().Abandon();
        }

        Assert.Equal(Abandoned(failed.Id, LogDraftAbandonReason.Unknown), await ResumeAsync(failed.Id));
        Assert.Equal(Committed(staged.Id, 1, 2), await ResumeAsync(staged.Id));

        async Task<LogEvent<string>> ResumeAsync(LogDraftId id)
        {
            await using var read = log.ReadAsync(log.NextSequence, mode: DurableWatchMode.Staged | DurableWatchMode.Drafts, resumeDraftId: id, cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
            return await read.NextAsync();
        }
    });

    [Fact]
    public Task Draft_CommitsSeveralEntriesAsOneJournalEntry() => RunOnActivationSchedulerAsync(async () =>
    {
        var storage = new GatedJournalStorage();
        var sut = CreateSystem(storage);
        var log = CreateLog(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);
        await using var read = log.ReadAsync(0, mode: DurableWatchMode.Staged | DurableWatchMode.Drafts, cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        Assert.Equal(CaughtUp(0), await read.NextAsync());

        var draft = log.BeginDraft();
        draft.Write("partial");
        Assert.Equal(DraftItem(draft.Id, 0, "partial"), await read.NextAsync());
        Assert.Equal(0, draft.Commit("tool call", "tool result", "answer"));
        Assert.Equal((3L, 3), (log.NextSequence, log.Count));
        Assert.Equal(Committed(draft.Id, 0, 3, DurableChangeStatus.Staged), await read.NextAsync());
        Assert.Equal(Entry(0, "tool call", DurableChangeStatus.Staged), await read.NextAsync());
        Assert.Equal(Entry(1, "tool result", DurableChangeStatus.Staged), await read.NextAsync());
        Assert.Equal(Entry(2, "answer", DurableChangeStatus.Staged), await read.NextAsync());
        await sut.Manager.WriteStateAsync(CancellationToken);
        Assert.Equal(new LogEvent<string> { Kind = LogEventKind.Watermark, Sequence = 3, Status = DurableChangeStatus.Committed }, await read.NextAsync());

        var recovered = CreateSystem(storage);
        var recoveredLog = CreateLog(recovered.Manager);
        await recovered.Lifecycle.OnStart(CancellationToken);
        Assert.Equal(["tool call", "tool result", "answer"], recoveredLog);
    });

    [Fact]
    public Task Draft_CommitWithoutEntries_ClosesCleanly() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var log = CreateLog(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);
        log.Append("question");
        await sut.Manager.WriteStateAsync(CancellationToken);
        await using var read = log.ReadAsync(1, mode: DurableWatchMode.Committed | DurableWatchMode.Drafts, cancellationToken: CancellationToken)
            .GetAsyncEnumerator(CancellationToken);
        Assert.Equal(CaughtUp(1), await read.NextAsync());

        var draft = log.BeginDraft();
        draft.Write("Thinking");
        Assert.Equal(DraftItem(draft.Id, 0, "Thinking"), await read.NextAsync());
        Assert.Equal(1, draft.Commit());
        Assert.False(draft.IsOpen);
        Assert.Equal((1L, 1L), (log.NextSequence, log.CommittedSequence));

        // The commit record is durable after the next write.
        await sut.Manager.WriteStateAsync(CancellationToken);
        Assert.Equal(Committed(draft.Id, 1, 0), await read.NextAsync());
    });

    [Fact]
    public Task Draft_EncodingFailure_AppendsNothingAndLeavesDraftOpen() => RunOnActivationSchedulerAsync(async () =>
    {
        var storage = new GatedJournalStorage();
        var sut = CreateSystem(storage);
        var log = new DurableLog<string>("log", sut.Manager, new PoisonLogCodec(CreateCodec()));
        await sut.Lifecycle.OnStart(CancellationToken);

        var draft = log.BeginDraft();
        Assert.Throws<InvalidOperationException>(() => draft.Commit("answer", PoisonLogCodec.Poison));
        Assert.True(draft.IsOpen);
        Assert.Equal(0, log.NextSequence);
        Assert.Equal(0, draft.Commit("answer"));
        await sut.Manager.WriteStateAsync(CancellationToken);

        var recovered = CreateSystem(storage);
        var recoveredLog = CreateLog(recovered.Manager);
        await recovered.Lifecycle.OnStart(CancellationToken);
        Assert.Equal(["answer"], recoveredLog);
    });

    [Fact]
    public Task ClosedDraft_ExplainsWhyItIsClosed() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var log = CreateLog(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);

        var committed = log.BeginDraft();
        committed.Commit("a");
        Assert.Contains("committed", Assert.Throws<InvalidOperationException>(() => committed.Commit("b")).Message);

        var reset = log.BeginDraft();
        await sut.Manager.DeleteStateAsync(CancellationToken);
        Assert.False(reset.IsOpen);
        var exception = Assert.Throws<InvalidOperationException>(() => reset.Write("b"));
        Assert.Contains("journal was deleted", exception.Message);
        Assert.Contains(nameof(IDurableLogDraft<string>.IsOpen), exception.Message);

        // Closing a closed draft has no effect.
        reset.Abandon();
        reset.Dispose();
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

        await using var read = log.ReadAsync(committedCursor, mode: DurableWatchMode.Staged | DurableWatchMode.Drafts, resumeDraftId: draft.Id, cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        Assert.Equal(Abandoned(draft.Id, LogDraftAbandonReason.PreviousActivation), await read.NextAsync());
        Assert.Equal(CaughtUp(1), await read.NextAsync());

        // The recovered activation regenerates the response from the last stored entry.
        using var retry = log.BeginDraft();
        Assert.NotEqual(draft.Id.InstanceId, retry.Id.InstanceId);
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
        await using var read = log.ReadAsync(0, mode: DurableWatchMode.Staged | DurableWatchMode.Drafts, cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        Assert.Equal(CaughtUp(0), await read.NextAsync());
        var draft = log.BeginDraft();
        draft.Write("a");
        Assert.Equal(DraftItem(draft.Id, 0, "a"), await read.NextAsync());

        await sut.Manager.DeleteStateAsync(CancellationToken);
        Assert.False(draft.IsOpen);
        Assert.Equal(Abandoned(draft.Id, LogDraftAbandonReason.JournalDeleted), await read.NextAsync());

        // The log had no history, so the reader observed no entries which the deletion replaced.
        Assert.Equal(0, read.Current.Epoch);
        log.Append("b");
        Assert.Equal(Entry(0, "b", DurableChangeStatus.Staged), await read.NextAsync());
        Assert.Equal(log.Epoch, read.Current.Epoch);
    });

    [Theory]
    [InlineData(DurableWatchMode.Drafts)]
    [InlineData(DurableWatchMode.Staged | DurableWatchMode.Drafts)]
    public Task DeletedLog_ReportsFollowedDraftCommitAsAbandonedBeforeReplacementHistory(DurableWatchMode mode) => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var log = CreateLog(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);
        await using var read = log.ReadAsync(mode: mode, cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        Assert.Equal(CaughtUp(0), await read.NextAsync());
        var draft = log.BeginDraft();
        draft.Write("discarded draft");
        Assert.Equal(DraftItem(draft.Id, 0, "discarded draft"), await read.NextAsync());
        draft.Commit("discarded entry");

        await sut.Manager.DeleteStateAsync(CancellationToken);
        log.Append("replacement");
        await sut.Manager.WriteStateAsync(CancellationToken);

        // The deletion discarded the draft's committed entry. The reader had observed no entries, so it continues from zero.
        Assert.Equal(Abandoned(draft.Id, LogDraftAbandonReason.JournalDeleted), await read.NextAsync());
        Assert.Equal(Entry(0, "replacement"), await read.NextAsync());
    });

    [Fact]
    public Task ReadAsync_ValidatesArguments() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var log = CreateLog(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);
        Assert.Throws<ArgumentOutOfRangeException>(() => log.ReadAsync(-1, cancellationToken: CancellationToken));
        Assert.Throws<ArgumentOutOfRangeException>(() => log.ReadAsync(0, epoch: -1, cancellationToken: CancellationToken));
        Assert.Throws<ArgumentOutOfRangeException>(() => log.ReadAsync(0, mode: (DurableWatchMode)9, cancellationToken: CancellationToken));
    });

    [Fact]
    public Task Read_ResumedAfterDeletionInPreviousActivation_ReportsReset() => RunOnActivationSchedulerAsync(async () =>
    {
        var storage = new GatedJournalStorage();
        var first = CreateSystem(storage);
        var log = CreateLog(first.Manager);
        await first.Lifecycle.OnStart(CancellationToken);
        Assert.Equal(0, log.Epoch);
        for (var i = 0; i < 10; i++)
        {
            log.Append($"old{i}");
        }

        await first.Manager.WriteStateAsync(CancellationToken);
        var oldEpoch = log.Epoch;
        Assert.NotEqual(0, oldEpoch);

        // A client which read entries 0 through 9 disconnects. The grain deletes its journal and writes a longer history.
        await first.Manager.DeleteStateAsync(CancellationToken);
        Assert.Equal(0, log.Epoch);
        for (var i = 0; i < 15; i++)
        {
            log.Append($"new{i}");
        }

        await first.Manager.WriteStateAsync(CancellationToken);
        var newEpoch = log.Epoch;
        Assert.NotEqual(oldEpoch, newEpoch);

        var second = CreateSystem(storage);
        var recovered = CreateLog(second.Manager);
        await second.Lifecycle.OnStart(CancellationToken);
        Assert.Equal(newEpoch, recovered.Epoch);

        // The client's position belongs to the deleted history, so the stream restarts the new history.
        await using (var resumed = recovered.ReadAsync(10, oldEpoch, cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken))
        {
            Assert.Equal(Reset(0), await resumed.NextAsync());
            Assert.Equal(newEpoch, resumed.Current.Epoch);
            Assert.Equal(Entry(0, "new0"), await resumed.NextAsync());
        }

        // A position in the current history continues it.
        await using (var resumed = recovered.ReadAsync(10, newEpoch, cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken))
        {
            Assert.Equal(Entry(10, "new10"), await resumed.NextAsync());
            Assert.Equal(newEpoch, resumed.Current.Epoch);
        }
    });

    [Fact]
    public Task Epoch_PersistsThroughClearAndCompaction() => RunOnActivationSchedulerAsync(async () =>
    {
        var storage = new GatedJournalStorage();
        var first = CreateSystem(storage);
        var log = CreateLog(first.Manager);
        await first.Lifecycle.OnStart(CancellationToken);
        log.Append("a");
        var epoch = log.Epoch;
        log.Clear();
        Assert.Equal(epoch, log.Epoch);
        log.Append("b");
        storage.IsCompactionRequested = true;
        await first.Manager.WriteStateAsync(CancellationToken);
        Assert.Equal(1, storage.ReplaceCount);

        var second = CreateSystem(storage);
        var recovered = CreateLog(second.Manager);
        await second.Lifecycle.OnStart(CancellationToken);
        Assert.Equal((epoch, 2L, 3L), (recovered.Epoch, recovered.FirstSequence, recovered.NextSequence));
        await using var read = recovered.ReadAsync(0, epoch, cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        Assert.Equal(Reset(2), await read.NextAsync());
        Assert.Equal(Entry(2, "b"), await read.NextAsync());
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Draft_CommittedOutcome_SurvivesReactivation(bool compact) => RunOnActivationSchedulerAsync(async () =>
    {
        var storage = new GatedJournalStorage();
        var first = CreateSystem(storage);
        var log = CreateLog(first.Manager);
        await first.Lifecycle.OnStart(CancellationToken);
        log.Append("question");
        var committed = log.BeginDraft();
        committed.Write("ans");
        committed.Commit("answer");
        var empty = log.BeginDraft();
        empty.Commit();
        storage.IsCompactionRequested = compact;
        await first.Manager.WriteStateAsync(CancellationToken);
        var unwritten = log.BeginDraft();
        unwritten.Commit("lost");

        var second = CreateSystem(storage);
        var recovered = CreateLog(second.Manager);
        await second.Lifecycle.OnStart(CancellationToken);

        // The log records commits with their entries, so a new activation reports them. The unwritten commit was lost.
        Assert.Equal(Committed(committed.Id, 1, 1), await ResumeAsync(recovered, committed.Id));
        Assert.Equal(Committed(empty.Id, 2, 0), await ResumeAsync(recovered, empty.Id));
        Assert.Equal(Abandoned(unwritten.Id, LogDraftAbandonReason.PreviousActivation), await ResumeAsync(recovered, unwritten.Id));

        // A commit is retained while its first entry is.
        recovered.TrimBefore(2);
        Assert.Equal(Abandoned(committed.Id, LogDraftAbandonReason.PreviousActivation), await ResumeAsync(recovered, committed.Id));
        Assert.Equal(Committed(empty.Id, 2, 0, DurableChangeStatus.Committed), await ResumeAsync(recovered, empty.Id));

        async Task<LogEvent<string>> ResumeAsync(DurableLog<string> target, LogDraftId id)
        {
            await using var read = target.ReadAsync(target.NextSequence, mode: DurableWatchMode.Staged | DurableWatchMode.Drafts, resumeDraftId: id, cancellationToken: CancellationToken)
                .GetAsyncEnumerator(CancellationToken);
            return await read.NextAsync();
        }
    });

    [Fact]
    public Task DraftCommit_OnEmptyLog_ReportsTheEpochItBegins() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var log = CreateLog(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);
        await using var read = log.ReadAsync(mode: DurableWatchMode.Staged | DurableWatchMode.Drafts, cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        Assert.Equal(CaughtUp(0), await read.NextAsync());
        Assert.Equal(0, read.Current.Epoch);
        var draft = log.BeginDraft();
        draft.Write("a");
        Assert.Equal(DraftItem(draft.Id, 0, "a"), await read.NextAsync());

        // The commit begins the log's history, so its range is reported in that history.
        draft.Commit("answer");
        Assert.NotEqual(0, log.Epoch);
        Assert.Equal(Committed(draft.Id, 0, 1, DurableChangeStatus.Staged), await read.NextAsync());
        Assert.Equal(log.Epoch, read.Current.Epoch);
        Assert.Equal(Entry(0, "answer", DurableChangeStatus.Staged), await read.NextAsync());
        Assert.Equal(log.Epoch, read.Current.Epoch);
    });

    [Fact]
    public Task StagedRead_DeliversEligibleWatermarkBeforeLaterEntries() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var log = CreateLog(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);
        await using var read = log.ReadAsync(mode: DurableWatchMode.Staged, cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        Assert.Equal(CaughtUp(0), await read.NextAsync());

        // The producer commits the delivered entry and appends another before every read. The watermark still advances.
        log.Append("e0");
        Assert.Equal(Entry(0, "e0", DurableChangeStatus.Staged), await read.NextAsync());
        for (var i = 1; i <= 5; i++)
        {
            await sut.Manager.WriteStateAsync(CancellationToken);
            log.Append($"e{i}");
            Assert.Equal(Watermark(i), await read.NextAsync());
            Assert.Equal(Entry(i, $"e{i}", DurableChangeStatus.Staged), await read.NextAsync());
        }
    });

    [Fact]
    public Task StagedReset_WithLaterTrim_ConfirmsTheTrimOnceDurable() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var log = CreateLog(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);
        log.Append("a");
        log.Append("b");
        await sut.Manager.WriteStateAsync(CancellationToken);

        // A held write captures a clear and the replacement entries 3 and 4. A trim staged during the write discards entry 3.
        log.Clear();
        log.Append("c");
        log.Append("d");
        sut.Storage.HoldNextWrite();
        var write = sut.Manager.WriteStateAsync(CancellationToken).AsTask();
        await sut.Storage.WriteStarted.Task;
        log.TrimBefore(4);

        await using var read = log.ReadAsync(0, log.Epoch, DurableWatchMode.Staged, cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        Assert.Equal(Reset(4, DurableChangeStatus.Staged), await read.NextAsync());
        Assert.Equal(CaughtUp(4), await read.NextAsync());
        Assert.Equal(Entry(4, "d", DurableChangeStatus.Staged), await read.NextAsync());

        sut.Storage.Release();
        await write;
        Assert.Equal(Watermark(5), await read.NextAsync());
        var next = read.MoveNextAsync();
        Assert.False(next.IsCompleted);

        // The trim's write confirms the retention boundary which the staged reset reported.
        await sut.Manager.WriteStateAsync(CancellationToken);
        Assert.True(await next);
        Assert.Equal(Trimmed(4), read.Current with { Epoch = 0 });
    });

    [Fact]
    public Task EmptyDraftCommit_IsReportedAsCommittedAfterItsRecordIsDurable() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var log = CreateLog(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);
        log.Append("question");
        await sut.Manager.WriteStateAsync(CancellationToken);
        await using var committed = log.ReadAsync(1, mode: DurableWatchMode.Committed | DurableWatchMode.Drafts, cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        await using var staged = log.ReadAsync(1, mode: DurableWatchMode.Staged | DurableWatchMode.Drafts, cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        Assert.Equal(CaughtUp(1), await committed.NextAsync());
        Assert.Equal(CaughtUp(1), await staged.NextAsync());
        var draft = log.BeginDraft();
        draft.Write("partial");
        Assert.Equal(DraftItem(draft.Id, 0, "partial"), await committed.NextAsync());
        Assert.Equal(DraftItem(draft.Id, 0, "partial"), await staged.NextAsync());

        // The commit adds no entries at the durable tail, so only its record shows whether it is durable.
        draft.Commit();
        Assert.Equal(Committed(draft.Id, 1, 0, DurableChangeStatus.Staged), await staged.NextAsync());
        var next = committed.MoveNextAsync();
        Assert.False(next.IsCompleted);
        await using var resumed = log.ReadAsync(1, mode: DurableWatchMode.Committed | DurableWatchMode.Drafts, resumeDraftId: draft.Id, cancellationToken: CancellationToken)
            .GetAsyncEnumerator(CancellationToken);
        Assert.Equal(CaughtUp(1), await resumed.NextAsync());
        var resumedNext = resumed.MoveNextAsync();
        Assert.False(resumedNext.IsCompleted);

        await sut.Manager.WriteStateAsync(CancellationToken);
        Assert.True(await next);
        Assert.Equal(Committed(draft.Id, 1, 0), committed.Current with { Epoch = 0 });
        Assert.True(await resumedNext);
        Assert.Equal(Committed(draft.Id, 1, 0), resumed.Current with { Epoch = 0 });
    });

    [Fact]
    public Task EmptyDraftCommit_StagedDuringAnotherWrite_AwaitsTheWriteWhichCapturesIt() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var log = CreateLog(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);
        await using var read = log.ReadAsync(mode: DurableWatchMode.Committed | DurableWatchMode.Drafts, cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        Assert.Equal(CaughtUp(0), await read.NextAsync());
        log.Append("a");
        var draft = log.BeginDraft();
        draft.Write("partial");
        Assert.Equal(DraftItem(draft.Id, 0, "partial"), await read.NextAsync());
        sut.Storage.HoldNextWrite();
        var write = sut.Manager.WriteStateAsync(CancellationToken).AsTask();
        await sut.Storage.WriteStarted.Task;

        // The held write captured entry 0. The commit's empty range ends where that write's entries end.
        draft.Commit();
        sut.Storage.Release();
        await write;
        Assert.Equal(Entry(0, "a"), await read.NextAsync());
        var next = read.MoveNextAsync();
        Assert.False(next.IsCompleted);

        await sut.Manager.WriteStateAsync(CancellationToken);
        Assert.True(await next);
        Assert.Equal(Committed(draft.Id, 1, 0), read.Current with { Epoch = 0 });
    });

    [Fact]
    public Task EmptyDraftCommit_WhoseWriteFails_IsNotReportedAsCommitted() => RunOnActivationSchedulerAsync(async () =>
    {
        var sut = CreateSystem();
        var log = CreateLog(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);
        log.Append("question");
        await sut.Manager.WriteStateAsync(CancellationToken);
        await using var read = log.ReadAsync(1, mode: DurableWatchMode.Committed | DurableWatchMode.Drafts, cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        Assert.Equal(CaughtUp(1), await read.NextAsync());
        var draft = log.BeginDraft();
        draft.Write("partial");
        Assert.Equal(DraftItem(draft.Id, 0, "partial"), await read.NextAsync());
        draft.Commit();

        sut.Storage.Failure = new IOException("The write failed.");
        await Assert.ThrowsAsync<IOException>(async () => await sut.Manager.WriteStateAsync(CancellationToken));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await read.MoveNextAsync());
        Assert.IsType<IOException>(error.InnerException);
    });

    [Fact]
    public void Replay_RejectsCommandsWhichViolateLogInvariants()
    {
        var codec = CreateCodec();
        var draftId = new LogDraftId(Guid.NewGuid(), 1);
        Assert.Throws<InvalidOperationException>(() => Replay(writer => codec.WriteAppend(["a"], writer)));
        Assert.Throws<InvalidOperationException>(() => Replay(writer => codec.WriteClear(writer)));
        Assert.Throws<InvalidOperationException>(() => Replay(writer => codec.WriteSeal(writer)));
        Assert.Throws<InvalidOperationException>(() => Replay(writer => codec.WriteEpoch(1, writer), writer => codec.WriteEpoch(2, writer)));
        Assert.Throws<InvalidOperationException>(() => Replay(writer => codec.WriteEpoch(1, writer), writer => codec.WriteSeal(writer), writer => codec.WriteAppend(["a"], writer)));
        Assert.Throws<InvalidOperationException>(() => Replay(writer => codec.WriteEpoch(1, writer), writer => codec.WriteSeal(writer), writer => codec.WriteClear(writer)));
        Assert.Throws<InvalidOperationException>(() => Replay(writer => codec.WriteSnapshot(0, 1, 0, isSealed: false, [], [], writer)));
        Assert.Throws<InvalidOperationException>(() => Replay(writer => codec.WriteSnapshot(1, 0, 0, isSealed: false, ["a"], [new(draftId, 0, 2)], writer)));
        Assert.Throws<InvalidOperationException>(
            () => Replay(writer => codec.WriteSnapshot(1, 0, 0, isSealed: false, ["a", "b"], [new(draftId, 1, 1), new(draftId, 0, 1)], writer)));

        var log = Replay(writer => codec.WriteSnapshot(1, 0, 0, isSealed: true, ["a"], [new(draftId, 0, 1)], writer));
        Assert.Equal((1L, true, 1), (log.Epoch, log.IsSealed, log.Count));

        DurableLog<string> Replay(params Action<JournalStreamWriter>[] commands)
        {
            var sut = CreateSystem();
            var target = CreateLog(sut.Manager);
            foreach (var command in commands)
            {
                codec.Apply(CodecTestHelpers.ReadBuffer(CodecTestHelpers.WriteEntry(command)), target);
            }

            return target;
        }
    }

    [Fact]
    public async Task RecoveredLog_MatchesLiveLog_ForOperationSequences()
    {
        var operations = Gen.Select(Gen.Int[0, 7], Gen.Int[0, 9]).Array[1, 40];
        await operations.SampleAsync(sequence => RunOnActivationSchedulerAsync(() => RunRecoveryModelAsync(sequence)), iter: 50);
    }

    // After each write, a new activation recovers the same log, its readers see the same entries, and it reports the
    // commits of retained drafts.
    private async Task RunRecoveryModelAsync((int Operation, int Argument)[] operations)
    {
        var storage = new GatedJournalStorage();
        var sut = CreateSystem(storage);
        var log = CreateLog(sut.Manager);
        await sut.Lifecycle.OnStart(CancellationToken);
        var commits = new List<(LogDraftId Id, long Sequence, int Count)>();
        foreach (var (operation, argument) in operations)
        {
            switch (operation)
            {
                case 0:
                case 1:
                    log.Append($"e{argument}");
                    break;
                case 2:
                    log.TrimBefore(Math.Min(log.NextSequence, log.FirstSequence + argument));
                    commits.RemoveAll(commit => commit.Sequence < log.FirstSequence);
                    break;
                case 3:
                    log.Clear();
                    commits.Clear();
                    break;
                case 4:
                    var draft = log.BeginDraft();
                    draft.Write("draft");
                    var entries = Enumerable.Range(0, argument % 3).Select(i => $"d{argument}.{i}").ToArray();
                    commits.Add((draft.Id, draft.Commit(entries), entries.Length));
                    break;
                case 5:
                case 6:
                    storage.IsCompactionRequested = operation == 6;
                    await sut.Manager.WriteStateAsync(CancellationToken);
                    await AssertRecoveredAsync();
                    break;
                default:
                    await sut.Manager.DeleteStateAsync(CancellationToken);
                    Assert.Equal((0L, 0L), (log.Epoch, log.NextSequence));
                    commits.Clear();
                    break;
            }
        }

        await sut.Manager.WriteStateAsync(CancellationToken);
        await AssertRecoveredAsync();

        async Task AssertRecoveredAsync()
        {
            Assert.Equal(log.NextSequence, log.CommittedSequence);
            var recoveredSystem = CreateSystem(storage);
            var recovered = CreateLog(recoveredSystem.Manager);
            await recoveredSystem.Lifecycle.OnStart(CancellationToken);
            Assert.Equal(
                (log.Epoch, log.FirstSequence, log.NextSequence, log.CommittedSequence, log.IsSealed),
                (recovered.Epoch, recovered.FirstSequence, recovered.NextSequence, recovered.CommittedSequence, recovered.IsSealed));
            Assert.Equal(log, recovered);

            // A reader of either log from the start of the history receives the retained entries with their sequence numbers.
            Assert.Equal(await ReadCommittedAsync(log), await ReadCommittedAsync(recovered));
            Assert.Equal(Enumerable.Range(0, log.Count).Select(i => (log.FirstSequence + i, (string?)log[log.FirstSequence + i])), await ReadCommittedAsync(recovered));

            foreach (var (id, sequence, count) in commits)
            {
                await using var read = recovered.ReadAsync(recovered.NextSequence, recovered.Epoch, DurableWatchMode.Drafts, id, CancellationToken)
                    .GetAsyncEnumerator(CancellationToken);
                Assert.Equal(Committed(id, sequence, count), await read.NextAsync());
            }
        }

        async Task<List<(long Sequence, string? Item)>> ReadCommittedAsync(DurableLog<string> target)
        {
            var entries = new List<(long Sequence, string? Item)>();
            await using var read = target.ReadAsync(0, target.Epoch, cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
            while ((await read.NextAsync()).Kind is not LogEventKind.CaughtUp)
            {
                Assert.Equal(target.Epoch, read.Current.Epoch);
                switch (read.Current.Kind)
                {
                    case LogEventKind.Entry:
                        entries.Add((read.Current.Sequence, read.Current.Item));
                        break;
                    case LogEventKind.Reset:
                    case LogEventKind.Trimmed:
                        Assert.Empty(entries);
                        Assert.Equal(target.FirstSequence, read.Current.Sequence);
                        break;
                    default:
                        Assert.Fail($"Unexpected event {read.Current}.");
                        break;
                }
            }

            return entries;
        }
    }

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

        var draftId = new LogDraftId(Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"), 3);
        codec.Apply(CodecTestHelpers.ReadBuffer(CodecTestHelpers.WriteEntry(writer => codec.WriteEpoch(long.MaxValue, writer))), handler);
        codec.Apply(CodecTestHelpers.ReadBuffer(CodecTestHelpers.WriteEntry(writer => codec.WriteAppend(["a"], writer))), handler);
        codec.Apply(CodecTestHelpers.ReadBuffer(CodecTestHelpers.WriteEntry(writer => codec.WriteAppend(["b", "c"], writer))), handler);
        codec.Apply(CodecTestHelpers.ReadBuffer(CodecTestHelpers.WriteEntry(writer => codec.WriteDraftCommit(draftId, 3, ["f"], writer))), handler);
        codec.Apply(CodecTestHelpers.ReadBuffer(CodecTestHelpers.WriteEntry(writer => codec.WriteDraftCommit(draftId, 4, [], writer))), handler);
        codec.Apply(CodecTestHelpers.ReadBuffer(CodecTestHelpers.WriteEntry(writer => codec.WriteTrimBefore(long.MaxValue, writer))), handler);
        codec.Apply(CodecTestHelpers.ReadBuffer(CodecTestHelpers.WriteEntry(writer => codec.WriteClear(writer))), handler);
        codec.Apply(CodecTestHelpers.ReadBuffer(CodecTestHelpers.WriteEntry(writer => codec.WriteSeal(writer))), handler);
        codec.Apply(
            CodecTestHelpers.ReadBuffer(CodecTestHelpers.WriteEntry(writer => codec.WriteSnapshot(9, 7, 5, isSealed: true, ["d", "e"], [new(draftId, 8, 1)], writer))),
            handler);

        Assert.Equal(
            [
                $"epoch {long.MaxValue}", "append a", "append b", "append c", "append f", $"commit {draftId} 3 1", $"commit {draftId} 4 0",
                $"trim {long.MaxValue}", "clear", "seal", "reset 9 7 5 2", "append d", "append e", $"commit {draftId} 8 1", "seal",
            ],
            handler.Commands);
        Assert.Throws<ArgumentException>(() => CodecTestHelpers.WriteEntry(writer => codec.WriteAppend([], writer)));
        Assert.Throws<ArgumentOutOfRangeException>(() => CodecTestHelpers.WriteEntry(writer => codec.WriteEpoch(0, writer)));
    }

    private static LogEvent<string> Entry(long sequence, string item, DurableChangeStatus status = DurableChangeStatus.Committed) =>
        new() { Kind = LogEventKind.Entry, Sequence = sequence, Item = item, Status = status };

    private static LogEvent<string> Reset(long sequence, DurableChangeStatus status = DurableChangeStatus.Committed) =>
        new() { Kind = LogEventKind.Reset, Sequence = sequence, Status = status };

    private static LogEvent<string> Trimmed(long sequence, DurableChangeStatus status = DurableChangeStatus.Committed) =>
        new() { Kind = LogEventKind.Trimmed, Sequence = sequence, Status = status };

    private static LogEvent<string> Watermark(long sequence) =>
        new() { Kind = LogEventKind.Watermark, Sequence = sequence, Status = DurableChangeStatus.Committed };

    private static LogEvent<string> CaughtUp(long sequence) =>
        new() { Kind = LogEventKind.CaughtUp, Sequence = sequence, Status = DurableChangeStatus.Committed };

    private static LogEvent<string> Sealed(long sequence) =>
        new() { Kind = LogEventKind.Sealed, Sequence = sequence, Status = DurableChangeStatus.Committed };

    private static LogEvent<string> DraftItem(LogDraftId id, long position, string item) =>
        new() { Kind = LogEventKind.DraftItem, Sequence = position, Item = item, DraftId = id, Status = DurableChangeStatus.Draft };

    private static LogEvent<string> Committed(LogDraftId id, long sequence, int count, DurableChangeStatus status = DurableChangeStatus.Committed) =>
        new() { Kind = LogEventKind.DraftCommitted, Sequence = sequence, Count = count, DraftId = id, Status = status };

    private static LogEvent<string> Abandoned(LogDraftId id, LogDraftAbandonReason reason, string? message = null) =>
        new() { Kind = LogEventKind.DraftAbandoned, DraftId = id, AbandonReason = reason, Message = message, Status = DurableChangeStatus.Draft };

    private (IJournaledStateManager Manager, GatedJournalStorage Storage, ILifecycleSubject Lifecycle) CreateSystem(GatedJournalStorage? storage = null)
    {
        storage ??= new GatedJournalStorage();
        storage.Inner.SetConfiguredJournalFormatKey(OrleansBinaryJournalFormat.JournalFormatKey);
        var sut = CreateTestSystem(storage);
        return (sut.Manager, storage, sut.Lifecycle);
    }

    private DurableLog<string> CreateLog(IJournaledStateManager manager, string name = "log") => new(name, manager, CreateCodec());

    private OrleansBinaryDurableLogCommandCodec<string> CreateCodec() => new(CodecProvider.GetCodec<string>(), SessionPool);

    /// <summary>
    /// Fails to encode <see cref="Poison"/> after writing the items before it, as an encoder failing mid-command would.
    /// </summary>
    private sealed class PoisonLogCodec(IDurableLogCommandCodec<string> inner) : IDurableLogCommandCodec<string>
    {
        public const string Poison = "poison";

        public void WriteAppend(ReadOnlySpan<string> items, JournalStreamWriter writer)
        {
            if (!items.Contains(Poison))
            {
                inner.WriteAppend(items, writer);
                return;
            }

            using var entry = writer.BeginEntry();
            foreach (var item in items)
            {
                if (item == Poison)
                {
                    throw new InvalidOperationException("The item cannot be encoded.");
                }

                System.Buffers.BuffersExtensions.Write(entry.Writer, "x"u8);
            }
        }

        public void WriteEpoch(long epoch, JournalStreamWriter writer) => inner.WriteEpoch(epoch, writer);

        public void WriteDraftCommit(LogDraftId draftId, long sequence, ReadOnlySpan<string> items, JournalStreamWriter writer)
        {
            if (items.Contains(Poison))
            {
                WritePoisonedEntry(items, writer);
            }

            inner.WriteDraftCommit(draftId, sequence, items, writer);
        }

        public void WriteTrimBefore(long sequence, JournalStreamWriter writer) => inner.WriteTrimBefore(sequence, writer);

        public void WriteClear(JournalStreamWriter writer) => inner.WriteClear(writer);

        public void WriteSeal(JournalStreamWriter writer) => inner.WriteSeal(writer);

        public void WriteSnapshot(
            long epoch,
            long firstSequence,
            long resetSequence,
            bool isSealed,
            IReadOnlyCollection<string> items,
            IReadOnlyCollection<LogDraftCommit> draftCommits,
            JournalStreamWriter writer) =>
            inner.WriteSnapshot(epoch, firstSequence, resetSequence, isSealed, items, draftCommits, writer);

        public void Apply(JournalBufferReader input, IDurableLogCommandHandler<string> consumer) => inner.Apply(input, consumer);

        private static void WritePoisonedEntry(ReadOnlySpan<string> items, JournalStreamWriter writer)
        {
            using var entry = writer.BeginEntry();
            foreach (var item in items)
            {
                if (item == Poison)
                {
                    throw new InvalidOperationException("The item cannot be encoded.");
                }

                System.Buffers.BuffersExtensions.Write(entry.Writer, "x"u8);
            }
        }
    }

    internal sealed class RecordingLogHandler : IDurableLogCommandHandler<string>
    {
        public List<string> Commands { get; } = [];

        public void ApplyEpoch(long epoch) => Commands.Add($"epoch {epoch}");

        public void ApplyAppend(string item) => Commands.Add($"append {item}");

        public void ApplyDraftCommit(LogDraftCommit commit) => Commands.Add($"commit {commit.DraftId} {commit.Sequence} {commit.Count}");

        public void ApplyTrimBefore(long sequence) => Commands.Add($"trim {sequence}");

        public void ApplyClear() => Commands.Add("clear");

        public void ApplySeal() => Commands.Add("seal");

        public void Reset(long epoch, long firstSequence, long resetSequence, int capacityHint) =>
            Commands.Add($"reset {epoch} {firstSequence} {resetSequence} {capacityHint}");
    }
}
