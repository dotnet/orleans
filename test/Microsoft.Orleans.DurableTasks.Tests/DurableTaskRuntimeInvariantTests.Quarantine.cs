using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks.Runtime;
using Orleans.Journaling;
using Orleans.Messaging;
using Orleans.Runtime;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.Tests;

/// <summary>
/// Per-task failures: a stored request which no longer decodes quarantines only its task, and a cancellation which names
/// no task stores nothing unless a caller's cancellation of its child overtakes the child's invocation.
/// </summary>
public sealed partial class DurableTaskRuntimeInvariantTests
{
    [Fact]
    public Task RecoveredRequestWhichNoLongerDecodesStallsOnlyItsTask() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var stateMachine = (IStateMachine)runtime;
        var undecodableId = TaskId.CreateRoot("undecodable");
        var otherId = TaskId.CreateRoot("other");
        var undecodableRequest = CreateRequest(1, () => DurableTask.FromResult(7));
        var otherRequest = CreateRequest(2, () => DurableTask.FromResult(8));
        stateMachine.Reset(default);
        var undecodable = AddRecoveredRequest(undecodableId, undecodableRequest);
        undecodable.RequestDecodeFailure = new InvalidOperationException("The request's type was removed.");
        AddRecoveredRequest(otherId, otherRequest);
        stateMachine.OnRecoveryCompleted();
        using var stalls = new StallRecorder(undecodableId);

        // Activation decodes no request, so it recovers, and the other task runs to completion.
        await runtime.ResumePendingTasksAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => storage.Get(otherId).Result is { IsCompleted: true });
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(8, storage.Get(otherId).Result!.GetResult<int>());

        // The task whose request doesn't decode stalls, keeps its stored request, and polls report it as pending.
        var stalled = await stalls.Stalled.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(DurableTaskAttemptCancellationReason.Diverged, stalled.Reason);
        Assert.Contains($"The request which durable task '{undecodableId}' stores no longer decodes", stalled.Exception.Message, StringComparison.Ordinal);
        Assert.Contains("The request's type was removed.", stalled.Exception.Message, StringComparison.Ordinal);
        Assert.Null(undecodable.Result);
        Assert.True(undecodable.HasRequest);
        Assert.Equal(0, undecodableRequest.CreateTaskCallCount);
        Assert.Equal(
            DurableTaskStatus.Pending,
            (await runtime.SubscribeOrPollAsync(undecodableId, new SubscribeOrPollOptions { PollTimeout = TimeSpan.Zero }, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(TimeSpan.FromMinutes(1), ((IInboxWorkSource)runtime).GetWakeupDelay());
        await runtime.StopAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // A later activation whose code decodes the request runs the task.
        undecodable.RequestDecodeFailure = null;
        var (replacement, _, _, _) = CreateRuntime(storage, manager);
        await replacement.ResumePendingTasksAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => undecodable.Result is { IsCompleted: true });
        Assert.Equal(7, undecodable.Result!.GetResult<int>());
        await replacement.StopAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        TestTaskState AddRecoveredRequest(TaskId taskId, RuntimeTestDurableTaskRequest request)
        {
            var state = storage.GetOrCreate(taskId, DurableTaskRecordKind.Invocation);
            storage.SetRequest(taskId, state, request);
            storage.SetRequestFingerprint(taskId, state, IDurableTaskRequest.GetFingerprint(request, CreateSerializer()), IDurableTaskRequest.GetMethod(request));
            storage.SetIncarnation(taskId, state, Guid.NewGuid());
            return state;
        }
    });

    [Fact]
    public Task DirectCancellationOfAnUnknownTaskThrowsNotFoundAndStoresNothing() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var missing = TaskId.CreateRoot("missing");

        var fromClient = await Assert.ThrowsAsync<DurableTaskNotFoundException>(
            () => ((IDurableTaskServer)runtime).CancelAsync(missing, "client", TestContext.Current.CancellationToken).AsTask());
        var fromManager = await Assert.ThrowsAsync<DurableTaskNotFoundException>(
            () => ((IDurableTaskManager)runtime).CancelAsync(missing, "grain", TestContext.Current.CancellationToken).AsTask());

        Assert.Equal((missing, missing), (fromClient.TaskId, fromManager.TaskId));
        Assert.Empty(storage.Tasks);
        Assert.Equal(0, manager.WriteCount);
    });

    [Fact]
    public Task InboxCancellationOfACallersChildWhichHasNotArrivedIsRecorded() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var caller = GrainId.Create("caller", "one");
        var childId = TaskId.Parse("starter/call");
        var storedId = CallerScope.GetTaskId(caller, childId);

        // The caller's cancellation of its child can overtake the child's invocation, so the inbox records it.
        Assert.True(CreateHandler(runtime).TryAccept(CancellationMessage(caller, runtime.GrainId, childId, "caller"), out var result));
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DeliveryStatus.Accepted, result.Status);
        var record = storage.Get(storedId);
        Assert.Equal(DurableTaskRecordKind.EarlyCancellation, record.Kind);
        Assert.Equal(caller, record.CallerId);
        Assert.Equal("caller", record.CancellationReason);
        Assert.False(record.HasRequest);
    });
}
