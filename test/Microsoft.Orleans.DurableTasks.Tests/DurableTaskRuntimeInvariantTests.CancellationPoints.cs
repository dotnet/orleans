using System.Collections.Concurrent;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Diagnostics;
using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks.Runtime;
using Orleans.DurableTasks.Storage;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.Tests;

/// <summary>
/// Cancellation points: an execution records the point, counted in the starts of its body's durable operations, at which
/// the body first observes its task's cancellation, and a replay delivers the cancellation at that point.
/// </summary>
public sealed partial class DurableTaskRuntimeInvariantTests
{
    private static readonly DurableSignal<string> NeverSent = new("never-sent");
    private static readonly TimeSpan CancellationPointTimeout = TimeSpan.FromSeconds(10);

    [Theory]
    [MemberData(nameof(DurableTaskJournalHarness.Formats), MemberType = typeof(DurableTaskJournalHarness))]
    public Task ReplayObservesTaskCancellationAtTheRecordedPointAndRecordsTheSameCompensation(string journalFormatKey) => ActivationScheduler.RunAsync(async () =>
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var workflowId = TaskId.CreateRoot($"canceled-{Guid.NewGuid():N}");
        var probe = new CancellationProbe();
        await using var harness = new DurableTaskReplayHarness(grain => ObserveAtGateAsync(grain, probe, holdBeforeCompensation: false), journalFormatKey);
        string[] live;
        TaskId[] liveRecords;
        await using (var activation = await harness.ActivateAsync(cancellationToken))
        {
            await activation.ScheduleAsync(workflowId, cancellationToken);
            await activation.Grain.AtGate.WaitAsync(CancellationPointTimeout, cancellationToken);

            // The body observes the cancellation at the gate, after the start of its second operation: point 2.
            await ((IDurableTaskManager)activation.Runtime).CancelAsync(workflowId, "stop", cancellationToken);
            await probe.Held.WaitAsync(CancellationPointTimeout, cancellationToken);
            await activation.Grain.UnrelatedWrites;
            live = probe.NextActivation();
            liveRecords = GetRecordIds(activation.Storage, workflowId);
            Assert.Equal(2, GetCancellationPoint(activation.Storage, workflowId));
        }

        Assert.Equal(["first:False", "second:False", "canceled:True"], live);
        Assert.Equal(1, harness.EffectRuns);

        // The activation ends while the compensated body holds. Each journal format recovers the record, and the replay
        // keeps the durable token uncanceled until it reaches the recorded point, so it takes the same path.
        await using var replay = await harness.ActivateAsync(cancellationToken);
        Assert.True(replay.Storage.TryGetTask(workflowId.Child(DurableTaskGrainRuntime.CancellationPointSegment), out var record));
        Assert.Equal(DurableTaskRecordKind.CancellationObserved, record.Kind);
        Assert.Equal(2, record.Result!.GetResult<int>());
        await probe.Held.WaitAsync(CancellationPointTimeout, cancellationToken);
        probe.Release();

        Assert.Equal("compensated", (await replay.GetResponseAsync(workflowId, cancellationToken)).GetResult<string>());
        Assert.Equal(live, probe.NextActivation());
        Assert.Equal(1, harness.EffectRuns);
        Assert.Equal(1, replay.Grain.Counter.Value);
        Assert.Equal(liveRecords, GetRecordIds(replay.Storage, workflowId));
        Assert.Equal(2, GetCancellationPoint(replay.Storage, workflowId));
    });

    [Fact]
    public Task CancellationOfAParkedAttemptRecordsThePointOfTheParkedWait() => ActivationScheduler.RunAsync(async () =>
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var workflowId = TaskId.CreateRoot($"canceled-{Guid.NewGuid():N}");
        var probe = new CancellationProbe();
        await using var harness = new DurableTaskReplayHarness(grain => ObserveAtSignalWaitAsync(grain, probe));
        string[] live;
        await using (var activation = await harness.ActivateAsync(cancellationToken))
        {
            await activation.ScheduleAsync(workflowId, cancellationToken);
            await probe.AtWait.WaitAsync(CancellationPointTimeout, cancellationToken);

            // The attempt is parked at its signal wait, whose start is its third point.
            await ((IDurableTaskManager)activation.Runtime).CancelAsync(workflowId, "stop", cancellationToken);
            await probe.Held.WaitAsync(CancellationPointTimeout, cancellationToken);
            await activation.Grain.UnrelatedWrites;
            live = probe.NextActivation();
            Assert.Equal(3, GetCancellationPoint(activation.Storage, workflowId));
        }

        Assert.Equal(["first:False", "second:False", "wait:False", "canceled:True"], live);

        // The replay delivers the cancellation as it starts the signal wait, which then ends as it did in the first run.
        await using var replay = await harness.ActivateAsync(cancellationToken);
        await probe.Held.WaitAsync(CancellationPointTimeout, cancellationToken);
        probe.Release();

        Assert.Equal("compensated", (await replay.GetResponseAsync(workflowId, cancellationToken)).GetResult<string>());
        Assert.Equal(live, probe.NextActivation());
        Assert.Equal(1, harness.EffectRuns);
        Assert.Equal(3, GetCancellationPoint(replay.Storage, workflowId));
    });

    [Fact]
    public Task ReplayWhosePointDidNotCommitObservesTheCancellationWhereTheReplayEndsAndRecordsIt() => ActivationScheduler.RunAsync(async () =>
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var workflowId = TaskId.CreateRoot($"canceled-{Guid.NewGuid():N}");
        var pointId = workflowId.Child(DurableTaskGrainRuntime.CancellationPointSegment);
        var recordedPoints = new ConcurrentQueue<int>();
        using var subscription = DurableTaskEvents.AllEvents.Subscribe(new EventObserver(evt =>
        {
            if (evt is DurableTaskEvents.TaskCompleted { TaskId: var taskId } completed && taskId == pointId)
            {
                recordedPoints.Enqueue(completed.Response.GetResult<int>());
            }
        }));
        var probe = new CancellationProbe();
        await using var harness = new DurableTaskReplayHarness(grain => ObserveAtGateAsync(grain, probe, holdBeforeCompensation: true));
        await using (var activation = await harness.ActivateAsync(cancellationToken))
        {
            await activation.ScheduleAsync(workflowId, cancellationToken);
            await activation.Grain.AtGate.WaitAsync(CancellationPointTimeout, cancellationToken);

            // The request commits, and the body observes it and holds before any later write commits the point's record.
            await ((IDurableTaskManager)activation.Runtime).CancelAsync(workflowId, "stop", cancellationToken);
            await probe.Held.WaitAsync(CancellationPointTimeout, cancellationToken);
            Assert.Equal([2], recordedPoints);
        }

        Assert.Equal(["first:False", "second:False", "canceled:True"], probe.NextActivation());

        // The replay recovers the request without a point. It replays its recorded operations with an uncanceled token,
        // observes the cancellation as it reaches the last of them, and records that point.
        await using (var replay = await harness.ActivateAsync(cancellationToken))
        {
            await probe.Held.WaitAsync(CancellationPointTimeout, cancellationToken);
            Assert.Equal([2, 2], recordedPoints);
            probe.Release();
            Assert.Equal("compensated", (await replay.GetResponseAsync(workflowId, cancellationToken)).GetResult<string>());
        }

        Assert.Equal(["first:False", "second:False", "canceled:True"], probe.NextActivation());
        Assert.Equal(1, harness.EffectRuns);

        // The write which committed the result committed the replay's point too.
        await using var recovered = await harness.ActivateAsync(cancellationToken);
        Assert.Equal(2, GetCancellationPoint(recovered.Storage, workflowId));
    });

    [Fact]
    public Task CancellationPointIsNotARecordedOperationOfItsExecution() => ActivationScheduler.RunAsync(async () =>
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var workflowId = TaskId.CreateRoot($"canceled-{Guid.NewGuid():N}");
        var probe = new CancellationProbe();
        await using var harness = new DurableTaskReplayHarness(grain => ObserveAtGateAsync(grain, probe, holdBeforeCompensation: false));
        await using (var activation = await harness.ActivateAsync(cancellationToken))
        {
            await activation.ScheduleAsync(workflowId, cancellationToken);
            await activation.Grain.AtGate.WaitAsync(CancellationPointTimeout, cancellationToken);
            await ((IDurableTaskManager)activation.Runtime).CancelAsync(workflowId, "stop", cancellationToken);
            await probe.Held.WaitAsync(CancellationPointTimeout, cancellationToken);
            await activation.Grain.UnrelatedWrites;
            Assert.Equal(
                [workflowId.Child("compensate"), workflowId.Child("first"), workflowId.Child("second")],
                activation.Runtime.GetRecordedOperations(workflowId)!.OrderBy(static id => id.ToString(), StringComparer.Ordinal));
            _ = probe.NextActivation();
        }

        // A replay of the same code reaches every recorded operation, which ends its replay, and doesn't diverge.
        using var stalls = new StallRecorder(workflowId);
        await using var replay = await harness.ActivateAsync(cancellationToken);
        await probe.Held.WaitAsync(CancellationPointTimeout, cancellationToken);
        Assert.False(probe.HeldWhileReplaying);
        probe.Release();

        Assert.Equal("compensated", (await replay.GetResponseAsync(workflowId, cancellationToken)).GetResult<string>());
        Assert.False(stalls.Stalled.IsCompleted);
    });

    [Fact]
    public Task ReplayWhichEndsBeforeTheRecordedPointStallsAsDiverged() => ActivationScheduler.RunAsync(async () =>
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var workflowId = TaskId.CreateRoot($"canceled-{Guid.NewGuid():N}");
        var probe = new CancellationProbe();
        await using var harness = new DurableTaskReplayHarness(grain => ObserveAtGateAsync(grain, probe, holdBeforeCompensation: false));
        await using (var activation = await harness.ActivateAsync(cancellationToken))
        {
            await activation.ScheduleAsync(workflowId, cancellationToken);
            await activation.Grain.AtGate.WaitAsync(CancellationPointTimeout, cancellationToken);
            await ((IDurableTaskManager)activation.Runtime).CancelAsync(workflowId, "stop", cancellationToken);
            await probe.Held.WaitAsync(CancellationPointTimeout, cancellationToken);
            await activation.Grain.UnrelatedWrites;
            _ = probe.NextActivation();
        }

        // A later version returns after its first operation, so its replay never reaches point 2.
        var original = harness.Workflow;
        harness.Workflow = static _ => Changed();
        using (var stalls = new StallRecorder(workflowId))
        {
            await using var changed = await harness.ActivateAsync(cancellationToken);
            var stalled = await stalls.Stalled.WaitAsync(CancellationPointTimeout, cancellationToken);
            Assert.Equal(DurableTaskAttemptCancellationReason.Diverged, stalled.Reason);
            Assert.Contains("ended at cancellation point 1, before point 2", stalled.Exception.Message, StringComparison.Ordinal);
            Assert.True(changed.Storage.TryGetTask(workflowId, out var state));
            Assert.Null(state.CompletedAt);
        }

        // Code which reaches the point replays the execution.
        harness.Workflow = original;
        await using var replay = await harness.ActivateAsync(cancellationToken);
        await probe.Held.WaitAsync(CancellationPointTimeout, cancellationToken);
        probe.Release();
        Assert.Equal("compensated", (await replay.GetResponseAsync(workflowId, cancellationToken)).GetResult<string>());
        Assert.Equal(1, harness.EffectRuns);

        static async DurableTask<string> Changed()
        {
            DurableTask.RunInline("first", static () => 1);
            await Task.Yield();
            return "changed";
        }
    });

    /// <summary>
    /// Runs two effects, then waits at the grain's gate with the durable token, so it observes a cancellation which arrives
    /// there at point 2; compensates with an effect, and holds until the test releases it.
    /// </summary>
    private static async DurableTask<string> ObserveAtGateAsync(ReplayGrain grain, CancellationProbe probe, bool holdBeforeCompensation)
    {
        try
        {
            probe.Observe("first");
            DurableTask.RunInline("first", static () => 1);
            probe.Observe("second");
            DurableTask.RunInline("second", static () => 2);
            await grain.WaitAtGateAsync();
            return "completed";
        }
        catch (OperationCanceledException) when (DurableTask.Current.IsTaskCancellationRequested)
        {
            probe.Observe("canceled");
            if (holdBeforeCompensation)
            {
                await probe.HoldAsync();
            }

            await CompensateAsync(grain, probe, hold: !holdBeforeCompensation);
            return "compensated";
        }
    }

    /// <summary>Runs two effects, then parks at a signal wait which no one sends, its third point; then compensates.</summary>
    private static async DurableTask<string> ObserveAtSignalWaitAsync(ReplayGrain grain, CancellationProbe probe)
    {
        try
        {
            probe.Observe("first");
            DurableTask.RunInline("first", static () => 1);
            probe.Observe("second");
            DurableTask.RunInline("second", static () => 2);
            probe.Observe("wait");
            return await NeverSent.WaitAsync();
        }
        catch (OperationCanceledException) when (DurableTask.Current.IsTaskCancellationRequested)
        {
            probe.Observe("canceled");
            await CompensateAsync(grain, probe, hold: true);
            return "compensated";
        }
    }

    /// <summary>Records a compensating effect, requests a write which commits it, and optionally holds.</summary>
    private static Task CompensateAsync(ReplayGrain grain, CancellationProbe probe, bool hold)
    {
        DurableTask.RunInline("compensate", () =>
        {
            grain.Harness.CountEffectRun();
            return ++grain.Counter.Value;
        });
        grain.RequestUnrelatedWrite();
        return hold ? probe.HoldAsync() : Task.CompletedTask;
    }

    private static int? GetCancellationPoint(DurableTaskGrainStorage storage, TaskId executionId) =>
        storage.TryGetTask(executionId.Child(DurableTaskGrainRuntime.CancellationPointSegment), out var record)
            ? record.Result!.GetResult<int>()
            : null;

    private static TaskId[] GetRecordIds(DurableTaskGrainStorage storage, TaskId executionId) =>
        [.. storage.GetChildren(executionId).Select(static child => child.Id).OrderBy(static id => id.ToString(), StringComparer.Ordinal)];

    /// <summary>
    /// Records what one activation's attempt observed of its durable token before each step, and holds the attempt after
    /// it compensates, until the test releases it or the activation ends.
    /// </summary>
    private sealed class CancellationProbe
    {
        private readonly List<string> _observed = [];
        private TaskCompletionSource _atWait = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once the attempt is about to wait for its signal.</summary>
        public Task AtWait => _atWait.Task;

        /// <summary>Completes once the attempt holds.</summary>
        public Task Held => _held.Task;

        /// <summary>Gets whether the attempt still replayed when it held.</summary>
        public bool HeldWhileReplaying { get; private set; }

        public void Observe(string step)
        {
            _observed.Add($"{step}:{DurableTask.Current.CancellationToken.IsCancellationRequested}");
            if (step == "wait")
            {
                _atWait.TrySetResult();
            }
        }

        /// <summary>Holds the attempt until <see cref="Release"/>, or until the attempt is canceled.</summary>
        public Task HoldAsync()
        {
            HeldWhileReplaying = DurableTask.Current.IsReplaying;
            _held.TrySetResult();
            return _release.Task.WaitAsync(DurableTask.Current.AttemptCancellationToken);
        }

        public void Release() => _release.TrySetResult();

        /// <summary>Returns what the ended activation's attempt observed, and resets the probe for the next activation.</summary>
        public string[] NextActivation()
        {
            string[] observed = [.. _observed];
            _observed.Clear();
            _atWait = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return observed;
        }
    }
}
