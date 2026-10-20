using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks.Runtime;
using Orleans.Runtime;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.Tests;

/// <summary>
/// Record kinds: a replay which requests another kind of operation under a recorded identifier diverges, which stalls its
/// attempt, rather than observing or changing the recorded operation.
/// </summary>
public sealed partial class DurableTaskRuntimeInvariantTests
{
    [Fact]
    public Task DelayWhereAStepWasRecordedStallsAsDiverged() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, _, transport) = CreateRuntime();
        var taskId = TaskId.Parse("root/local");
        var waitId = taskId.Child("wait");
        storage.AddInvoked(TaskId.Parse("root"));
        var step = storage.GetOrCreate(waitId, DurableTaskRecordKind.LocalStep);
        storage.SetResponse(waitId, step, DurableTaskResponse.FromResult(1));
        using var stalls = new StallRecorder(taskId);

        // The workflow changed: a replay requests a delay where the first execution recorded a step's outcome.
        _ = await runtime.ScheduleChildAsync(
            taskId,
            DurableTask.Run(async () => await DurableTask.Delay(TimeSpan.FromDays(1)).WithId("wait")),
            TestContext.Current.CancellationToken);

        var stalled = await stalls.Stalled.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(DurableTaskAttemptCancellationReason.Diverged, stalled.Reason);
        Assert.Contains("It records a local child task, and this attempt requested a delay.", stalled.Exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, storage.Get(waitId).Result!.GetResult<int>());
        Assert.False(storage.Get(taskId).Result is { IsCompleted: true });
        Assert.Empty(transport.Resumes);
    });

    [Fact]
    public Task StepWhereADelayWasRecordedStallsAsDiverged() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, _, transport) = CreateRuntime();
        var taskId = TaskId.Parse("root/local");
        var waitId = taskId.Child("wait");
        storage.AddInvoked(TaskId.Parse("root"));
        await runtime.ScheduleDelayAsync(waitId, TimeSpan.FromDays(1), TestContext.Current.CancellationToken);
        var dueTime = storage.Get(waitId).DueTime;
        using var stalls = new StallRecorder(taskId);

        // The workflow changed: a replay requests a step where the first execution recorded a pending delay.
        _ = await runtime.ScheduleChildAsync(
            taskId,
            DurableTask.Run(async () => await DurableTask.Run(static _ => 1).WithId("wait")),
            TestContext.Current.CancellationToken);

        var stalled = await stalls.Stalled.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(DurableTaskAttemptCancellationReason.Diverged, stalled.Reason);
        Assert.Contains($"It records a delay due at {dueTime:O}, and this attempt requested a local child task.", stalled.Exception.Message, StringComparison.Ordinal);
        Assert.Equal(DurableTaskRecordKind.Delay, storage.Get(waitId).Kind);
        Assert.Equal(dueTime, storage.Get(waitId).DueTime);
        Assert.Null(storage.Get(waitId).Result);
        Assert.Single(transport.Resumes);
    });

    [Fact]
    public Task DelayAtAPendingCallRecordStallsWithoutChangingIt() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, transport) = CreateRuntime();
        var taskId = TaskId.Parse("root/local");
        var callId = taskId.Child("wait");
        var target = GrainId.Create("target", "one");
        storage.AddInvoked(TaskId.Parse("root"));
        storage.SetRemoteRequest(callId, storage.GetOrCreate(callId, DurableTaskRecordKind.RemoteCall), target, "0123456789ABCDEF0123");

        // A delay which an execution schedules under its child's identifier finds the call's record and changes nothing.
        var direct = await Assert.ThrowsAsync<DurableTaskDivergenceException>(
            () => runtime.ScheduleDelayAsync(callId, TimeSpan.FromDays(1), TestContext.Current.CancellationToken).AsTask());
        Assert.Contains($"It records a call to grain '{target}' (fingerprint 0123456789AB), and this attempt requested a delay due at", direct.Message, StringComparison.Ordinal);

        // A replay which awaits a delay under the call's identifier stalls.
        using var stalls = new StallRecorder(taskId);
        _ = await runtime.ScheduleChildAsync(
            taskId,
            DurableTask.Run(async () => await DurableTask.Delay(TimeSpan.FromDays(1)).WithId("wait")),
            TestContext.Current.CancellationToken);

        var stalled = await stalls.Stalled.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(DurableTaskAttemptCancellationReason.Diverged, stalled.Reason);
        var call = storage.Get(callId);
        Assert.Equal((DurableTaskRecordKind.RemoteCall, target), (call.Kind, call.RemoteTarget));
        Assert.Null(call.DueTime);
        Assert.Equal(0, call.ResumeGeneration);
        Assert.Null(call.Result);
        Assert.Empty(transport.Resumes);
    });

    [Fact]
    public Task PendingStepWithACancellationRequestIsNotAnEarlyCancellationRecord() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var rootId = TaskId.Parse("root");
        var stepId = rootId.Child("step");
        storage.AddInvoked(rootId);
        var step = storage.GetOrCreate(stepId, DurableTaskRecordKind.LocalStep);
        storage.RequestCancellation(stepId, step);

        // The step is an operation of its execution, which a replay reaches, and receives no signals as an invoked task.
        Assert.Equal([stepId], runtime.GetRecordedOperations(rootId));
        Assert.IsType<DurableTaskNotFoundException>(runtime.StageSignalFromInbox(stepId, "approved", DurableTaskResponse.FromResult("ada")));
        Assert.False(storage.Contains(stepId.Child("$signal:approved")));

        // A cancellation which arrives before its invocation is recorded as such, and its invocation's signal precedes it.
        var earlyId = TaskId.CreateRoot("early");
        runtime.SignalCancellationFromInbox(earlyId, GrainId.Create("caller", "one"), reason: null);
        Assert.Equal(DurableTaskRecordKind.EarlyCancellation, storage.Get(earlyId).Kind);
        Assert.Null(runtime.StageSignalFromInbox(earlyId, "approved", DurableTaskResponse.FromResult("ada")));
        Assert.True(storage.Contains(earlyId.Child("$signal:approved")));
        Assert.Null(runtime.GetRecordedOperations(earlyId));
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
    });
}
