using Orleans.DurableTasks;
using Orleans.DurableTasks.Diagnostics;
using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks.Runtime;
using Orleans.Journaling;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.Tests;

/// <summary>
/// Verifies that a durable task body's accesses to journaled state are recorded, so a replay observes the values which
/// the recording run observed and applies no committed change again.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class DurableTaskJournaledStateAccessTests
{
    private static readonly TaskId WorkflowId = TaskId.CreateRoot("workflow");

    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    private static TimeSpan Timeout => TimeSpan.FromSeconds(10);

    [Fact]
    public Task ReplayObservesRecordedReadsAndSkipsCommittedWrites() => ActivationScheduler.RunAsync(async () =>
    {
        await using var harness = new DurableTaskReplayHarness(static grain => ReserveAsync(grain));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await WriteStockAsync(activation.Grain, "widget", 1);
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await activation.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
            Assert.Equal(0, activation.Grain.Stock["widget"]);
            Assert.Single(activation.Grain.Log);

            // Other grain work restocks the item, and its write commits the workflow's changes with their records.
            await WriteStockAsync(activation.Grain, "widget", 5);
        }

        // The replay decides from the level which the first run read, and doesn't reserve, decrement, or log again.
        await using var replay = await harness.ActivateAsync(TestCancellation);
        await replay.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
        Assert.Equal(5, replay.Grain.Stock["widget"]);
        Assert.Single(replay.Grain.Log);
        Assert.Equal(1, harness.EffectRuns);
        replay.Grain.OpenGate();

        Assert.Equal(1, (await replay.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<int>());
        Assert.Empty(replay.Storage.GetAccessLog(WorkflowId));

        static async DurableTask<int> ReserveAsync(ReplayGrain grain)
        {
            var level = grain.Stock.TryGetValue("widget", out var stock) ? stock : 0;
            if (level > 0)
            {
                await DurableTask.Run(_ => grain.Harness.CountEffectRun()).WithId("reserve");
                grain.Stock["widget"] = level - 1;
                grain.Log.Append(level);
            }

            await grain.WaitAtGateAsync();
            return level;
        }
    });

    [Fact]
    public Task AccessesWhichNoWriteCommittedRunAgain() => ActivationScheduler.RunAsync(async () =>
    {
        await using var harness = new DurableTaskReplayHarness(static grain => Workflow(grain));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await activation.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
            Assert.Equal(1, activation.Grain.Counter.Value);
        }

        // No write committed the increment or its record, so the replay increments the committed value once.
        await using var replay = await harness.ActivateAsync(TestCancellation);
        await replay.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
        Assert.Equal(1, replay.Grain.Counter.Value);
        replay.Grain.OpenGate();
        await replay.GetResponseAsync(WorkflowId, TestCancellation);
        Assert.Equal(1, replay.Grain.Counter.Value);

        static async DurableTask Workflow(ReplayGrain grain)
        {
            grain.Counter.Value++;
            await grain.WaitAtGateAsync();
        }
    });

    [Fact]
    public Task ReplayRethrowsTheRecordedExceptionOfAnAccess() => ActivationScheduler.RunAsync(async () =>
    {
        await using var harness = new DurableTaskReplayHarness(static grain => Workflow(grain));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await activation.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
            await WriteStockAsync(activation.Grain, "gadget", 3);
        }

        // The item exists now, and the replay still observes the missing key which the first run observed.
        await using var replay = await harness.ActivateAsync(TestCancellation);
        await replay.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
        replay.Grain.OpenGate();

        Assert.Equal(-1, (await replay.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<int>());

        static async DurableTask<int> Workflow(ReplayGrain grain)
        {
            int level;
            try
            {
                level = grain.Stock["gadget"];
            }
            catch (KeyNotFoundException)
            {
                level = -1;
            }

            await DurableTask.Run(static _ => { }).WithId("step");
            await grain.WaitAtGateAsync();
            return level;
        }
    });

    [Fact]
    public Task ReplayRethrowsTheNormalizedExceptionWhenEncodingTheOriginalFails() => ActivationScheduler.RunAsync(async () =>
    {
        string? firstOutcome = null;
        await using var harness = new DurableTaskReplayHarness(Workflow);
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await CommitAtGateAsync(activation);
            Assert.StartsWith("The access to journaled state 'list' threw System.Collections.Generic.KeyNotFoundException: source failed", firstOutcome, StringComparison.Ordinal);
            Assert.Equal(2, activation.Grain.Counter.Value);
            Assert.Equal([7], activation.Grain.List);
        }

        await using var replay = await harness.ActivateAsync(TestCancellation);
        await replay.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
        replay.Grain.OpenGate();

        Assert.Equal(firstOutcome, (await replay.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<string>());
        Assert.Equal(2, replay.Grain.Counter.Value);
        Assert.Equal([7], replay.Grain.List);
        Assert.Equal(1, harness.EffectRuns);

        async DurableTask<string> Workflow(ReplayGrain grain)
        {
            string outcome;
            try
            {
                grain.List.AddRange(ItemsThenFailure());
                outcome = "succeeded";
            }
            catch (KeyNotFoundException)
            {
                grain.Counter.Value = 1;
                outcome = "original exception";
            }
            catch (InvalidOperationException exception)
            {
                grain.Counter.Value = 2;
                outcome = exception.Message;
            }

            firstOutcome ??= outcome;
            await DurableTask.Run(_ => grain.Harness.CountEffectRun()).WithId("after-failure");
            await grain.WaitAtGateAsync();
            return outcome;
        }

        static IEnumerable<int> ItemsThenFailure()
        {
            yield return 7;
            var exception = new KeyNotFoundException("source failed");
            exception.Data["unserializable"] = new UnserializableValue();
            throw exception;
        }
    });

    [Theory]
    [InlineData("seal")]
    [InlineData("abandon")]
    [InlineData("dispose")]
    [InlineData("commit")]
    public Task ReplayRecordsAClosedDraftCommitBeforeTheNextStateAccess(string closure) => ActivationScheduler.RunAsync(async () =>
    {
        string? firstOutcome = null;
        await using var harness = new DurableTaskReplayHarness(Workflow);
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await CommitAtGateAsync(activation);
            Assert.Equal(1, activation.Grain.Counter.Value);
            Assert.Contains("closed", firstOutcome, StringComparison.Ordinal);
        }

        await using var replay = await harness.ActivateAsync(TestCancellation);
        await replay.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
        replay.Grain.OpenGate();

        Assert.Equal(firstOutcome, (await replay.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<string>());
        Assert.Equal(1, replay.Grain.Counter.Value);
        Assert.Equal(closure == "commit" ? [7] : Array.Empty<int>(), replay.Grain.Log);
        Assert.Equal(closure == "seal", replay.Grain.Log.IsSealed);
        Assert.Equal(1, harness.EffectRuns);

        async DurableTask<string> Workflow(ReplayGrain grain)
        {
            using var draft = grain.Log.BeginDraft();
            switch (closure)
            {
                case "seal":
                    grain.Log.Seal();
                    break;
                case "abandon":
                    draft.Abandon();
                    break;
                case "dispose":
                    draft.Dispose();
                    break;
                case "commit":
                    Assert.Equal(0, draft.Commit(7));
                    Assert.False(draft.IsOpen);
                    break;
            }

            string outcome;
            try
            {
                draft.Commit(8);
                outcome = "succeeded";
            }
            catch (InvalidOperationException exception)
            {
                outcome = exception.Message;
                grain.Counter.Value++;
            }

            firstOutcome ??= outcome;
            await DurableTask.Run(_ => grain.Harness.CountEffectRun()).WithId("after-commit");
            await grain.WaitAtGateAsync();
            return outcome;
        }
    });

    [Fact]
    public Task ReplayedSealClosesTheDraftBeforeAnIsOpenGuardedCommit() => ActivationScheduler.RunAsync(async () =>
    {
        await using var harness = new DurableTaskReplayHarness(static grain => Workflow(grain));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await CommitAtGateAsync(activation);
            Assert.True(activation.Grain.Log.IsSealed);
            Assert.Empty(activation.Grain.Log);
            Assert.Equal(1, activation.Grain.Counter.Value);
        }

        await using var replay = await harness.ActivateAsync(TestCancellation);
        await replay.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
        replay.Grain.OpenGate();

        Assert.False((await replay.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<bool>());
        Assert.True(replay.Grain.Log.IsSealed);
        Assert.Empty(replay.Grain.Log);
        Assert.Equal(1, replay.Grain.Counter.Value);
        Assert.Equal(1, harness.EffectRuns);

        static async DurableTask<bool> Workflow(ReplayGrain grain)
        {
            using var draft = grain.Log.BeginDraft();
            draft.Write(7);
            grain.Log.Seal();
            var isOpen = draft.IsOpen;
            if (isOpen)
            {
                draft.Commit(7);
            }

            grain.Counter.Value++;
            await DurableTask.Run(_ => grain.Harness.CountEffectRun()).WithId("after-seal");
            await grain.WaitAtGateAsync();
            return isOpen;
        }
    });

    [Fact]
    public Task ReadsStageOnlyWithALaterWriteOrOperation() => ActivationScheduler.RunAsync(async () =>
    {
        await using var harness = new DurableTaskReplayHarness(static grain => Workflow(grain));
        await using var activation = await harness.ActivateAsync(TestCancellation);
        await activation.ScheduleAsync(WorkflowId, TestCancellation);
        await activation.Grain.AtGate.WaitAsync(Timeout, TestCancellation);

        // No later operation depends on the read yet, so its record isn't staged.
        Assert.Empty(activation.Storage.GetAccessLog(WorkflowId));
        activation.Grain.OpenGate();

        // The write stages the read's record and its own, and the result removes both.
        Assert.True((await activation.GetResponseAsync(WorkflowId, TestCancellation)).IsCompleted);
        Assert.Empty(activation.Storage.GetAccessLog(WorkflowId));
        Assert.Equal(1, activation.Grain.Counter.Value);

        static async DurableTask Workflow(ReplayGrain grain)
        {
            await DurableTask.Run(static _ => { }).WithId("step");
            var value = grain.Counter.Value;
            await grain.WaitAtGateAsync();
            grain.Counter.Value = value + 1;
            await DurableTask.Run(static _ => { }).WithId("after");
        }
    });

    [Fact]
    public Task RunInlineRecordCoversTheAccessesOfItsEffect() => ActivationScheduler.RunAsync(async () =>
    {
        await using var harness = new DurableTaskReplayHarness(static grain => Workflow(grain));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await CommitAtGateAsync(activation);
            Assert.Equal(1, activation.Grain.Counter.Value);
        }

        // The replay returns the effect's record without running the effect, and its increment applies once.
        await using var replay = await harness.ActivateAsync(TestCancellation);
        await replay.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
        replay.Grain.OpenGate();
        Assert.Equal(0, (await replay.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<int>());
        Assert.Equal(1, replay.Grain.Counter.Value);
        Assert.Equal(1, harness.EffectRuns);

        static async DurableTask<int> Workflow(ReplayGrain grain)
        {
            var before = DurableTask.RunInline("increment", () =>
            {
                grain.Harness.CountEffectRun();
                return grain.Counter.Value++;
            });
            await DurableTask.Run(static _ => { }).WithId("step");
            await grain.WaitAtGateAsync();
            return before;
        }
    });

    [Fact]
    public Task AttemptWhichDivergesFromARecordedResultDiscardsItsCleanupWrites() => ActivationScheduler.RunAsync(async () =>
    {
        var readsText = false;
        var failures = new List<Exception>();
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = new DurableTaskReplayHarness(grain => Workflow(grain, readsText, failures, failed));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await CommitAtGateAsync(activation);
        }

        // A later version reads the step's recorded result as another type. The abstractions end the attempt as diverged
        // without the runtime, so its cleanup's write throws the attempt's cancellation and leaves the state unchanged.
        readsText = true;
        await using var diverged = await harness.ActivateAsync(TestCancellation);
        await failed.Task.WaitAsync(Timeout, TestCancellation);
        var failure = Assert.IsType<DurableTaskAttemptCanceledException>(Assert.Single(failures));
        Assert.Equal(DurableTaskAttemptCancellationReason.Diverged, failure.Reason);
        Assert.Equal(0, diverged.Grain.Counter.Value);

        static async DurableTask<int> Workflow(ReplayGrain grain, bool readsText, List<Exception> failures, TaskCompletionSource failed)
        {
            if (!readsText)
            {
                // The body records no access, so the later version's cleanup finds no record to replay.
                var value = await DurableTask.Run(static _ => 1).WithId("step");
                await grain.WaitAtGateAsync();
                return value;
            }

            try
            {
                _ = await DurableTask.Run(static _ => "one").WithId("step");
                throw new InvalidOperationException("The step's recorded result has another type.");
            }
            catch (DurableTaskAttemptCanceledException)
            {
                try
                {
                    grain.Counter.Value = 99;
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }

                failed.TrySetResult();
                throw;
            }
        }
    });

    [Fact]
    public Task ReplayWhichAccessesOtherStateStallsUntilCodeWhichReplaysTheRecordRunsIt() => ActivationScheduler.RunAsync(async () =>
    {
        var readsLog = false;
        var stalled = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failures = new List<Exception>();
        using var subscription = DurableTaskEvents.AllEvents.Subscribe(new EventObserver(evt =>
        {
            if (evt is DurableTaskEvents.ExecutionStalled { Reason: DurableTaskAttemptCancellationReason.Diverged } stall && evt.TaskId == WorkflowId)
            {
                stalled.TrySetResult(stall.Exception);
            }
        }));

        await using var harness = new DurableTaskReplayHarness(grain => Workflow(grain, readsLog, failures));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await activation.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
            Assert.Equal(1, activation.Grain.Counter.Value);
        }

        // A later version reads other state first. The divergence stalls the attempt: the body's handler observes the
        // attempt's cancellation, and the task stays pending.
        readsLog = true;
        await using (var diverged = await harness.ActivateAsync(TestCancellation))
        {
            var divergence = await stalled.Task.WaitAsync(Timeout, TestCancellation);
            Assert.Contains("diverged", Assert.IsType<DurableTaskDivergenceException>(divergence).Message, StringComparison.Ordinal);
            await WaitUntilAsync(() => failures.Count == 1);
            Assert.All(failures, failure =>
            {
                var canceled = Assert.IsType<DurableTaskAttemptCanceledException>(failure);
                Assert.Equal(DurableTaskAttemptCancellationReason.Diverged, canceled.Reason);
                Assert.Same(divergence, canceled.InnerException);
            });
            Assert.Equal(1, diverged.Grain.Counter.Value);
            Assert.True(diverged.Storage.TryGetTask(WorkflowId, out var state));
            Assert.False(state.Result is { IsCompleted: true });
        }

        // Code which accesses state as the record does replays the task to its outcome.
        readsLog = false;
        await using var fixedCode = await harness.ActivateAsync(TestCancellation);
        await fixedCode.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
        fixedCode.Grain.OpenGate();
        Assert.Equal(0, (await fixedCode.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<int>());
        Assert.Equal(1, fixedCode.Grain.Counter.Value);

        static async DurableTask<int> Workflow(ReplayGrain grain, bool readsLog, List<Exception> failures)
        {
            int value;
            try
            {
                value = readsLog ? grain.Log.Count : grain.Counter.Value++;
            }
            catch (Exception exception)
            {
                failures.Add(exception);
                throw;
            }

            await DurableTask.Run(static _ => { }).WithId("step");
            await grain.WaitAtGateAsync();
            return value;
        }
    });

    [Fact]
    public Task DiscardedAttemptsCleanupWritesThrowAndItsReadsRunLive() => ActivationScheduler.RunAsync(async () =>
    {
        var cleanup = new TaskCompletionSource<(Exception? Write, int Read)>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = new DurableTaskReplayHarness(grain => Workflow(grain, cleanup));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await activation.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
            activation.Grain.RequestUnrelatedWrite();
            await activation.Grain.UnrelatedWrites;

            // Stopping the activation discards the attempt waiting at the gate: the next activation reruns it.
        }

        // The cleanup isn't part of the execution's history, so its write throws the attempt's cancellation without
        // changing the counter, and its read observes the counter directly.
        var (write, read) = await cleanup.Task.WaitAsync(Timeout, TestCancellation);
        Assert.Equal(DurableTaskAttemptCancellationReason.ActivationStopping, Assert.IsType<DurableTaskAttemptCanceledException>(write).Reason);
        Assert.Equal(1, read);

        // The replay returns the committed increment's record and increments once more after the gate.
        await using var replay = await harness.ActivateAsync(TestCancellation);
        await replay.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
        Assert.Equal(1, replay.Grain.Counter.Value);
        replay.Grain.OpenGate();
        Assert.Equal(2, (await replay.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<int>());
        Assert.Equal(2, replay.Grain.Counter.Value);

        static async DurableTask<int> Workflow(ReplayGrain grain, TaskCompletionSource<(Exception?, int)> cleanup)
        {
            grain.Counter.Value++;
            try
            {
                await grain.WaitAtGateAsync();
            }
            catch (OperationCanceledException)
            {
                cleanup.TrySetResult((Record.Exception(() => grain.Counter.Value += 10), grain.Counter.Value));
                throw;
            }

            return ++grain.Counter.Value;
        }
    });

    [Fact]
    public Task AbortedTasksCleanupChangesStateAfterReplayingTheCommittedRecords() => ActivationScheduler.RunAsync(async () =>
    {
        var cleanup = new TaskCompletionSource<(Exception? Write, int Read)>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = new DurableTaskReplayHarness(static grain => Increment(grain));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await CommitAtGateAsync(activation);
            Assert.Equal(1, activation.Grain.Counter.Value);
        }

        // The abort ends the task while the replay still holds the committed increment's records: the cleanup's increment
        // returns them instead of applying the increment again, and its next write applies directly.
        harness.Workflow = grain => WaitThenCleanUp(grain, cleanup);
        await using var replay = await harness.ActivateAsync(TestCancellation);
        await replay.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
        await ((IDurableTaskManager)replay.Runtime).AbortAsync(WorkflowId, "operator", TestCancellation);
        var (write, read) = await cleanup.Task.WaitAsync(Timeout, TestCancellation);
        Assert.Null(write);
        Assert.Equal(101, read);
        Assert.Equal(101, replay.Grain.Counter.Value);

        static async DurableTask Increment(ReplayGrain grain)
        {
            grain.Counter.Value++;
            await grain.WaitAtGateAsync();
        }

        static async DurableTask WaitThenCleanUp(ReplayGrain grain, TaskCompletionSource<(Exception?, int)> cleanup)
        {
            try
            {
                await grain.WaitAtGateAsync();
            }
            catch (OperationCanceledException)
            {
                var write = Record.Exception(() =>
                {
                    grain.Counter.Value++;
                    grain.Counter.Value += 100;
                });
                cleanup.TrySetResult((write, grain.Counter.Value));
                throw;
            }
        }
    });

    [Fact]
    public Task WorkWhichOutlivesACompletedAttemptChangesStateUnrecorded() => ActivationScheduler.RunAsync(async () =>
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outcome = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = new DurableTaskReplayHarness(grain => Workflow(grain, release.Task, outcome));
        await using var activation = await harness.ActivateAsync(TestCancellation);
        await activation.ScheduleAsync(WorkflowId, TestCancellation);
        await activation.GetResponseAsync(WorkflowId, TestCancellation);

        // The body never replays once its result commits, so the work's change applies directly, as other grain code's
        // does, and the next activation recovers it with the task's result.
        release.SetResult();
        var write = await outcome.Task.WaitAsync(Timeout, TestCancellation);
        Assert.Null(write);
        Assert.Equal(5, activation.Grain.Counter.Value);
        activation.Grain.RequestUnrelatedWrite();
        await activation.Grain.UnrelatedWrites;
        await activation.DisposeAsync();

        await using var recovered = await harness.ActivateAsync(TestCancellation);
        Assert.Equal(5, recovered.Grain.Counter.Value);
        Assert.Equal(1, (await recovered.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<int>());

        static DurableTask<int> Workflow(ReplayGrain grain, Task release, TaskCompletionSource<Exception?> outcome)
        {
            grain.Counter.Value = 1;
            _ = Task.Run(async () =>
            {
                await release;
                outcome.TrySetResult(Record.Exception(() => grain.Counter.Value = 5));
            });

            return DurableTask.FromResult(1);
        }
    });

    [Fact]
    public Task BodyObservesStateLiveUnrecorded() => ActivationScheduler.RunAsync(async () =>
    {
        await using var harness = new DurableTaskReplayHarness(static grain => Workflow(grain));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            activation.Grain.Log.Append(7);
            activation.Grain.Counter.Value = 1;
            activation.Grain.Completion.TrySetResult(3);
            activation.Grain.RequestUnrelatedWrite();
            await activation.Grain.UnrelatedWrites;
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await CommitAtGateAsync(activation);
            Assert.Equal("7,1,3", activation.Grain.Observed);

            // Other grain work changes the observed log and counter after the body observed them.
            activation.Grain.Log.TrimBefore(1);
            activation.Grain.Log.Append(8);
            activation.Grain.Counter.Value = 2;
            activation.Grain.RequestUnrelatedWrite();
            await activation.Grain.UnrelatedWrites;
        }

        // Live observations report the state's changes as they happen, which no record holds, so the replay observes the
        // current state without diverging.
        await using var replay = await harness.ActivateAsync(TestCancellation);
        await replay.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
        replay.Grain.OpenGate();
        Assert.Equal("8,2,3", (await replay.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<string>());

        static async DurableTask<string> Workflow(ReplayGrain grain)
        {
            int entry = -1;
            await foreach (var logEvent in grain.Log.ReadAsync(cancellationToken: DurableTask.Current.CancellationToken))
            {
                if (logEvent.Kind is LogEventKind.Entry)
                {
                    entry = logEvent.Item;
                    break;
                }
            }

            var counter = -1;
            await foreach (var change in grain.Counter.WatchAsync(cancellationToken: DurableTask.Current.CancellationToken))
            {
                counter = change.Value;
                break;
            }

            var completion = await grain.Completion.Task;
            grain.Observed = $"{entry},{counter},{completion}";
            await DurableTask.Run(static _ => { }).WithId("step");
            await grain.WaitAtGateAsync();
            return grain.Observed;
        }
    });
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition wasn't met in time.");
            await Task.Delay(10, TestCancellation);
        }
    }

    [Fact]
    public Task NestedRunInlineRunsWithinItsOuterEffect() => ActivationScheduler.RunAsync(async () =>
    {
        await using var harness = new DurableTaskReplayHarness(static grain => Workflow(grain));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await activation.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
        }

        // The replay returns the outer effect's record without running the nested effect, so the unnamed step keeps the
        // identifier which the first run gave it.
        await using var replay = await harness.ActivateAsync(TestCancellation);
        await replay.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
        replay.Grain.OpenGate();

        Assert.Equal(12, (await replay.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<int>());

        static async DurableTask<int> Workflow(ReplayGrain grain)
        {
            var outer = DurableTask.RunInline(static () => DurableTask.RunInline(static () => 1) + 1);
            var step = await DurableTask.Run(static _ => 10);
            await grain.WaitAtGateAsync();
            return outer + step;
        }
    });

    [Fact]
    public Task CodeWhichCreatesTheDefinitionIsRecorded() => ActivationScheduler.RunAsync(async () =>
    {
        await using var harness = new DurableTaskReplayHarness(static grain => Create(grain));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await CommitAtGateAsync(activation);
            Assert.Equal(1, activation.Grain.Counter.Value);
        }

        // The method which isn't async runs within the attempt, so the replay returns its write's record.
        await using var replay = await harness.ActivateAsync(TestCancellation);
        await replay.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
        replay.Grain.OpenGate();
        Assert.Equal(1, (await replay.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<int>());
        Assert.Equal(1, replay.Grain.Counter.Value);

        static DurableTask<int> Create(ReplayGrain grain)
        {
            var count = ++grain.Counter.Value;
            return RunAsync(grain, count);
        }

        static async DurableTask<int> RunAsync(ReplayGrain grain, int count)
        {
            await DurableTask.Run(static _ => { }).WithId("step");
            await grain.WaitAtGateAsync();
            return count;
        }
    });

    [Fact]
    public Task ChildStepRecordsItsOwnAccesses() => ActivationScheduler.RunAsync(async () =>
    {
        await using var harness = new DurableTaskReplayHarness(static grain => Workflow(grain));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await CommitAtGateAsync(activation);
            Assert.Equal(2, activation.Grain.Counter.Value);

            // Other grain work changes the counter after the body read it.
            activation.Grain.Counter.Value = 10;
            activation.Grain.RequestUnrelatedWrite();
            await activation.Grain.UnrelatedWrites;
        }

        // The child's result and the parent's reads replay from their records, and neither increment applies again.
        await using var replay = await harness.ActivateAsync(TestCancellation);
        await replay.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
        replay.Grain.OpenGate();
        Assert.Equal(12, (await replay.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<int>());
        Assert.Equal(10, replay.Grain.Counter.Value);

        static async DurableTask<int> Workflow(ReplayGrain grain)
        {
            grain.Counter.Value++;
            var child = await Increment(grain).WithId("child");
            var observed = grain.Counter.Value;
            await DurableTask.Run(static _ => { }).WithId("after");
            await grain.WaitAtGateAsync();
            return child * 10 + observed;
        }

        static async DurableTask<int> Increment(ReplayGrain grain)
        {
            await Task.Yield();
            return ++grain.Counter.Value - 1;
        }
    });

    [Fact]
    public Task RuntimeWorkWhichABodyRequestsIsntRecordedAsTheBodys() => ActivationScheduler.RunAsync(async () =>
    {
        IDurableTaskManager? manager = null;
        var siblingId = TaskId.CreateRoot("sibling");
        await using var harness = new DurableTaskReplayHarness(grain => Workflow(grain, manager!, siblingId));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            manager = activation.Runtime;
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await CommitAtGateAsync(activation);
            Assert.Equal(1, activation.Grain.Counter.Value);
        }

        // The cancellation and the abort read and change the runtime's own state, which the body's records don't hold, so
        // the replay returns the body's two records in order.
        await using var replay = await harness.ActivateAsync(TestCancellation);
        manager = replay.Runtime;
        await replay.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
        replay.Grain.OpenGate();
        Assert.Equal(0, (await replay.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<int>());
        Assert.Equal(1, replay.Grain.Counter.Value);

        static async DurableTask<int> Workflow(ReplayGrain grain, IDurableTaskManager manager, TaskId siblingId)
        {
            var value = grain.Counter.Value;

            // The manager cancels a task which this grain retains, such as the body's own child.
            var child = await DurableTask.Delay(TimeSpan.FromHours(1)).WithId("delay").ScheduleAsync();
            await manager.CancelAsync(child.Id, "not needed");
            await manager.GetTask<int>(siblingId).AbortAsync("not needed");
            grain.Counter.Value = value + 1;
            await grain.WaitAtGateAsync();
            return value;
        }
    });

    [Fact]
    public Task RecordWhichThisVersionCantReadStallsTheReplay() => ActivationScheduler.RunAsync(async () =>
    {
        var stalled = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = DurableTaskEvents.AllEvents.Subscribe(new EventObserver(evt =>
        {
            if (evt is DurableTaskEvents.ExecutionStalled { Reason: DurableTaskAttemptCancellationReason.Diverged } stall && evt.TaskId == WorkflowId)
            {
                stalled.TrySetResult(stall.Exception);
            }
        }));

        await using var harness = new DurableTaskReplayHarness(static grain => Workflow(grain));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await activation.Grain.AtGate.WaitAsync(Timeout, TestCancellation);

            // A record which a later version wrote, with a representation which this version doesn't know.
            activation.Storage.AppendAccessLog(WorkflowId, [0x21, (byte)JournaledStateOperation.ValueGet, 0, 0, 0, 0]);
            activation.Grain.RequestUnrelatedWrite();
            await activation.Grain.UnrelatedWrites;
        }

        await using var replay = await harness.ActivateAsync(TestCancellation);
        await replay.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
        replay.Grain.OpenGate();
        var divergence = await stalled.Task.WaitAsync(Timeout, TestCancellation);
        Assert.Contains("has a representation, 0x21, which this version doesn't read", Assert.IsType<DurableTaskDivergenceException>(divergence).Message, StringComparison.Ordinal);
        Assert.True(replay.Storage.TryGetTask(WorkflowId, out var state));
        Assert.False(state.Result is { IsCompleted: true });

        static async DurableTask<int> Workflow(ReplayGrain grain)
        {
            var first = grain.Counter.Value;
            await DurableTask.Run(static _ => { }).WithId("step");
            await grain.WaitAtGateAsync();
            return first + grain.Counter.Value;
        }
    });

    private static async Task CommitAtGateAsync(ReplayActivation activation)
    {
        await activation.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
        activation.Grain.RequestUnrelatedWrite();
        await activation.Grain.UnrelatedWrites;
    }

    /// <summary>Sets an item's stock level as ordinary grain work does, and waits for a write which commits it.</summary>
    private static async Task WriteStockAsync(ReplayGrain grain, string item, int level)
    {
        grain.Stock[item] = level;
        grain.RequestUnrelatedWrite();
        await grain.UnrelatedWrites;
    }

    private sealed class UnserializableValue;
}
