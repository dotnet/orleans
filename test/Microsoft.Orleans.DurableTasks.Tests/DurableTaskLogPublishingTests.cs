using System.Threading.Channels;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Orleans.Journaling;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.Tests;

/// <summary>
/// Verifies how a durable task publishes its progress to a journaled log: an entry appended in a recorded effect, and a
/// draft which a step streams to readers and commits in its final synchronous code.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class DurableTaskLogPublishingTests
{
    private static readonly TaskId WorkflowId = TaskId.CreateRoot("workflow");
    private static readonly TaskId StepId = WorkflowId.Child("respond");
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    [Fact]
    public Task AppendInOnceCommitsTheEntryWithItsRecordAndReplayReturnsTheRecordedSequence() => ActivationScheduler.RunAsync(async () =>
    {
        var effectId = WorkflowId.Child("$child-1");
        await using var harness = new DurableTaskReplayHarness(static grain => Workflow(grain));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await activation.Grain.AtGate.WaitAsync(WaitTimeout, TestCancellation);
            Assert.Equal(0, activation.Grain.Log.CommittedSequence);

            // Unrelated grain work writes the journal while the workflow waits; the write gathers the entry together with
            // the effect's record.
            activation.Grain.RequestUnrelatedWrite();
            await activation.Grain.UnrelatedWrites;

            Assert.Equal(1, activation.Grain.Log.CommittedSequence);
            var entryWrites = activation.Writes.Where(static write => write.Entries == 1).ToArray();
            Assert.NotEmpty(entryWrites);
            Assert.All(entryWrites, write => Assert.Contains(effectId, write.Completed));
            Assert.Equal(0L, GetTask(activation, effectId).Result!.GetResult<long>());
        }

        // The replay returns the recorded sequence without appending again.
        await using var replay = await harness.ActivateAsync(TestCancellation);
        await replay.Grain.AtGate.WaitAsync(WaitTimeout, TestCancellation);
        replay.Grain.OpenGate();

        Assert.Equal(0L, (await replay.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<long>());
        Assert.Equal([10], replay.Grain.Log.ToArray());

        static async DurableTask<long> Workflow(ReplayGrain grain)
        {
            var sequence = DurableTask.Once(() => grain.Log.Append(10));
            await grain.WaitAtGateAsync();
            return sequence;
        }
    });

    [Fact]
    public Task StepStreamsItsDraftLiveAndCommitsTheEntryInTheWriteWhichRecordsItsResult() => ActivationScheduler.RunAsync(async () =>
    {
        var chunks = Channel.CreateUnbounded<int>();
        await using var harness = new DurableTaskReplayHarness(grain => RespondAsync(grain, chunks.Reader));
        await using var activation = await harness.ActivateAsync(TestCancellation);
        await using var drafts = ReadLog(activation, DurableWatchMode.Drafts);
        await using var committed = ReadLog(activation, DurableWatchMode.Committed);
        Assert.Equal(LogEventKind.CaughtUp, (await NextAsync(drafts)).Kind);
        Assert.Equal(LogEventKind.CaughtUp, (await NextAsync(committed)).Kind);

        await activation.ScheduleAsync(WorkflowId, TestCancellation);
        chunks.Writer.TryWrite(1);
        var first = await NextAsync(drafts);
        Assert.Equal((LogEventKind.DraftItem, 0L, 1, DurableChangeStatus.Draft), (first.Kind, first.Sequence, first.Item, first.Status));

        // Other grain work writes the journal while the step streams. The draft's items are volatile, so the write
        // commits none of them, and the committed reader receives nothing.
        activation.Grain.RequestUnrelatedWrite();
        await activation.Grain.UnrelatedWrites;
        Assert.Equal(0, activation.Writes.Last().Entries);
        var nextCommitted = committed.MoveNextAsync();
        Assert.False(nextCommitted.IsCompleted);

        chunks.Writer.TryWrite(2);
        var second = await NextAsync(drafts);
        Assert.Equal((LogEventKind.DraftItem, 1L, 2, first.DraftId), (second.Kind, second.Sequence, second.Item, second.DraftId));
        Assert.False(nextCommitted.IsCompleted);

        // The stream ends, so the step's final synchronous code commits the draft's entry and returns its sequence. The
        // committed reader receives the entry once the write which records the step's result is durable.
        chunks.Writer.Complete();
        Assert.True(await nextCommitted.AsTask().WaitAsync(WaitTimeout, TestCancellation));
        Assert.Equal((LogEventKind.Entry, 0L, 3), (committed.Current.Kind, committed.Current.Sequence, committed.Current.Item));
        var entryWrites = activation.Writes.Where(static write => write.Entries == 1).ToArray();
        Assert.NotEmpty(entryWrites);
        Assert.All(entryWrites, static write => Assert.Contains(StepId, write.Completed));
        Assert.Equal(0L, GetTask(activation, StepId).Result!.GetResult<long>());

        var outcome = await NextAsync(drafts);
        Assert.Equal((LogEventKind.DraftCommitted, 0L, 1, first.DraftId), (outcome.Kind, outcome.Sequence, outcome.Count, outcome.DraftId));
        var entry = await NextAsync(drafts);
        Assert.Equal((LogEventKind.Entry, 0L, 3), (entry.Kind, entry.Sequence, entry.Item));

        activation.Grain.OpenGate();
        Assert.Equal("committed at 0", (await activation.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<string>());
        Assert.Equal(1, harness.EffectRuns);
    });

    [Fact]
    public Task ReplayAfterTheStepsWriteReturnsTheRecordedSequenceWithoutStreamingAgain() => ActivationScheduler.RunAsync(async () =>
    {
        var chunks = Channel.CreateUnbounded<int>();
        await using var harness = new DurableTaskReplayHarness(grain => RespondAsync(grain, chunks.Reader));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            Stream(chunks, 1, 2);

            // The workflow resumes from the step once the write which records the step's result commits.
            await activation.Grain.AtGate.WaitAsync(WaitTimeout, TestCancellation);
            Assert.Equal(1, activation.Grain.Log.CommittedSequence);
        }

        // The replay's stream never yields, so a step which ran again wouldn't return.
        chunks = Channel.CreateUnbounded<int>();
        await using var replay = await harness.ActivateAsync(TestCancellation);
        await replay.Grain.AtGate.WaitAsync(WaitTimeout, TestCancellation);
        Assert.Equal(1, harness.EffectRuns);
        Assert.Equal([3], replay.Grain.Log.ToArray());

        using var reading = CancellationTokenSource.CreateLinkedTokenSource(TestCancellation);
        await using var drafts = ReadLog(replay, DurableWatchMode.Drafts, reading.Token);
        var entry = await NextAsync(drafts);
        Assert.Equal((LogEventKind.Entry, 0L, 3), (entry.Kind, entry.Sequence, entry.Item));
        Assert.Equal(LogEventKind.CaughtUp, (await NextAsync(drafts)).Kind);
        var next = drafts.MoveNextAsync();

        replay.Grain.OpenGate();
        Assert.Equal("committed at 0", (await replay.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<string>());

        // No draft opened, so the reader has nothing to deliver.
        Assert.False(next.IsCompleted);
        reading.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next.AsTask());
        Assert.Equal([3], replay.Grain.Log.ToArray());
    });

    [Fact]
    public Task StepWhichEndsMidStreamRunsAgainAndCommitsOnce() => ActivationScheduler.RunAsync(async () =>
    {
        var chunks = Channel.CreateUnbounded<int>();
        await using var harness = new DurableTaskReplayHarness(grain => RespondAsync(grain, chunks.Reader));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await using var drafts = ReadLog(activation, DurableWatchMode.Drafts);
            Assert.Equal(LogEventKind.CaughtUp, (await NextAsync(drafts)).Kind);
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            chunks.Writer.TryWrite(1);
            Assert.Equal(LogEventKind.DraftItem, (await NextAsync(drafts)).Kind);
        }

        // The activation ended while the step streamed, so nothing recorded the step, and the replay runs it again.
        chunks = Channel.CreateUnbounded<int>();
        await using var replay = await harness.ActivateAsync(TestCancellation);
        Assert.Empty(replay.Grain.Log);
        Stream(chunks, 1, 2);
        await replay.Grain.AtGate.WaitAsync(WaitTimeout, TestCancellation);

        Assert.Equal(2, harness.EffectRuns);
        Assert.Equal([3], replay.Grain.Log.ToArray());
        Assert.Equal(1, replay.Grain.Log.CommittedSequence);
        replay.Grain.OpenGate();
        Assert.Equal("committed at 0", (await replay.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<string>());
    });

    [Fact]
    public Task CommittedDraftIsLostWithTheStepsRecordWhenTheActivationEndsBeforeTheirWrite() => ActivationScheduler.RunAsync(async () =>
    {
        var chunks = Channel.CreateUnbounded<int>();
        await using var harness = new DurableTaskReplayHarness(grain => RespondAsync(grain, chunks.Reader));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await using var drafts = ReadLog(activation, DurableWatchMode.Drafts);
            Assert.Equal(LogEventKind.CaughtUp, (await NextAsync(drafts)).Kind);
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            chunks.Writer.TryWrite(1);
            Assert.Equal(LogEventKind.DraftItem, (await NextAsync(drafts)).Kind);

            // The step commits its draft and stages its result, and the write which would commit both fails.
            activation.FailWrites();
            Stream(chunks, 2);
            await activation.FailedWrite.WaitAsync(WaitTimeout, TestCancellation);

            Assert.Equal([3], activation.Grain.Log.ToArray());
            Assert.Equal(0, activation.Grain.Log.CommittedSequence);
            Assert.Equal(0L, GetTask(activation, StepId).Result!.GetResult<long>());
            Assert.DoesNotContain(activation.Writes, static write => write.Entries > 0);
        }

        // The entry was lost with the step's record, so the replay streams again and commits once.
        chunks = Channel.CreateUnbounded<int>();
        await using var replay = await harness.ActivateAsync(TestCancellation);
        Assert.Empty(replay.Grain.Log);
        Stream(chunks, 4);
        await replay.Grain.AtGate.WaitAsync(WaitTimeout, TestCancellation);

        Assert.Equal(2, harness.EffectRuns);
        Assert.Equal([4], replay.Grain.Log.ToArray());
        Assert.Equal(1, replay.Grain.Log.CommittedSequence);
        replay.Grain.OpenGate();
        Assert.Equal("committed at 0", (await replay.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<string>());
    });

    [Fact]
    public Task StepWhichFailsMidStreamAbandonsItsDraftAndRecordsTheFailure() => ActivationScheduler.RunAsync(async () =>
    {
        const string Failure = "failed: The model stopped responding.";
        var chunks = Channel.CreateUnbounded<int>();
        await using var harness = new DurableTaskReplayHarness(grain => RespondAsync(grain, chunks.Reader));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await using var drafts = ReadLog(activation, DurableWatchMode.Drafts);
            Assert.Equal(LogEventKind.CaughtUp, (await NextAsync(drafts)).Kind);
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            chunks.Writer.TryWrite(1);
            var item = await NextAsync(drafts);
            Assert.Equal(LogEventKind.DraftItem, item.Kind);

            // The stream fails, and disposing the draft abandons it.
            chunks.Writer.Complete(new InvalidOperationException("The model stopped responding."));
            var abandoned = await NextAsync(drafts);
            Assert.Equal(
                (LogEventKind.DraftAbandoned, LogDraftAbandonReason.Disposed, item.DraftId),
                (abandoned.Kind, abandoned.AbandonReason, abandoned.DraftId));

            // The workflow resumes from the step once the write which records the step's failure commits.
            await activation.Grain.AtGate.WaitAsync(WaitTimeout, TestCancellation);
            Assert.IsType<InvalidOperationException>(GetTask(activation, StepId).Result!.Exception);
            Assert.Empty(activation.Grain.Log);
        }

        // The replay rethrows the recorded failure without streaming again.
        chunks = Channel.CreateUnbounded<int>();
        await using var replay = await harness.ActivateAsync(TestCancellation);
        await replay.Grain.AtGate.WaitAsync(WaitTimeout, TestCancellation);
        Assert.Equal(1, harness.EffectRuns);

        using var reading = CancellationTokenSource.CreateLinkedTokenSource(TestCancellation);
        await using var replayDrafts = ReadLog(replay, DurableWatchMode.Drafts, reading.Token);
        Assert.Equal(LogEventKind.CaughtUp, (await NextAsync(replayDrafts)).Kind);
        var next = replayDrafts.MoveNextAsync();

        replay.Grain.OpenGate();
        Assert.Equal(Failure, (await replay.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<string>());
        Assert.False(next.IsCompleted);
        reading.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next.AsTask());
        Assert.Empty(replay.Grain.Log);
    });

    [Fact]
    public Task AppendBeforeAnAwaitInAStepCanCommitBeforeTheStepCompletesAndRunsAgainOnReplay() => ActivationScheduler.RunAsync(async () =>
    {
        var stepId = WorkflowId.Child("append");
        await using var harness = new DurableTaskReplayHarness(static grain => Workflow(grain));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await activation.Grain.AtGate.WaitAsync(WaitTimeout, TestCancellation);

            // Unrelated grain work writes the journal while the step awaits; the write commits the entry without a record
            // of the step.
            activation.Grain.RequestUnrelatedWrite();
            await activation.Grain.UnrelatedWrites;

            Assert.Equal(1, activation.Grain.Log.CommittedSequence);
            Assert.All(
                activation.Writes.Where(static write => write.Entries == 1),
                write => Assert.DoesNotContain(stepId, write.Completed));
        }

        // The replay runs the step again, which appends the entry again.
        await using var replay = await harness.ActivateAsync(TestCancellation);
        await replay.Grain.AtGate.WaitAsync(WaitTimeout, TestCancellation);
        Assert.Equal(2, harness.EffectRuns);
        Assert.Equal([10, 10], replay.Grain.Log.ToArray());
        replay.Grain.OpenGate();

        Assert.Equal(1L, (await replay.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<long>());

        static async DurableTask<long> Workflow(ReplayGrain grain) => await DurableTask.Run(async _ =>
        {
            grain.Harness.CountEffectRun();
            var sequence = grain.Log.Append(10);
            await grain.WaitAtGateAsync();
            return sequence;
        }).WithId("append");
    });

    /// <summary>
    /// Streams <paramref name="chunks"/>, which stand in for a model's response, into a draft of the grain's log within a
    /// step, commits their sum as one entry in the step's final synchronous code, then waits at the grain's gate and
    /// describes the step's outcome.
    /// </summary>
    private static async DurableTask<string> RespondAsync(ReplayGrain grain, ChannelReader<int> chunks)
    {
        string outcome;
        try
        {
            var sequence = await DurableTask.Run(async cancellationToken =>
            {
                grain.Harness.CountEffectRun();
                using var draft = grain.Log.BeginDraft();
                var total = 0;
                await foreach (var chunk in chunks.ReadAllAsync(cancellationToken))
                {
                    draft.Write(chunk);
                    total += chunk;
                }

                return draft.Commit(total);
            }).WithId("respond");
            outcome = $"committed at {sequence}";
        }
        catch (InvalidOperationException exception)
        {
            outcome = $"failed: {exception.Message}";
        }

        await grain.WaitAtGateAsync();
        return outcome;
    }

    private static void Stream(Channel<int> chunks, params int[] items)
    {
        foreach (var item in items)
        {
            Assert.True(chunks.Writer.TryWrite(item));
        }

        chunks.Writer.Complete();
    }

    private static IAsyncEnumerator<LogEvent<int>> ReadLog(
        ReplayActivation activation,
        DurableWatchMode mode,
        CancellationToken cancellationToken = default) =>
        activation.Grain.Log.ReadAsync(mode: mode, cancellationToken: cancellationToken).GetAsyncEnumerator(cancellationToken);

    private static async Task<LogEvent<int>> NextAsync(IAsyncEnumerator<LogEvent<int>> reader)
    {
        Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(WaitTimeout, TestCancellation));
        return reader.Current;
    }

    private static IDurableTaskState GetTask(ReplayActivation activation, TaskId taskId)
    {
        Assert.True(activation.Storage.TryGetTask(taskId, out var state));
        return state;
    }
}
