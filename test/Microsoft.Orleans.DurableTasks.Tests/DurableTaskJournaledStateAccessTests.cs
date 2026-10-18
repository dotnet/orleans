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
    public Task RunInlineRecordsTheAccessesOfItsEffect() => ActivationScheduler.RunAsync(async () =>
    {
        await using var harness = new DurableTaskReplayHarness(static grain => Workflow(grain));
        await using var activation = await harness.ActivateAsync(TestCancellation);
        await activation.ScheduleAsync(WorkflowId, TestCancellation);
        await activation.Grain.AtGate.WaitAsync(Timeout, TestCancellation);

        Assert.Equal(1, activation.Grain.Counter.Value);
        Assert.Empty(activation.Storage.GetAccessLog(WorkflowId));

        static async DurableTask Workflow(ReplayGrain grain)
        {
            DurableTask.RunInline("increment", () => grain.Counter.Value++);
            await DurableTask.Run(static _ => { }).WithId("step");
            await grain.WaitAtGateAsync();
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
    public Task WorkWhichOutlivesACanceledAttemptChangesStateUnrecorded() => ActivationScheduler.RunAsync(async () =>
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outcome = new TaskCompletionSource<(Exception? Write, int Read)>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = new DurableTaskReplayHarness(grain => Workflow(grain, release.Task, outcome));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await activation.Grain.AtGate.WaitAsync(Timeout, TestCancellation);

            // Stopping the activation cancels the attempt, which drains, so no attempt runs on the activation afterwards.
        }

        // The canceled attempt's log is closed, so the work's change applies directly, as other grain code's does.
        release.SetResult();
        var (write, read) = await outcome.Task.WaitAsync(Timeout, TestCancellation);
        Assert.Null(write);
        Assert.Equal(5, read);

        static async DurableTask Workflow(ReplayGrain grain, Task release, TaskCompletionSource<(Exception?, int)> outcome)
        {
            grain.Counter.Value = 1;

            // Task.Run flows the attempt's context into work which outlives the attempt.
            _ = Task.Run(async () =>
            {
                await release;
                var write = Record.Exception(() => grain.Counter.Value = 5);
                outcome.TrySetResult((write, grain.Counter.Value));
            });

            await DurableTask.Run(static _ => { }).WithId("step");
            await grain.WaitAtGateAsync();
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

        // The body never replays once its result commits, so the work's change applies directly and isn't recorded.
        release.SetResult();
        var write = await outcome.Task.WaitAsync(Timeout, TestCancellation);
        Assert.Null(write);
        Assert.Equal(5, activation.Grain.Counter.Value);
        Assert.Empty(activation.Storage.GetAccessLog(WorkflowId));

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
        await using var activation = await harness.ActivateAsync(TestCancellation);
        await activation.ScheduleAsync(WorkflowId, TestCancellation);

        // Live observations report the state's changes as they happen, which no record can hold.
        Assert.Equal("observed", (await activation.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<string>());
        Assert.Empty(activation.Storage.GetAccessLog(WorkflowId));

        static DurableTask<string> Workflow(ReplayGrain grain)
        {
            try
            {
                _ = grain.Log.ReadAsync();
                return DurableTask.FromResult("observed");
            }
            catch (InvalidOperationException exception)
            {
                return DurableTask.FromResult(exception.Message);
            }
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
            activation.Storage.AppendAccessLog(WorkflowId, [0x11, (byte)JournaledStateOperation.ValueGet, 0, 0, 0, 0]);
            activation.Grain.RequestUnrelatedWrite();
            await activation.Grain.UnrelatedWrites;
        }

        await using var replay = await harness.ActivateAsync(TestCancellation);
        await replay.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
        replay.Grain.OpenGate();
        var divergence = await stalled.Task.WaitAsync(Timeout, TestCancellation);
        Assert.IsType<NotSupportedException>(Assert.IsType<DurableTaskDivergenceException>(divergence).InnerException);
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
