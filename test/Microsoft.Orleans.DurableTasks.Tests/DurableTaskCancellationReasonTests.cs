using System.Text.Json;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks.Runtime;
using Orleans.DurableTasks.Storage;
using Orleans.Runtime;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.Tests;

/// <summary>Cancellation reasons, root-identifier manager overloads, and signal results.</summary>
public sealed partial class DurableTaskRuntimeInvariantTests
{
    private static readonly DurableSignal<string> ShippedSignal = new("shipped");

    [Fact]
    public Task CancellationReasonReachesTheBodyAndItsLocalChildren() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var tasks = (IDurableTaskManager)runtime;
        var rootId = TaskId.CreateRoot("order");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.ScheduleFromInbox(rootId, CreateRequest(1, () => DurableTask.Run(async _ =>
        {
            var childReason = await DurableTask.Run(async cancellationToken =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return "completed";
                }
                catch (OperationCanceledException) when (DurableExecutionContext.Current!.IsCancellationRequested)
                {
                    return DurableExecutionContext.Current.CancellationReason;
                }
            }).WithId("work");
            return $"{DurableExecutionContext.Current!.CancellationReason}|{childReason}";
        })));
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(CancellationTestTimeout, TestContext.Current.CancellationToken);

        await tasks.GetTask<string>("order").CancelAsync("Cancelled by the buyer", TestContext.Current.CancellationToken);
        await tasks.CancelAsync("order", "late", TestContext.Current.CancellationToken);

        Assert.Equal("Cancelled by the buyer|Cancelled by the buyer", (await WaitForResponseAsync(runtime, rootId)).GetResult<string>());
        Assert.Equal("Cancelled by the buyer", storage.Get(rootId).CancellationReason);
        Assert.Equal("Cancelled by the buyer", storage.Get(rootId.Child("work")).CancellationReason);
    });

    [Fact]
    public Task CancellationWithoutReasonRecordsNoReason() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, _, _) = CreateRuntime();
        var rootId = TaskId.CreateRoot("order");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = CreateRequest(1, () => DurableTask.Run(async _ =>
        {
            await BlockedChild(started).WithId("work");
            return 0;
        }));
        await ((IDurableTaskServer)runtime).ScheduleAsync(rootId, request, TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(CancellationTestTimeout, TestContext.Current.CancellationToken);

        await ((IDurableTaskManager)runtime).CancelAsync("order", TestContext.Current.CancellationToken);
        await ((IDurableTaskManager)runtime).CancelAsync(rootId, "after the first request", TestContext.Current.CancellationToken);

        Assert.Equal(DurableTaskStatus.Canceled, (await WaitForResponseAsync(runtime, rootId)).Status);
        Assert.NotNull(storage.Get(rootId).CancellationRequestedAt);
        Assert.Null(storage.Get(rootId).CancellationReason);
        Assert.Null(storage.Get(rootId.Child("work")).CancellationReason);
    });

    [Fact]
    public Task DiagnosticsReportTheCancellationReason() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, _, _, _) = CreateRuntime();
        var rootId = TaskId.CreateRoot("order");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = CreateRequest(1, () => DurableTask.Run(async cancellationToken =>
        {
            started.TrySetResult();
            await release.Task;
            cancellationToken.ThrowIfCancellationRequested();
            return 0;
        }));
        await ((IDurableTaskServer)runtime).ScheduleAsync(rootId, request, TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(CancellationTestTimeout, TestContext.Current.CancellationToken);

        await ((IDurableTaskServer)runtime).CancelAsync(rootId, "operator", TestContext.Current.CancellationToken);
        var canceling = await GetDiagnosticsAsync(runtime, rootId);
        release.SetResult();
        Assert.Equal(DurableTaskStatus.Canceled, (await WaitForResponseAsync(runtime, rootId)).Status);
        var canceled = await GetDiagnosticsAsync(runtime, rootId);

        Assert.Equal(("Canceling", "operator"), (canceling.Status, canceling.Response));
        Assert.Equal(("Canceled", "operator"), (canceled.Status, canceled.Response));
    });

    [Fact]
    public Task CancellationReasonReachesARemoteChild() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, _, transport) = CreateRuntime();
        var rootId = TaskId.Parse("root");
        var childId = TaskId.Parse("root/remote");
        storage.GetOrCreate(rootId);
        _ = await runtime.ScheduleChildAsync(childId, new TestStateManager.TestRemoteDurableTask(CreateRemoteRequest(1)), TestContext.Current.CancellationToken);

        await ((IDurableTaskManager)runtime).CancelAsync(rootId, "buyer", TestContext.Current.CancellationToken);

        Assert.Equal(childId, Assert.Single(transport.Cancellations).TaskId);
        Assert.Equal("buyer", Assert.Single(transport.CancellationReasons));
        Assert.Equal("buyer", storage.Get(childId).CancellationReason);
    });

    [Fact]
    public Task WorkflowCancelsItsRemoteChildWithAReason() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, transport) = CreateRuntime();
        var rootId = TaskId.CreateRoot("order");
        var childId = rootId.Child("remote");
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.ScheduleFromInbox(rootId, CreateRequest(1, () => DurableTask.Run(async cancellationToken =>
        {
            var remote = await new TestStateManager.TestRemoteDurableTask(CreateRemoteRequest(2)).WithId("remote").ScheduleAsync(cancellationToken);
            await remote.CancelAsync("no longer needed", cancellationToken);
            canceled.TrySetResult();
            return 0;
        })));
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await canceled.Task.WaitAsync(CancellationTestTimeout, TestContext.Current.CancellationToken);

        Assert.Equal(childId, Assert.Single(transport.Cancellations).TaskId);
        Assert.Equal("no longer needed", Assert.Single(transport.CancellationReasons));
        Assert.Equal("no longer needed", storage.Get(childId).CancellationReason);
        Assert.Null(storage.Get(rootId).CancellationReason);
    });

    [Fact]
    public Task InboxCancellationCarriesItsReasonToTheBody() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var caller = GrainId.Create("caller", "one");
        var taskId = TaskId.Parse("invoked");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.ScheduleFromInbox(taskId, CreateRequest(1, () => DurableTask.Run(async cancellationToken =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return "completed";
            }
            catch (OperationCanceledException)
            {
                return DurableExecutionContext.Current!.CancellationReason;
            }
        })));
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(CancellationTestTimeout, TestContext.Current.CancellationToken);

        Accept(CreateHandler(runtime), MessagingTestHelpers.CreateMessage(
            MessagingTestHelpers.MessageTypes.Cancellation,
            caller,
            runtime.GrainId,
            taskId,
            new DurableTaskCancellationMessage { TaskId = taskId, Sender = caller, CancellationReason = "caller canceled" }));
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);

        Assert.Equal("caller canceled", (await WaitForResponseAsync(runtime, taskId)).GetResult<string>());
        Assert.Equal("caller canceled", storage.Get(taskId).CancellationReason);
    });

    [Fact]
    public Task RecoveryReplaysCanceledBodyWithItsRecordedReason() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var rootId = TaskId.Parse("root");
        var attempts = 0;
        var firstAttemptBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = CreateRequest(1, () => DurableTask.Run(async _ =>
        {
            var context = DurableExecutionContext.Current!;
            if (Interlocked.Increment(ref attempts) == 1)
            {
                firstAttemptBlocked.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
            }

            return context.CancellationReason;
        }));
        await ((IDurableTaskServer)runtime).ScheduleAsync(rootId, request, TestContext.Current.CancellationToken);
        await firstAttemptBlocked.Task.WaitAsync(CancellationTestTimeout, TestContext.Current.CancellationToken);
        await runtime.StopAsync(TestContext.Current.CancellationToken).WaitAsync(CancellationTestTimeout, TestContext.Current.CancellationToken);

        storage.RequestCancellation(rootId, storage.Get(rootId), "recorded");
        var (replacement, _, _, _) = CreateRuntime(storage, manager);
        await replacement.ResumePendingTasksAsync(TestContext.Current.CancellationToken);

        Assert.Equal("recorded", (await WaitForResponseAsync(replacement, rootId)).GetResult<string>());
        Assert.Equal(2, Volatile.Read(ref attempts));
    });

    [Fact]
    public void CancellationMessageCarriesItsReasonUnderTheCancellationIdentity()
    {
        var outbox = new RecordingOutbox();
        var transport = MessagingTestHelpers.CreateTransport(outbox);
        var sender = GrainId.Create("caller", "one");
        var target = GrainId.Create("target", "one");
        var taskId = TaskId.Parse("root");

        transport.SendCancellation(sender, target, taskId);
        transport.SendCancellation(sender, target, taskId, "buyer");

        var messages = outbox.Messages.ToArray();
        Assert.Equal(messages[0].Envelope.MessageId, messages[1].Envelope.MessageId);
        Assert.Null(MessagingTestHelpers.MessageTypes.Cancellation.Decode(messages[0].Envelope).CancellationReason);
        var cancellation = MessagingTestHelpers.MessageTypes.Cancellation.Decode(messages[1].Envelope);
        Assert.Equal(("buyer", (string?)null), (cancellation.CancellationReason, cancellation.AbortReason));
    }

    [Fact]
    public void JsonTaskStateStoresTheCancellationReasonOnlyWhenGiven()
    {
        var typeInfo = DurableTaskJsonContext.Default.DurableTaskState;
        var withReason = new DurableTaskState { CancellationRequestedAt = DateTimeOffset.UnixEpoch, CancellationReason = "buyer" };
        var withoutReason = new DurableTaskState { CancellationRequestedAt = DateTimeOffset.UnixEpoch };

        var json = JsonSerializer.Serialize(withReason, typeInfo);

        Assert.Equal("buyer", JsonSerializer.Deserialize(json, typeInfo)!.CancellationReason);
        Assert.DoesNotContain(nameof(DurableTaskState.CancellationReason), JsonSerializer.Serialize(withoutReason, typeInfo), StringComparison.Ordinal);
    }

    [Fact]
    public Task ManagerRootIdentifierOverloadsAddressRootTasks() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var tasks = (IDurableTaskManager)runtime;
        var rootId = TaskId.CreateRoot("order/1");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.ScheduleFromInbox(rootId, CreateRequest(1, () => DurableTask.Run(async _ =>
        {
            started.TrySetResult();
            return await ShippedSignal.WaitAsync();
        })));
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(CancellationTestTimeout, TestContext.Current.CancellationToken);

        Assert.Equal(rootId, tasks.GetTask("order/1").Id);
        Assert.Equal(rootId, tasks.GetTask<string>("order/1").Id);
        await tasks.SignalAsync("order/1", ShippedSignal, "parcel", TestContext.Current.CancellationToken);
        Assert.Equal("parcel", (await WaitForResponseAsync(runtime, rootId)).GetResult<string>());
        Assert.Equal(DurableSignalResult.Duplicate, await tasks.TrySignalAsync("order/1", ShippedSignal, "parcel", TestContext.Current.CancellationToken));

        runtime.ScheduleFromInbox(TaskId.CreateRoot("stuck"), CreateRequest(2, () => DurableTask.Run(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        })));
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await tasks.AbortAsync("stuck", "operator", TestContext.Current.CancellationToken);
        AssertAborted(storage.Get(TaskId.CreateRoot("stuck")).Result, "operator");
        Assert.Throws<ArgumentException>(() => tasks.GetTask(string.Empty));
        await runtime.StopAsync(TestContext.Current.CancellationToken).WaitAsync(CancellationTestTimeout, TestContext.Current.CancellationToken);
    });

    [Fact]
    public Task TrySignalReportsEachOutcomeWithoutThrowing() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var tasks = (IDurableTaskManager)runtime;
        var rootId = TaskId.CreateRoot("order");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.ScheduleFromInbox(rootId, CreateRequest(1, () => DurableTask.Run(async _ =>
        {
            await release.Task;
            return 0;
        })));
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        var writes = manager.WriteCount;

        Assert.Equal(DurableSignalResult.Accepted, await tasks.TrySignalAsync(rootId, ShippedSignal, "parcel", TestContext.Current.CancellationToken));
        Assert.Equal(DurableSignalResult.Duplicate, await tasks.TrySignalAsync(rootId, ShippedSignal, "parcel", TestContext.Current.CancellationToken));
        var acceptedWrites = manager.WriteCount;
        Assert.Equal(DurableSignalResult.Conflict, await tasks.TrySignalAsync(rootId, ShippedSignal, "other", TestContext.Current.CancellationToken));
        Assert.Equal(
            DurableSignalResult.TaskNotFound,
            await ((IDurableTaskGrainExtension)runtime).TrySignalAsync(TaskId.CreateRoot("missing"), ShippedSignal.Name, DurableTaskResponse.FromResult("parcel"), TestContext.Current.CancellationToken));
        release.SetResult();
        await WaitForResponseAsync(runtime, rootId);
        Assert.Equal(DurableSignalResult.TaskCompleted, await tasks.TrySignalAsync(rootId, ShippedSignal.For("again"), "parcel", TestContext.Current.CancellationToken));

        // Each accepted or duplicate signal commits; a rejection changes nothing.
        Assert.Equal(writes + 2, acceptedWrites);
        Assert.Equal("parcel", storage.Get(rootId.Child("$signal:shipped")).Result!.GetResult<string>());
        Assert.False(storage.Contains(rootId.Child("$signal:shipped/again")));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => tasks.SignalAsync(rootId, ShippedSignal, "other", TestContext.Current.CancellationToken).AsTask());
    });

    [Fact]
    public Task TrySignalInADurableExecutionReplaysItsRecordedResult() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var current = runtime;
        var rootId = TaskId.CreateRoot("workflow");
        var targetId = TaskId.CreateRoot("target");
        var attempts = 0;
        var firstAttemptBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var results = new List<DurableSignalResult>();
        var request = CreateRequest(1, () => DurableTask.Run(async cancellationToken =>
        {
            var context = DurableExecutionContext.Current!;
            var result = await ((IDurableTaskManager)Volatile.Read(ref current)).TrySignalAsync(targetId, ShippedSignal, "parcel", cancellationToken);
            lock (results)
            {
                results.Add(result);
            }

            if (Interlocked.Increment(ref attempts) == 1)
            {
                firstAttemptBlocked.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
            }

            return (int)result;
        }));
        await ((IDurableTaskServer)runtime).ScheduleAsync(rootId, request, TestContext.Current.CancellationToken);
        await firstAttemptBlocked.Task.WaitAsync(CancellationTestTimeout, TestContext.Current.CancellationToken);
        await runtime.StopAsync(TestContext.Current.CancellationToken).WaitAsync(CancellationTestTimeout, TestContext.Current.CancellationToken);

        // The target exists by the replay, which returns the recorded result instead of signaling it.
        var target = storage.GetOrCreateTask(targetId, CreateRequest(2));
        storage.SetRequestFingerprint(targetId, target, "target-fingerprint");
        var (replacement, _, _, _) = CreateRuntime(storage, manager);
        Volatile.Write(ref current, replacement);
        await replacement.ResumePendingTasksAsync(TestContext.Current.CancellationToken);

        Assert.Equal((int)DurableSignalResult.TaskNotFound, (await WaitForResponseAsync(replacement, rootId)).GetResult<int>());
        Assert.Equal([DurableSignalResult.TaskNotFound, DurableSignalResult.TaskNotFound], results);
        Assert.False(storage.Contains(targetId.Child("$signal:shipped")));
        await replacement.StopAsync(TestContext.Current.CancellationToken).WaitAsync(CancellationTestTimeout, TestContext.Current.CancellationToken);
    });
}
