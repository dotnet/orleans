using Microsoft.Extensions.Time.Testing;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks.Runtime;
using Orleans.Messaging;
using Orleans.Runtime;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.Tests;

/// <summary>
/// Runs of one root task ID: each run has its own incarnation, which the identities of the messages which name its nodes
/// cover, so a later run neither shares messages with an earlier run nor observes the earlier run's stale replies.
/// </summary>
public sealed partial class DurableTaskRuntimeInvariantTests
{
    [Fact]
    public Task RootWhichRunsAgainAfterItsDeletionSendsItsOwnMessagesAndIgnoresTheEarlierRunsReplies() => ActivationScheduler.RunAsync(async () =>
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (runtime, storage, manager, transport) = CreateRuntime(TimeSpan.Zero);
        var rootId = TaskId.CreateRoot("order");
        var callId = rootId.Child("call");

        // The first run calls another grain and cancels the call. Its result has no retention, so the write which follows
        // the call's outcome deletes the run's tree.
        await ((IDurableTaskServer)runtime).ScheduleAsync(rootId, CreateRequest(1, CallAndCancel), cancellationToken);
        await WaitUntilAsync(() => transport.Invocations.Count == 1 && transport.Cancellations.Count == 1);
        var firstWireId = transport.Invocations[0].TaskId;
        var firstInvocationId = Assert.Single(transport.SentInvocations);
        Assert.Equal(CallerScope.GetWireId(storage.Get(rootId).Incarnation, callId), firstWireId);
        Assert.Equal(firstWireId, transport.Cancellations[0].TaskId);
        runtime.AcceptResponseFromInbox(firstWireId, CallerScope.GetTaskId(runtime.GrainId, firstWireId), DurableTaskResponse.Canceled, RemoteTarget);
        await manager.WriteStateAsync(cancellationToken);
        await WaitUntilAsync(() => !storage.Contains(rootId) && !storage.Contains(callId));

        // The same root ID runs again, within Messaging's deduplication window, and records the call under the same ID.
        await ((IDurableTaskServer)runtime).ScheduleAsync(rootId, CreateRequest(1, CallAndCancel), cancellationToken);
        var handle = runtime.GetScheduledTaskHandle(rootId);
        await WaitUntilAsync(() => transport.Invocations.Count == 2 && transport.Cancellations.Count == 2);
        var secondWireId = transport.Invocations[1].TaskId;

        // The run's invocation and cancellation name the call by the run's own wire ID, so their message IDs differ from
        // the first run's, and the target stores the call under another ID.
        Assert.Equal(CallerScope.GetWireId(storage.Get(rootId).Incarnation, callId), secondWireId);
        Assert.NotEqual(firstWireId, secondWireId);
        Assert.Equal(secondWireId, transport.Cancellations[1].TaskId);
        Assert.Equal(2, transport.SentInvocations.Count);
        Assert.NotEqual(firstInvocationId, transport.SentInvocations[1]);
        Assert.NotEqual(
            DurableTaskMessageTransport.CreateMessageId(runtime.GrainId, RemoteTarget, firstWireId, DurableTaskMessageTypes.CancellationSubject),
            DurableTaskMessageTransport.CreateMessageId(runtime.GrainId, RemoteTarget, secondWireId, DurableTaskMessageTypes.CancellationSubject));
        Assert.NotEqual(CallerScope.GetTaskId(runtime.GrainId, firstWireId), CallerScope.GetTaskId(runtime.GrainId, secondWireId));

        // The first run's invocation retires, and a completion addressed to the first run's call arrives late. Neither
        // reaches the second run's call.
        ((IOutboxMessageSource)runtime).OnRetired(
            firstInvocationId,
            OutboxMessageRetirement.Rejected("conflict", DurableTaskRejectionCodes.Conflict));
        runtime.AcceptResponseFromInbox(firstWireId, CallerScope.GetTaskId(runtime.GrainId, firstWireId), DurableTaskResponse.Completed, RemoteTarget);
        await manager.WriteStateAsync(cancellationToken);
        Assert.Null(storage.Get(callId).Accepted);
        Assert.Null(storage.Get(callId).Result);
        Assert.Null(storage.Get(rootId).Result);

        // The second run's own completion completes it.
        runtime.AcceptResponseFromInbox(secondWireId, CallerScope.GetTaskId(runtime.GrainId, secondWireId), DurableTaskResponse.Canceled, RemoteTarget);
        await manager.WriteStateAsync(cancellationToken);
        Assert.Equal(nameof(DurableTaskStatus.Canceled), (await WaitForResponseAsync(handle)).GetResult<string>());

        static DurableTask<string> CallAndCancel() => DurableTask.Run(async cancellationToken =>
        {
            var call = new TestStateManager.TestRemoteDurableTask(CreateRemoteRequest(2)).WithId("call").Post();
            await call.CancelAsync("superseded", cancellationToken);
            return $"{(await call.GetResponseAsync(CancellationToken.None)).Status}";
        });
    });

    [Fact]
    public Task ResumeOfAnEarlierRunDoesNotCompleteALaterRunsDelayWithTheSameIdAndGeneration() => ActivationScheduler.RunAsync(async () =>
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var (runtime, storage, _, transport) = CreateRuntime(TimeSpan.Zero, timeProvider: clock);
        var rootId = TaskId.Parse("root");
        var delayId = rootId.Child("delay");
        storage.AddInvoked(rootId);
        await runtime.ScheduleDelayAsync(delayId, TimeSpan.FromMinutes(5), cancellationToken);
        var stale = Assert.Single(transport.Resumes);

        // The run ends, and its result has no retention, so the write which follows deletes its tree.
        await runtime.AbortTaskAsync(rootId, "finished", cancellationToken);
        await WaitUntilAsync(() => !storage.Contains(rootId) && !storage.Contains(delayId));

        // A later run records the same delay, with the same generation and due time, under another incarnation.
        storage.AddInvoked(rootId);
        await runtime.ScheduleDelayAsync(delayId, TimeSpan.FromMinutes(5), cancellationToken);
        var current = transport.Resumes[1];
        Assert.Equal((stale.TaskId, stale.Generation, stale.DueTime), (current.TaskId, current.Generation, current.DueTime));
        Assert.Equal(storage.Get(rootId).Incarnation, current.Incarnation);
        Assert.NotEqual(stale.Incarnation, current.Incarnation);
        Assert.NotEqual(
            DurableTaskMessageTransport.CreateResumeMessageId(runtime.GrainId, delayId, stale.Incarnation, stale.Generation, stale.DueTime),
            DurableTaskMessageTransport.CreateResumeMessageId(runtime.GrainId, delayId, current.Incarnation, current.Generation, current.DueTime));

        // The earlier run's resume changes nothing; the later run's own resume completes the delay.
        var handler = CreateHandler(runtime);
        await HandleAsync(handler, ResumeContext(runtime, delayId, stale.Generation, stale.DueTime, stale.Incarnation));
        Assert.Null(storage.Get(delayId).Result);
        await HandleAsync(handler, ResumeContext(runtime, delayId, current.Generation, current.DueTime, current.Incarnation));
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, storage.Get(delayId).Result!.Status);
    });

    [Fact]
    public Task EarlyCancellationOfAnEarlierCallerRunDoesNotCancelTheLaterRunsInvocation() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var handler = CreateHandler(runtime);
        var sender = GrainId.Create("caller", "one");
        var callId = TaskId.Parse("starter/call");
        var earlierWireId = CallerScope.GetWireId(Guid.NewGuid(), callId);
        var wireId = CallerScope.GetWireId(Guid.NewGuid(), callId);

        // An earlier run of the caller's tree canceled its call, whose invocation never arrived, so this grain records the
        // cancellation under the earlier run's scoped ID.
        Accept(handler, CancellationMessage(sender, runtime.GrainId, earlierWireId));
        Assert.NotNull(storage.Get(CallerScope.Resolve(sender, earlierWireId)).CancellationRequestedAt);

        // The caller's later run invokes its call with the same node ID, which this grain stores under another ID.
        Accept(
            handler,
            MessagingTestHelpers.CreateInvocationMessage(
                sender,
                runtime.GrainId,
                wireId,
                new DurableTaskInvocationMessage { TaskId = wireId, Request = CreateSerializableRequest(runtime.GrainId), Sender = sender },
                runtime.UtcNow));
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);

        var storedId = CallerScope.Resolve(sender, wireId);
        await WaitUntilAsync(() => storage.Get(storedId).Result is { IsCompleted: true });
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, storage.Get(storedId).Result!.Status);
        Assert.Null(storage.Get(storedId).CancellationRequestedAt);
        Assert.Equal([new DurableTaskSubscriber(sender, wireId)], storage.Get(storedId).CompletionDestinations);
        Assert.Null(storage.Get(CallerScope.Resolve(sender, earlierWireId)).Result);
        await runtime.StopAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    });

    [Fact]
    public Task ClientScheduleBeneathTheReservedCallerScopeRootIsRejected() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var scopedId = CallerScope.GetTaskId(GrainId.Create("caller", "one"), CallerScope.GetWireId(Guid.NewGuid(), TaskId.Parse("starter/call")));

        // Only another grain's invocation stores a task in that grain's scope.
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await ((IDurableTaskServer)runtime).ScheduleAsync(scopedId, CreateRequest(1), TestContext.Current.CancellationToken));

        Assert.Empty(storage.Tasks);
        Assert.Equal(0, manager.WriteCount);
    });

    private static async Task<DurableTaskResponse> WaitForResponseAsync(IScheduledTaskHandle handle) =>
        await handle.WaitAsync(TestContext.Current.CancellationToken)
            .AsTask()
            .WaitAsync(CancellationTestTimeout, TestContext.Current.CancellationToken);
}
