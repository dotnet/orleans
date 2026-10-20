using Orleans.CodeGeneration;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks.Runtime;
using Orleans.Messaging;
using Orleans.Runtime;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.Tests;

/// <summary>
/// Operation identity: a replay, or a retry of a root task, is the same operation as a record when it has the record's
/// kind and identifier and invokes the same method on the same target. Arguments aren't compared, so a deployment which
/// changes how an argument serializes replays the same operations.
/// </summary>
public sealed partial class DurableTaskRuntimeInvariantTests
{
    [Fact]
    public Task ReplayedCallWithOtherArgumentsReusesTheRecordedCall() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, transport) = CreateRuntime();
        var taskId = TaskId.Parse("root/local");
        var callId = taskId.Child("charge");
        storage.AddInvoked(TaskId.Parse("root"));
        var recordedCall = CreateRemoteChild(1);
        var recorded = storage.GetOrCreate(callId, DurableTaskRecordKind.RemoteCall);
        storage.SetRemoteRequest(
            callId,
            recorded,
            recordedCall.Context!.TargetId,
            IDurableTaskRequest.GetMethod(recordedCall),
            IDurableTaskRequest.GetFingerprint(recordedCall, CreateSerializer()));
        storage.SetResponse(callId, recorded, DurableTaskResponse.FromResult(42));
        var fingerprint = recorded.RemoteRequestFingerprint;
        using var stalls = new StallRecorder(taskId);

        // A deployment changed how the call's argument serializes: the replay calls the same method on the same grain.
        var handle = await runtime.ScheduleChildAsync(
            taskId,
            DurableTask.Run(async () => await CreateRemoteChild(2).WithId("charge")),
            TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => storage.Get(taskId).Result is { IsCompleted: true });
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, (await handle.WaitAsync(TestContext.Current.CancellationToken)).Status);
        Assert.False(stalls.Stalled.IsCompleted);
        Assert.Empty(transport.Invocations);
        Assert.Equal(fingerprint, storage.Get(callId).RemoteRequestFingerprint);
        Assert.Equal(42, storage.Get(callId).Result!.GetResult<int>());
    });

    [Fact]
    public Task ReplayedPendingCallWithOtherArgumentsCoalescesWithItsInvocation() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, transport) = CreateRuntime();
        var starterId = TaskId.Parse("starter");
        var childId = starterId.Child("call");
        var argument = 1;
        var request = CreateRequest(
            1,
            () => DurableTask.Run(async _ => await CreateRemoteChild(argument).WithId("call")));

        runtime.ScheduleFromInbox(starterId, request);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => transport.SentInvocations.Count == 1);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        var messageId = Assert.Single(transport.SentInvocations);
        var fingerprint = storage.Get(childId).RemoteRequestFingerprint;

        // A replacement activation replays the call with an argument which serializes differently. The call reaches its
        // record and coalesces with the invocation which the table holds, whose journaled request is kept.
        await runtime.StopAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        argument = 2;
        var (replacement, _, _, replacementTransport) = CreateRuntime(storage, manager);
        using var stalls = new StallRecorder(starterId);
        await replacement.ResumePendingTasksAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => replacementTransport.Invocations.Count == 1);

        Assert.Equal(2, replacementTransport.Invocations[0].Request.GetArgument(0));
        Assert.Empty(replacementTransport.SentInvocations);
        Assert.True(storage.Invocations.ContainsKey(messageId));
        Assert.Equal(fingerprint, storage.Get(childId).RemoteRequestFingerprint);
        Assert.False(stalls.Stalled.IsCompleted);
        await replacement.StopAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    });

    [Fact]
    public Task ReplayedCallToAnotherTargetStallsTheAttempt() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, _, transport) = CreateRuntime();
        var taskId = TaskId.Parse("root/local");
        var callId = taskId.Child("charge");
        storage.AddInvoked(TaskId.Parse("root"));
        var recordedCall = CreateRemoteChild(1);
        storage.SetRemoteRequest(
            callId,
            storage.GetOrCreate(callId, DurableTaskRecordKind.RemoteCall),
            recordedCall.Context!.TargetId,
            IDurableTaskRequest.GetMethod(recordedCall),
            IDurableTaskRequest.GetFingerprint(recordedCall, CreateSerializer()));
        var otherTarget = CreateRemoteRequest(1);
        otherTarget.Context!.TargetId = GrainId.Create("target", "two");
        using var stalls = new StallRecorder(taskId);

        // The same method on another grain is another operation.
        _ = await runtime.ScheduleChildAsync(
            taskId,
            DurableTask.Run(async () => await new TestStateManager.TestRemoteDurableTask(otherTarget).WithId("charge")),
            TestContext.Current.CancellationToken);

        var stalled = await stalls.Stalled.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(DurableTaskAttemptCancellationReason.Diverged, stalled.Reason);
        Assert.Contains("on grain 'target/two'", stalled.Exception.Message, StringComparison.Ordinal);
        Assert.Null(storage.Get(callId).Result);
        Assert.Empty(transport.Invocations);
    });

    [Fact]
    public Task ReplayedDetachedStartWithOtherArgumentsReusesTheRecordAndAnotherMethodDiverges() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, transport) = CreateRuntime();
        storage.AddInvoked(TaskId.Parse("starter"));
        var recordId = TaskId.Parse("starter/$detach:target:first");
        var rootId = TaskId.CreateRoot("first");
        runtime.PostDetached(recordId, rootId, CreateRemoteRequest(1), shielded: false);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        var fingerprint = storage.Get(recordId).RequestFingerprint;

        runtime.PostDetached(recordId, rootId, CreateRemoteRequest(2), shielded: false);
        Assert.Single(transport.DetachedInvocations);
        Assert.Equal(fingerprint, storage.Get(recordId).RequestFingerprint);

        var exception = Assert.Throws<DurableTaskDivergenceException>(
            () => runtime.PostDetached(recordId, rootId, CreateOtherMethodRequest(1, GrainId.Create("target", "one")), shielded: false));
        Assert.Contains("different operation", exception.Message, StringComparison.Ordinal);
        Assert.Single(transport.DetachedInvocations);
    });

    [Fact]
    public Task RootScheduledAgainWithOtherArgumentsReturnsTheStoredTask() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, _, _) = CreateRuntime();
        var rootId = TaskId.CreateRoot("order");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = CreateRequest(1, () => DurableTask.Run(async _ => await release.Task));
        await ((IDurableTaskServer)runtime).ScheduleAsync(rootId, first, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => first.CreateTaskCallCount == 1);
        var fingerprint = storage.Get(rootId).RequestFingerprint;

        // A client retry of the same method under the same identifier reaches the stored task, whatever its arguments.
        var retry = CreateRequest(2);
        var response = await ((IDurableTaskServer)runtime).ScheduleAsync(rootId, retry, TestContext.Current.CancellationToken);

        Assert.Equal(DurableTaskStatus.Pending, response.Status);
        Assert.Equal(0, retry.CreateTaskCallCount);
        Assert.Same(first, storage.Get(rootId).Request);
        Assert.Equal(fingerprint, storage.Get(rootId).RequestFingerprint);

        // Another method under the same identifier conflicts.
        var conflict = await Assert.ThrowsAsync<DurableTaskConflictException>(
            async () => await ((IDurableTaskServer)runtime).ScheduleAsync(rootId, CreateOtherMethodRequest(1), TestContext.Current.CancellationToken));
        Assert.Contains($"Durable task '{rootId}' is already associated with a different request.", conflict.Message, StringComparison.Ordinal);
        Assert.Equal(rootId, conflict.TaskId);

        release.SetResult();
        await WaitUntilAsync(() => storage.Get(rootId).Result is { IsCompleted: true });
        Assert.Equal(1, first.CreateTaskCallCount);
        await runtime.StopAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    });

    [Fact]
    public Task InboxInvocationOfTheStoredMethodIsAcceptedAndAnotherMethodConflicts() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var sender = GrainId.Create("caller", "one");
        var taskId = TaskId.Parse("starter/call");
        var storedId = CallerScope.GetTaskId(sender, taskId);
        var handler = CreateHandler(runtime);
        Assert.True(handler.TryAccept(Invocation(CreateSerializableRequest(runtime.GrainId)), out var first));
        Assert.Equal(DeliveryStatus.Accepted, first.Status);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        var fingerprint = storage.Get(storedId).RequestFingerprint;

        // A redelivery whose request serializes differently, here with other invocation options, invokes the same method.
        var reserialized = CreateSerializableRequest(runtime.GrainId);
        reserialized.AddInvokeMethodOptions(InvokeMethodOptions.OneWay);
        Assert.True(handler.TryAccept(Invocation(reserialized), out var redelivered));
        Assert.Equal(DeliveryStatus.Accepted, redelivered.Status);
        Assert.Equal(fingerprint, storage.Get(storedId).RequestFingerprint);

        // An invocation of another method under the caller's identifier conflicts, and the inbox dead-letters it.
        var other = new TestOtherMethodDurableTaskRequest
        {
            Context = new DurableTaskRequestContext { CallerId = sender, TargetId = runtime.GrainId, SupportsDurableCompletion = true },
        };
        Assert.True(handler.TryAccept(Invocation(other), out var conflict));
        Assert.Equal((DeliveryStatus.DeadLettered, DurableTaskRejectionCodes.Conflict), (conflict.Status, conflict.Code));
        Assert.Contains($"Durable task '{storedId}' is already associated with a different request.", conflict.Message, StringComparison.Ordinal);
        Assert.Equal(fingerprint, storage.Get(storedId).RequestFingerprint);
        await runtime.StopAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        InboxMessage Invocation(IDurableTaskRequest request) =>
            MessagingTestHelpers.CreateInvocationMessage(
                sender,
                runtime.GrainId,
                taskId,
                new DurableTaskInvocationMessage { TaskId = taskId, Request = request, Sender = sender },
                runtime.UtcNow);
    });

    [Fact]
    public void PostedRootCoalescesWithAPendingInvocationOfTheSameMethodWhoseRequestSerializesDifferently()
    {
        var (runtime, storage, _, transport) = CreateRuntime();
        var rootId = TaskId.CreateRoot("posted");
        var target = GrainId.Create("target", "one");
        var messageId = transport.GetInvocationMessageId(runtime.GrainId, target, rootId, subscriberId: null);
        var pendingRequest = CreateSerializableRequest(target);
        pendingRequest.Context!.CallerId = default;
        var payload = CreateSerializer().SerializeToArray(
            new DurableTaskInvocationMessage { TaskId = rootId, Request = pendingRequest, Sender = runtime.GrainId, Detached = true });
        storage.Invocations[messageId] = new PendingInvocation(target, payload, RecordId: null, runtime.UtcNow);

        runtime.PostRoot(rootId, CreateSerializableRequest(target));

        Assert.Empty(transport.SentInvocations);
        Assert.Same(payload, storage.Invocations[messageId].Payload);
        var exception = Assert.Throws<InvalidOperationException>(
            () => runtime.PostRoot(rootId, new TestOtherMethodDurableTaskRequest { Context = new DurableTaskRequestContext { TargetId = target } }));
        Assert.Contains(messageId.ToString(), exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Creates a request for another method than <see cref="CreateRequest"/>'s, addressed like it.</summary>
    private static RuntimeTestOtherMethodRequest CreateOtherMethodRequest(int argument, GrainId? target = null) =>
        new(null, argument)
        {
            Context = new DurableTaskRequestContext
            {
                CallerId = GrainId.Create("caller", "one"),
                TargetId = target ?? GrainId.Create("test", "one"),
                SupportsDurableCompletion = true,
            },
        };
}
