using System.Diagnostics.CodeAnalysis;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using NSubstitute;
using Orleans.CodeGeneration;
using Orleans.Configuration;
using Orleans.DurableJobs;
using Orleans.DurableMessaging;
using Orleans.Invocation;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.Session;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class DurableTaskRuntimeInvariantTests
{
    private static readonly ServiceProvider EnvelopeServiceProvider = new ServiceCollection()
        .AddSerializer(builder => builder
            .AddAssembly(typeof(DurableTaskMessageHandler).Assembly)
            .AddAssembly(typeof(DurableTaskRuntimeInvariantTests).Assembly))
        .BuildServiceProvider();

    [Fact]
    public void Participant_WithoutObserverSupport_ThrowsDescriptiveConfigurationError()
    {
        var (runtime, _, _, _) = CreateRuntime();
        var grainContext = new TestGrainContext(GrainId.Create("test", "observer-support"));
        var jobHandlers = Substitute.For<IDurableJobHandlerRegistry>();
        var stateManager = Substitute.For<IJournaledStateManager>();
        stateManager
            .When(manager => manager.RegisterObserver(Arg.Any<IJournaledStateObserver>()))
            .Do(_ => throw new NotSupportedException("Observers are not supported."));
        var participant = new DurableTaskGrainParticipant(runtime, grainContext, jobHandlers, stateManager);

        var exception = Assert.Throws<InvalidOperationException>(participant.Initialize);

        Assert.Contains("Durable Tasks requires", exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(IJournaledStateManager.RegisterObserver), exception.Message, StringComparison.Ordinal);
        Assert.IsType<NotSupportedException>(exception.InnerException);
    }

    [Fact]
    public async Task OrdinaryGrainActivationFailsBeforeUsingUninitializedJournalAdapter()
    {
        var (runtime, _, _, _) = CreateRuntime(initialize: false);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.SignalCancellationAsync(TaskId.Parse("root"), default).AsTask());

        Assert.Contains(nameof(DurableGrain), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OrdinaryGrainActivationRejectsPollingAndDiagnostics()
    {
        var (runtime, _, _, _) = CreateRuntime(initialize: false);
        var extension = (IDurableTaskGrainExtension)runtime;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.SubscribeOrPollAsync(TaskId.Parse("root"), default, default).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await extension.GetTasksAsync().GetAsyncEnumerator().MoveNextAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await extension.GetRunningTasksAsync().GetAsyncEnumerator().MoveNextAsync());
    }

    [Fact]
    public async Task InboxSchedulingStartsOnlyAfterCommit()
    {
        var (runtime, _, manager, _) = CreateRuntime();
        var invoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = CreateRequest(1, () => DurableTask.Run(_ => invoked.TrySetResult()));
        var taskId = TaskId.Parse("root");

        var response = await runtime.ScheduleFromInboxAsync(taskId, request, default);

        Assert.Equal(DurableTaskResponseKind.Subscribed, response.ResponseKind);
        Assert.False(invoked.Task.IsCompleted);
        Assert.Equal(0, manager.WriteCount);

        await manager.WriteStateAsync(default);

        await invoked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, request.CreateTaskCallCount);
    }

    [Fact]
    public async Task DeferredStartRechecksCommittedCancellationBeforeInvocation()
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var request = CreateRequest(1);
        var taskId = TaskId.Parse("root");
        await runtime.ScheduleFromInboxAsync(taskId, request, default);
        manager.AfterWriteStarted = () =>
        {
            manager.AfterWriteStarted = null;
            var state = storage.Get(taskId);
            storage.RequestCancellation(taskId, state);
            return default;
        };

        await manager.WriteStateAsync(default);
        await WaitUntilAsync(() => storage.Get(taskId).Result is { IsCompleted: true });

        Assert.Equal(0, request.CreateTaskCallCount);
        Assert.Equal(DurableTaskStatus.Canceled, storage.Get(taskId).Result!.Status);
    }

    [Fact]
    public async Task EquivalentDuplicateDeliveryExecutesOnce()
    {
        var (runtime, _, manager, _) = CreateRuntime();
        var first = CreateRequest(1);
        var retry = CreateRequest(1);
        var taskId = TaskId.Parse("root");

        await runtime.ScheduleFromInboxAsync(taskId, first, default);
        await runtime.ScheduleFromInboxAsync(taskId, retry, default);
        await manager.WriteStateAsync(default);
        await WaitUntilAsync(() => first.CreateTaskCallCount + retry.CreateTaskCallCount == 1);

        Assert.Equal(1, first.CreateTaskCallCount + retry.CreateTaskCallCount);
    }

    [Fact]
    public async Task ConflictingRequestForSameTaskIdFailsBeforeExecution()
    {
        var (runtime, _, _, _) = CreateRuntime();
        var taskId = TaskId.Parse("root");
        await runtime.ScheduleFromInboxAsync(taskId, CreateRequest(1), default);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.ScheduleFromInboxAsync(taskId, CreateRequest(2), default).AsTask());

        Assert.Contains("different request", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvocationWithoutRequestContextFailsBeforeChangingDurableState()
    {
        var (runtime, storage, manager, transport) = CreateRuntime();
        var services = new ServiceCollection();
        services.AddSerializer(builder => builder.AddAssembly(typeof(DurableTaskRuntimeInvariantTests).Assembly));
        using var serviceProvider = services.BuildServiceProvider();
        var sender = GrainId.Create("caller", "one");
        var receiver = GrainId.Create("target", "one");
        var envelope = new DurableEnvelopeBuilder(
                serviceProvider.GetRequiredService<SerializerSessionPool>(),
                sender)
            .To(receiver, DurableTaskMessageTransport.InvocationRoute)
            .WithBody(new DurableTaskInvocationMessage
            {
                TaskId = TaskId.Parse("legacy"),
                Request = new TestDurableTaskRequest(),
            })
            .Build();
        var context = Substitute.For<IInboxHandlerContext>();
        context.Envelope.Returns(envelope);
        context.GrainId.Returns(receiver);
        var handler = (IInboxHandler)new DurableTaskMessageHandler(runtime);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await handler.HandleAsync(context, CancellationToken.None));

        Assert.Equal("The durable task invocation request has no context.", exception.Message);
        Assert.Empty(storage.Tasks);
        Assert.Equal(0, manager.WriteCount);
        Assert.Empty(transport.Invocations);
        Assert.Empty(transport.Completions);
        Assert.Empty(transport.Cancellations);
        Assert.Empty(transport.ScheduledResumes);
        Assert.Equal(0, transport.CommitCount);
    }

    [Fact]
    public async Task InboxInvocationRequiresMatchingTargetAndReplyAddress()
    {
        var (runtime, storage, _, transport) = CreateRuntime();
        var sender = GrainId.Create("caller", "one");
        var receiver = GrainId.Create("test", "one");
        var request = CreateSerializableRequest(GrainId.Create("target", "other"));
        var handler = (IInboxHandler)new DurableTaskMessageHandler(runtime);

        var wrongTarget = CreateHandlerContext(
            receiver,
            CreateEnvelope(
                sender,
                receiver,
                DurableTaskMessageTransport.InvocationRoute,
                new DurableTaskInvocationMessage { TaskId = TaskId.Parse("wrong-target"), Request = request },
                replyTo: sender));
        var targetException = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await handler.HandleAsync(wrongTarget, default));
        Assert.Contains("not receiver", targetException.Message, StringComparison.Ordinal);

        request.Context!.TargetId = receiver;
        var wrongReply = CreateHandlerContext(
            receiver,
            CreateEnvelope(
                sender,
                receiver,
                DurableTaskMessageTransport.InvocationRoute,
                new DurableTaskInvocationMessage { TaskId = TaskId.Parse("wrong-reply"), Request = request },
                replyTo: GrainId.Create("caller", "other")));
        var replyException = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await handler.HandleAsync(wrongReply, default));
        Assert.Contains("does not match sender", replyException.Message, StringComparison.Ordinal);

        Assert.Empty(storage.Tasks);
        Assert.Empty(transport.Completions);
    }

    [Fact]
    public async Task InboxCancellationBindsTaskToSender()
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var receiver = GrainId.Create("test", "one");
        var caller = GrainId.Create("caller", "one");
        var otherCaller = GrainId.Create("caller", "two");
        var taskId = TaskId.Parse("bound-cancellation");
        var handler = (IInboxHandler)new DurableTaskMessageHandler(runtime);

        await handler.HandleAsync(
            CreateHandlerContext(
                receiver,
                CreateEnvelope(
                    caller,
                    receiver,
                    DurableTaskMessageTransport.CancellationRoute,
                    new DurableTaskCancellationMessage { TaskId = taskId })),
            default);

        Assert.Equal(caller, storage.Get(taskId).CallerId);
        await manager.WriteStateAsync(default);
        var cancellationException = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await handler.HandleAsync(
                CreateHandlerContext(
                    receiver,
                    CreateEnvelope(
                        otherCaller,
                        receiver,
                        DurableTaskMessageTransport.CancellationRoute,
                        new DurableTaskCancellationMessage { TaskId = taskId })),
                default));
        Assert.Contains("already associated with caller", cancellationException.Message, StringComparison.Ordinal);

        var request = CreateSerializableRequest(receiver);
        var invocationException = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await handler.HandleAsync(
                CreateHandlerContext(
                    receiver,
                    CreateEnvelope(
                        otherCaller,
                        receiver,
                        DurableTaskMessageTransport.InvocationRoute,
                        new DurableTaskInvocationMessage { TaskId = taskId, Request = request },
                        replyTo: otherCaller)),
                default));
        Assert.Contains("already associated with caller", invocationException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InboxCancellationDefersTaskAndMessageCommitToOuterWrite()
    {
        var (runtime, storage, manager, transport) = CreateRuntime();
        var receiver = GrainId.Create("test", "one");
        var caller = GrainId.Create("caller", "one");
        var rootId = TaskId.Parse("root");
        var childId = TaskId.Parse("root/remote");
        storage.GetOrCreate(rootId);
        var child = storage.GetOrCreate(childId);
        var target = GrainId.Create("target", "one");
        storage.SetRemoteRequest(childId, child, target, "fingerprint");
        var handler = (IInboxHandler)new DurableTaskMessageHandler(runtime);

        await handler.HandleAsync(
            CreateHandlerContext(
                receiver,
                CreateEnvelope(
                    caller,
                    receiver,
                    DurableTaskMessageTransport.CancellationRoute,
                    new DurableTaskCancellationMessage { TaskId = rootId })),
            default);

        Assert.Equal(0, manager.WriteCount);
        Assert.NotNull(storage.Get(rootId).CancellationRequestedAt);
        Assert.NotNull(storage.Get(childId).CancellationRequestedAt);
        var cancellation = Assert.Single(transport.Cancellations);
        Assert.Equal(childId, cancellation.TaskId);
        Assert.Equal(target, cancellation.Target);
    }

    [Fact]
    public async Task RunningInboxCancellationDefersTerminalResponseUntilOuterWrite()
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var receiver = GrainId.Create("test", "one");
        var caller = GrainId.Create("caller", "one");
        var taskId = TaskId.Parse("root");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = CreateRequest(
            1,
            () => DurableTask.Run(async cancellationToken =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }));
        await runtime.ScheduleFromInboxAsync(taskId, request, default);
        await manager.WriteStateAsync(default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var writesBeforeCancellation = manager.WriteCount;
        var handler = (IInboxHandler)new DurableTaskMessageHandler(runtime);

        await handler.HandleAsync(
            CreateHandlerContext(
                receiver,
                CreateEnvelope(
                    caller,
                    receiver,
                    DurableTaskMessageTransport.CancellationRoute,
                    new DurableTaskCancellationMessage { TaskId = taskId })),
            default);

        Assert.Equal(writesBeforeCancellation, manager.WriteCount);
        Assert.Null(storage.Get(taskId).Result);

        await manager.WriteStateAsync(default);
        await WaitUntilAsync(() => storage.Get(taskId).Result is { IsCompleted: true });
        Assert.Equal(DurableTaskStatus.Canceled, storage.Get(taskId).Result!.Status);
    }

    [Fact]
    public async Task InboxCancellationPropagatesToRunningDescendantsAfterOuterWrite()
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var receiver = GrainId.Create("test", "one");
        var caller = GrainId.Create("caller", "one");
        var rootId = TaskId.Parse("root");
        var childId = TaskId.Parse("root/child");
        storage.GetOrCreate(rootId);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var childHandle = await runtime.ScheduleChildAsync(
            childId,
            DurableTask.Run(async cancellationToken =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    await runtime.ScheduleDelayAsync(
                        TaskId.Parse("root/callback-write"),
                        runtime.UtcNow,
                        default);
                    throw;
                }
            }),
            default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var handler = (IInboxHandler)new DurableTaskMessageHandler(runtime);

        await handler.HandleAsync(
            CreateHandlerContext(
                receiver,
                CreateEnvelope(
                    caller,
                    receiver,
                    DurableTaskMessageTransport.CancellationRoute,
                    new DurableTaskCancellationMessage { TaskId = rootId })),
            default);
        Assert.False((await childHandle.PollAsync(
            new PollingOptions { PollTimeout = TimeSpan.Zero },
            default)).IsCompleted);

        await manager.WriteStateAsync(default);

        Assert.Equal(
            DurableTaskStatus.Canceled,
            (await childHandle.WaitAsync(default).AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Status);
    }

    [Fact]
    public async Task CompletionRequiresRecordedRemoteTarget()
    {
        var (runtime, storage, _, transport) = CreateRuntime();
        var receiver = GrainId.Create("test", "one");
        var request = CreateRequest(1);
        var expectedTarget = request.Context!.TargetId;
        var taskId = TaskId.Parse("root/remote");
        await runtime.ScheduleChildAsync(taskId, new TestStateManager.TestRemoteDurableTask(request), default);
        var handler = (IInboxHandler)new DurableTaskMessageHandler(runtime);
        var completion = new DurableTaskCompletionMessage
        {
            TaskId = taskId,
            Response = DurableTaskResponse.FromResult(42),
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await handler.HandleAsync(
                CreateHandlerContext(
                    receiver,
                    CreateEnvelope(
                        GrainId.Create("target", "other"),
                        receiver,
                        DurableTaskMessageTransport.CompletionRoute,
                        completion)),
                default));
        Assert.Contains("does not accept completions", exception.Message, StringComparison.Ordinal);
        Assert.Null(storage.Get(taskId).Result);
        Assert.Empty(transport.CompletionAcknowledgements);

        await handler.HandleAsync(
            CreateHandlerContext(
                receiver,
                CreateEnvelope(
                    expectedTarget,
                    receiver,
                    DurableTaskMessageTransport.CompletionRoute,
                    completion)),
            default);

        Assert.Equal(42, storage.Get(taskId).Result!.GetResult<int>());
        Assert.Equal(expectedTarget, Assert.Single(transport.CompletionAcknowledgements).Target);
    }

    [Fact]
    public async Task CompletedRequestReplayRequeuesPersistedUnacknowledgedCompletion()
    {
        var (runtime, storage, _, transport) = CreateRuntime();
        var taskId = TaskId.Parse("root");
        var request = CreateRequest(1);
        var caller = request.Context!.CallerId;
        var state = storage.GetOrCreate(taskId);
        storage.SetRequest(taskId, state, request);
        storage.SetRequestFingerprint(taskId, state, IDurableTaskRequest.GetFingerprint(request, CreateSerializer()));
        storage.AddCompletionDestination(taskId, state, caller);
        storage.SetResponse(taskId, state, DurableTaskResponse.FromResult(42));

        var response = await runtime.ScheduleFromInboxAsync(taskId, request, default);

        Assert.Equal(42, response.GetResult<int>());
        var completion = Assert.Single(transport.Completions);
        Assert.Equal(caller, completion.Target);
        Assert.Equal(taskId, completion.TaskId);
    }

    [Fact]
    public async Task RootRemoteScheduleCreatesCallerStateBeforeAdvertisingCompletion()
    {
        var (runtime, storage, manager, transport) = CreateRuntime();
        var taskId = TaskId.Parse("root/remote");
        var request = CreateRemoteRequest(1);
        var target = request.Context!.TargetId;
        transport.BeforeSendInvocation = (_, sentTarget, sentTaskId, _) =>
        {
            var state = storage.Get(sentTaskId);
            Assert.Equal(target, sentTarget);
            Assert.Equal(target, state.RemoteTarget);
            Assert.NotNull(state.RemoteRequestFingerprint);
        };

        await runtime.ScheduleRemoteAsync(taskId, request, default);

        Assert.Equal(1, manager.WriteCount);
        Assert.True(request.Context.SupportsDurableCompletion);
    }

    [Fact]
    public async Task RootRemoteScheduleReturnsPersistedTerminalResultWithoutResending()
    {
        var (runtime, storage, manager, transport) = CreateRuntime();
        var taskId = TaskId.Parse("root/remote");
        var request = CreateRemoteRequest(1);
        var state = storage.GetOrCreate(taskId);
        storage.SetRemoteRequest(
            taskId,
            state,
            request.Context!.TargetId,
            IDurableTaskRequest.GetFingerprint(request, CreateSerializer()));
        storage.SetResponse(taskId, state, DurableTaskResponse.FromResult(42));

        var response = await runtime.ScheduleRemoteAsync(taskId, request, default);

        Assert.Equal(42, response.GetResult<int>());
        Assert.Empty(transport.Invocations);
        Assert.Equal(0, manager.WriteCount);
    }

    [Fact]
    public async Task RootRemoteScheduleRejectsExpiredTombstoneWithoutResending()
    {
        var (runtime, storage, manager, transport) = CreateRuntime();
        var taskId = TaskId.Parse("root/remote");
        var request = CreateRemoteRequest(1);
        var state = storage.GetOrCreate(taskId);
        storage.SetRemoteRequest(
            taskId,
            state,
            request.Context!.TargetId,
            IDurableTaskRequest.GetFingerprint(request, CreateSerializer()));
        state.TombstonedAt = runtime.UtcNow;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.ScheduleRemoteAsync(taskId, request, default).AsTask());

        Assert.Contains("retained result has expired", exception.Message, StringComparison.Ordinal);
        Assert.Empty(transport.Invocations);
        Assert.Equal(0, manager.WriteCount);
    }

    [Fact]
    public async Task CompletionHandleAdvancesOnlyAfterCommit()
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var taskId = TaskId.Parse("root/remote");
        var target = GrainId.Create("target", "one");
        var state = storage.GetOrCreate(taskId);
        storage.SetRemoteRequest(taskId, state, target, "fingerprint");
        var handle = runtime.GetScheduledTaskHandle(taskId);

        await runtime.AcceptResponseAsync(
            taskId,
            DurableTaskResponse.FromResult(42),
            target,
            default,
            persist: false);

        Assert.False((await handle.PollAsync(new PollingOptions { PollTimeout = TimeSpan.Zero }, default)).IsCompleted);
        await manager.WriteStateAsync(default);
        Assert.Equal(42, (await handle.WaitAsync(default)).GetResult<int>());
    }

    [Fact]
    public async Task InboxCompletionPersistsEligiblePruningAfterOuterWrite()
    {
        var (runtime, storage, manager, _) = CreateRuntime(TimeSpan.Zero);
        var taskId = TaskId.Parse("root/remote");
        var target = GrainId.Create("target", "one");
        var state = storage.GetOrCreate(taskId);
        storage.SetRemoteRequest(taskId, state, target, "fingerprint");

        await runtime.AcceptResponseAsync(
            taskId,
            DurableTaskResponse.FromResult(42),
            target,
            default,
            persist: false);
        Assert.True(storage.Contains(taskId));

        await manager.WriteStateAsync(default);
        await WaitUntilAsync(() => !storage.Contains(taskId) && manager.WriteCount >= 2);

        Assert.True(manager.WriteCount >= 2);
    }

    [Fact]
    public async Task ResponseStagedAfterWriteSnapshotIsCommittedBeforePruning()
    {
        var (runtime, storage, manager, _) = CreateRuntime(TimeSpan.Zero);
        var firstId = TaskId.Parse("root/first");
        var secondId = TaskId.Parse("root/second");
        var target = GrainId.Create("target", "one");
        var first = storage.GetOrCreate(firstId);
        storage.SetRemoteRequest(firstId, first, target, "first");
        var second = storage.GetOrCreate(secondId);
        storage.SetRemoteRequest(secondId, second, target, "second");
        var secondHandle = runtime.GetScheduledTaskHandle(secondId);
        await runtime.AcceptResponseAsync(
            firstId,
            DurableTaskResponse.FromResult(1),
            target,
            default,
            persist: false);
        manager.AfterWriteStarted = () => runtime.AcceptResponseAsync(
            secondId,
            DurableTaskResponse.FromResult(2),
            target,
            default,
            persist: false);

        await manager.WriteStateAsync(default);
        manager.AfterWriteStarted = null;

        Assert.Equal(
            2,
            (await secondHandle.WaitAsync(default).AsTask().WaitAsync(TimeSpan.FromSeconds(5))).GetResult<int>());
        await WaitUntilAsync(() => manager.WriteCount >= 2 && !storage.Contains(secondId));
    }

    [Fact]
    public async Task RecoveryDiscardsProvisionalCompletionHandle()
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var taskId = TaskId.Parse("root/remote");
        var target = GrainId.Create("target", "one");
        var state = storage.GetOrCreate(taskId);
        storage.SetRemoteRequest(taskId, state, target, "fingerprint");

        await runtime.AcceptResponseAsync(
            taskId,
            DurableTaskResponse.FromResult(42),
            target,
            default,
            persist: false);
        var handle = runtime.GetScheduledTaskHandle(taskId);
        await manager.RevertPendingChangesAsync(default);

        Assert.False((await handle.PollAsync(new PollingOptions { PollTimeout = TimeSpan.Zero }, default)).IsCompleted);
    }

    [Fact]
    public async Task LongPollDoesNotBlockCancellation()
    {
        var (runtime, storage, _, _) = CreateRuntime();
        var taskId = TaskId.Parse("root");
        storage.GetOrCreate(taskId);
        var polling = runtime.SubscribeOrPollAsync(
            taskId,
            new SubscribeOrPollOptions { PollTimeout = TimeSpan.FromMinutes(1) },
            default).AsTask();

        await runtime.SignalCancellationAsync(taskId, default).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DurableTaskStatus.Canceled, (await polling.WaitAsync(TimeSpan.FromSeconds(5))).Status);
    }

    [Fact]
    public async Task ConcurrentHandleLookupReturnsOneHandle()
    {
        var (runtime, storage, _, _) = CreateRuntime();
        var taskId = TaskId.Parse("root");
        storage.GetOrCreate(taskId);

        var handles = await Task.WhenAll(
            Enumerable.Range(0, 100)
                .Select(_ => Task.Run(() => runtime.GetScheduledTaskHandle(taskId))));

        Assert.All(handles, handle => Assert.Same(handles[0], handle));
    }

    [Fact]
    public async Task PreCanceledInboxInvocationStagesOneCompletion()
    {
        var (runtime, _, manager, transport) = CreateRuntime();
        var receiver = GrainId.Create("test", "one");
        var caller = GrainId.Create("caller", "one");
        var taskId = TaskId.Parse("pre-canceled");
        var handler = (IInboxHandler)new DurableTaskMessageHandler(runtime);

        await handler.HandleAsync(
            CreateHandlerContext(
                receiver,
                CreateEnvelope(
                    caller,
                    receiver,
                    DurableTaskMessageTransport.CancellationRoute,
                    new DurableTaskCancellationMessage { TaskId = taskId })),
            default);
        await manager.WriteStateAsync(default);
        var request = CreateSerializableRequest(receiver);
        await handler.HandleAsync(
            CreateHandlerContext(
                receiver,
                CreateEnvelope(
                    caller,
                    receiver,
                    DurableTaskMessageTransport.InvocationRoute,
                    new DurableTaskInvocationMessage { TaskId = taskId, Request = request },
                    replyTo: caller)),
            default);

        var completion = Assert.Single(transport.Completions);
        Assert.Equal(caller, completion.Target);
        Assert.Equal(DurableTaskStatus.Canceled, completion.Response.Status);
    }

    [Fact]
    public async Task CancellationBeforeInvocationPreventsExecution()
    {
        var (runtime, storage, _, _) = CreateRuntime();
        var request = CreateRequest(1);
        var taskId = TaskId.Parse("root");

        await runtime.SignalCancellationAsync(taskId, default);
        var response = await ((IDurableTaskServer)runtime).ScheduleAsync(taskId, request);

        Assert.Equal(DurableTaskStatus.Canceled, response.Status);
        Assert.Equal(0, request.CreateTaskCallCount);
        Assert.True(storage.Get(taskId).CancellationRequestedAt.HasValue);
    }

    [Fact]
    public async Task DirectScheduleRejectsMismatchedTargetBeforeStateMutation()
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var request = CreateRemoteRequest(1);
        var taskId = TaskId.Parse("wrong-target");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ((IDurableTaskServer)runtime).ScheduleAsync(taskId, request).AsTask());

        Assert.Contains("not receiver", exception.Message, StringComparison.Ordinal);
        Assert.Empty(storage.Tasks);
        Assert.Equal(0, manager.WriteCount);
        Assert.Equal(0, request.CreateTaskCallCount);
    }

    [Fact]
    public async Task DirectScheduleClearsForgedCallerIdentity()
    {
        var (runtime, storage, _, _) = CreateRuntime();
        var request = CreateRequest(1);
        request.Context!.CallerId = GrainId.Create("forged", "caller");
        request.Context.SupportsDurableCompletion = true;
        var taskId = TaskId.Parse("root");

        _ = await ((IDurableTaskServer)runtime).ScheduleAsync(taskId, request);

        Assert.Equal(default, storage.Get(taskId).CallerId);
        Assert.Empty(storage.Get(taskId).CompletionDestinations);
    }

    [Fact]
    public async Task StaleResumeGenerationCannotCompleteDelay()
    {
        var (runtime, storage, _, _) = CreateRuntime();
        var taskId = TaskId.Parse("root/delay");
        await runtime.ScheduleDelayAsync(taskId, runtime.UtcNow, default);
        var state = storage.Get(taskId);

        var stale = CreateRunContext(taskId, state.ResumeGeneration + 1);
        Assert.Same(DurableJobRunResult.Completed, await runtime.ExecuteJobAsync(stale, default));
        Assert.Null(storage.Get(taskId).Result);

        var current = CreateRunContext(taskId, state.ResumeGeneration);
        Assert.Same(DurableJobRunResult.Completed, await runtime.ExecuteJobAsync(current, default));
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, storage.Get(taskId).Result!.Status);
    }

    [Fact]
    public async Task MissingResumeStateWithUnsupportedPendingWriteSamplingReschedules()
    {
        var (runtime, _, manager, _) = CreateRuntime();
        manager.PendingWriteByteCount = -1;
        var result = await runtime.ExecuteJobAsync(
            CreateRunContext(TaskId.Parse("root/missing"), generation: 1),
            default);

        Assert.NotSame(DurableJobRunResult.Completed, result);
    }

    [Fact]
    public async Task PermanentlyMissingResumeStateEventuallyCompletes()
    {
        var (runtime, _, manager, _) = CreateRuntime();
        manager.PendingWriteByteCount = -1;
        var context = CreateRunContext(TaskId.Parse("root/missing"), generation: 1);
        Assert.NotSame(DurableJobRunResult.Completed, await runtime.ExecuteJobAsync(context, default));
        var result = await runtime.ExecuteJobAsync(context, default);

        Assert.Same(DurableJobRunResult.Completed, result);
    }

    [Fact]
    public async Task LocalDelayRemainsPendingUntilResumeAndRecoveryReschedulesIt()
    {
        var (runtime, storage, _, transport) = CreateRuntime();
        var localTaskId = TaskId.Parse("root/local");
        var localHandle = await runtime.ScheduleChildAsync(
            localTaskId,
            new TestStateManager.PendingDurableTask(),
            default);
        Assert.Equal(DurableTaskStatus.Pending, (await localHandle.PollAsync(default, default)).Status);
        Assert.Null(storage.Get(localTaskId).Result);
        Assert.Null(storage.Get(localTaskId).CompletedAt);

        var taskId = TaskId.Parse("root/delay");
        Assert.Equal(
            DurableTaskStatus.Pending,
            (await runtime.ScheduleDelayAsync(taskId, runtime.UtcNow, default)).Status);
        var handle = runtime.GetScheduledTaskHandle(taskId);

        Assert.Equal(
            DurableTaskStatus.Pending,
            (await handle.PollAsync(default, default)).Status);
        Assert.Null(storage.Get(taskId).Result);
        Assert.Null(storage.Get(taskId).CompletedAt);

        transport.ScheduledResumes.Clear();
        await runtime.ResumePendingTasksAsync(default);
        var resume = Assert.Single(transport.ScheduledResumes);
        Assert.Equal(taskId, resume.TaskId);
        Assert.Null(storage.Get(taskId).CompletedAt);

        var state = storage.Get(taskId);
        Assert.Same(DurableJobRunResult.Completed, await runtime.ExecuteJobAsync(
            CreateRunContext(taskId, state.ResumeGeneration),
            default));
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, (await handle.WaitAsync(default)).Status);
        Assert.NotNull(storage.Get(taskId).CompletedAt);
    }

    [Fact]
    public async Task RecoveredCanceledDelayTerminalizesBeforeRescheduleAndStaleResumesAreHarmless()
    {
        var (runtime, storage, _, transport) = CreateRuntime();
        var taskId = TaskId.Parse("root/delay");
        await runtime.ScheduleDelayAsync(taskId, runtime.UtcNow.AddMinutes(5), default);
        var generation = storage.Get(taskId).ResumeGeneration;

        storage.Get(taskId).CancellationRequestedAt = runtime.UtcNow;
        transport.ScheduledResumes.Clear();
        await runtime.ResumePendingTasksAsync(default);

        var canceled = storage.Get(taskId);
        Assert.Equal(DurableTaskStatus.Canceled, canceled.Result!.Status);
        Assert.NotNull(canceled.CompletedAt);
        Assert.Null(canceled.DueTime);
        Assert.True(canceled.ResumeGeneration > generation);
        Assert.Empty(transport.ScheduledResumes);

        await runtime.ExecuteJobAsync(CreateRunContext(taskId, generation), default);
        await runtime.ExecuteJobAsync(CreateRunContext(taskId, generation), default);
        Assert.Equal(DurableTaskStatus.Canceled, storage.Get(taskId).Result!.Status);
    }

    [Fact]
    public async Task PollingOnlyCallerIsNotRegisteredAsCompletionDestination()
    {
        var (runtime, storage, _, transport) = CreateRuntime();
        var request = CreateRequest(1);
        request.Context!.CallerId = GrainId.Create("client", "one");
        request.Context.SupportsDurableCompletion = false;
        var taskId = TaskId.Parse("client-request");

        var response = await ((IDurableTaskServer)runtime).ScheduleAsync(taskId, request);
        Assert.Equal(DurableTaskResponseKind.Pending, response.ResponseKind);
        await WaitUntilAsync(() => storage.Get(taskId).Result is { IsCompleted: true });

        Assert.Empty(storage.Get(taskId).CompletionDestinations);
        Assert.Empty(transport.Completions);
        Assert.Equal(
            DurableTaskStatus.CompletedSuccessfully,
            (await runtime.SubscribeOrPollAsync(taskId, default, default)).Status);
    }

    [Fact]
    public async Task RecoveredRemoteChildRetainsCancellationTargetAndReplayIdentity()
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var rootId = TaskId.Parse("root");
        var childId = TaskId.Parse("root/remote");
        storage.GetOrCreate(rootId);
        var request = CreateRemoteRequest(7);
        var target = request.Context!.TargetId;

        await runtime.ScheduleChildAsync(childId, new TestStateManager.TestRemoteDurableTask(request), default);
        var child = storage.Get(childId);
        var fingerprint = child.RemoteRequestFingerprint;
        Assert.Equal(target, child.RemoteTarget);
        Assert.NotNull(fingerprint);

        var (recovered, _, _, recoveredTransport) = CreateRuntime(storage, manager);
        manager.BeforeWrite = () => Assert.Contains(
            recoveredTransport.Cancellations,
            cancellation => cancellation.TaskId == childId && cancellation.Target == target);
        await recovered.SignalCancellationAsync(rootId, default);

        var cancellation = Assert.Single(recoveredTransport.Cancellations);
        Assert.Equal(childId, cancellation.TaskId);
        Assert.Equal(target, cancellation.Target);
        Assert.Equal(fingerprint, storage.Get(childId).RemoteRequestFingerprint);
    }

    [Fact]
    public async Task RemoteCancellationMarksStateBeforeStagingMessage()
    {
        var (runtime, storage, manager, transport) = CreateRuntime();
        var taskId = TaskId.Parse("root/remote");
        var target = GrainId.Create("target", "one");
        var state = storage.GetOrCreate(taskId);
        storage.SetRemoteRequest(taskId, state, target, "fingerprint");
        transport.BeforeSendCancellation = (_, sentTarget, sentTaskId) =>
        {
            Assert.Equal(target, sentTarget);
            Assert.Equal(taskId, sentTaskId);
            Assert.NotNull(storage.Get(taskId).CancellationRequestedAt);
            Assert.Equal(1, manager.WriteCount);
        };

        await runtime.CancelRemoteAsync(taskId, target, default);

        Assert.Equal(2, manager.WriteCount);
        Assert.Single(transport.Cancellations);
        Assert.NotNull(storage.Get(taskId).CancellationRequestedAt);
    }

    [Fact]
    public async Task RecoveryCommitsRemoteChildCancellationWithTerminalResponse()
    {
        var (_, storage, manager, _) = CreateRuntime();
        var childId = TaskId.Parse("root/remote");
        var target = GrainId.Create("target", "one");
        var child = storage.GetOrCreate(childId);
        storage.SetRemoteRequest(childId, child, target, "fingerprint");
        storage.RequestCancellation(childId, child);
        var (recovered, _, _, transport) = CreateRuntime(storage, manager);
        manager.BeforeWrite = () =>
        {
            Assert.Equal(DurableTaskStatus.Canceled, storage.Get(childId).Result!.Status);
            var cancellation = Assert.Single(transport.Cancellations);
            Assert.Equal(childId, cancellation.TaskId);
            Assert.Equal(target, cancellation.Target);
        };

        await recovered.ResumePendingTasksAsync(default);

        Assert.Equal(1, manager.WriteCount);
        Assert.Equal(DurableTaskStatus.Canceled, storage.Get(childId).Result!.Status);
    }

    [Fact]
    public async Task LocalAndRemoteRequestIdentitiesCannotShareTaskId()
    {
        var (runtime, storage, _, _) = CreateRuntime();
        var taskId = TaskId.Parse("identity");
        await runtime.ScheduleFromInboxAsync(taskId, CreateRequest(1), default);

        var remoteException = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.ScheduleChildAsync(
                taskId,
                new TestStateManager.TestRemoteDurableTask(CreateRemoteRequest(2)),
                default).AsTask());
        Assert.Contains("different request", remoteException.Message, StringComparison.Ordinal);

        var remoteTaskId = TaskId.Parse("remote-identity");
        await runtime.ScheduleChildAsync(
            remoteTaskId,
            new TestStateManager.TestRemoteDurableTask(CreateRemoteRequest(3)),
            default);
        var topLevelException = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.ScheduleFromInboxAsync(remoteTaskId, CreateRequest(4), default).AsTask());
        Assert.Contains("remote child request", topLevelException.Message, StringComparison.Ordinal);
        Assert.NotNull(storage.Get(remoteTaskId).RemoteRequestFingerprint);

        var delayTaskId = TaskId.Parse("delay-identity");
        await runtime.ScheduleDelayAsync(delayTaskId, runtime.UtcNow.AddMinutes(1), default);
        var delayException = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.ScheduleChildAsync(
                delayTaskId,
                new TestStateManager.TestRemoteDurableTask(CreateRemoteRequest(5)),
                default).AsTask());
        Assert.Contains("different request", delayException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunningLocalChildRejectsRemoteReuseWithoutChangingIdentity()
    {
        var (runtime, storage, _, _) = CreateRuntime();
        var taskId = TaskId.Parse("root/local");
        var localTask = new TestSchedulableTask(
            static (_, _) => new(DurableTaskResponse.Pending));
        _ = await runtime.ScheduleChildAsync(taskId, localTask, default);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.ScheduleChildAsync(
                taskId,
                new TestStateManager.TestRemoteDurableTask(CreateRemoteRequest(2)),
                default).AsTask());

        Assert.Contains("different request", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, localTask.ScheduleAsyncCallCount);
        Assert.Equal(default, storage.Get(taskId).RemoteTarget);
        Assert.Null(storage.Get(taskId).RemoteRequestFingerprint);
    }

    [Fact]
    public async Task FailedSchedulingRemovesTransientHandle()
    {
        var (runtime, _, _, _) = CreateRuntime();
        var taskId = TaskId.Parse("root/retry");
        var attempts = 0;
        var task = new TestSchedulableTask(
            (_, _) =>
            {
                attempts++;
                return attempts == 1
                    ? ValueTask.FromException<DurableTaskResponse>(
                        new InvalidOperationException("Expected scheduling failure."))
                    : new(DurableTaskResponse.Pending);
            });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.ScheduleChildAsync(taskId, task, default).AsTask());
        _ = await runtime.ScheduleChildAsync(taskId, task, default);

        Assert.Equal(2, task.ScheduleAsyncCallCount);
    }

    [Fact]
    public async Task PersistedLocalChildCancellationTerminalizesBeforeScheduling()
    {
        var (runtime, storage, _, _) = CreateRuntime();
        var taskId = TaskId.Parse("root/local");
        var state = storage.GetOrCreate(taskId);
        storage.RequestCancellation(taskId, state);
        var task = new TestSchedulableTask(static (_, _) => new(DurableTaskResponse.Pending));

        var handle = await runtime.ScheduleChildAsync(taskId, task, default);

        Assert.Equal(DurableTaskStatus.Canceled, (await handle.WaitAsync(default)).Status);
        Assert.Equal(0, task.ScheduleAsyncCallCount);
    }

    [Fact]
    public async Task PersistedRemoteChildCancellationTerminalizesBeforeResend()
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var taskId = TaskId.Parse("root/remote");
        var request = CreateRemoteRequest(1);
        var original = new TestStateManager.TestRemoteDurableTask(request);
        _ = await runtime.ScheduleChildAsync(taskId, original, default);
        storage.RequestCancellation(taskId, storage.Get(taskId));
        var (recovered, _, _, transport) = CreateRuntime(storage, manager);
        var retry = new TestStateManager.TestRemoteDurableTask(CreateRemoteRequest(1));

        var handle = await recovered.ScheduleChildAsync(taskId, retry, default);

        Assert.Equal(DurableTaskStatus.Canceled, (await handle.WaitAsync(default)).Status);
        Assert.Equal(0, retry.ScheduleAsyncCallCount);
        var cancellation = Assert.Single(transport.Cancellations);
        Assert.Equal(taskId, cancellation.TaskId);
        Assert.Equal(request.Context!.TargetId, cancellation.Target);
    }

    [Fact]
    public async Task CancelingLocalChildSignalsActiveDurableContext()
    {
        var (runtime, storage, stateManager, _) = CreateRuntime();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeCountObservedDuringCancellation = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var taskId = TaskId.Parse("root/local-cancel");
        var handle = await runtime.ScheduleChildAsync(
            taskId,
            DurableTask.Run(async cancellationToken =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    writeCountObservedDuringCancellation.TrySetResult(stateManager.WriteCount);
                    throw;
                }
            }),
            default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var writesBeforeCancellation = stateManager.WriteCount;

        await handle.CancelAsync(default);

        var observedWriteCount = await writeCountObservedDuringCancellation.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(observedWriteCount > writesBeforeCancellation);
        Assert.Equal(DurableTaskStatus.Canceled, storage.Get(taskId).Result!.Status);
    }

    [Fact]
    public async Task StopDiscardsSuccessReturnedAfterCatchingExecutionShutdown()
    {
        var (runtime, storage, _, transport) = CreateRuntime();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var caughtShutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = CreateRequest(
            1,
            () => DurableTask.Run(async durableCancellation =>
            {
                started.TrySetResult();
                try
                {
                    await DurableTask.Delay(TimeSpan.FromDays(1));
                }
                catch (OperationCanceledException)
                {
                    Assert.False(durableCancellation.IsCancellationRequested);
                    caughtShutdown.TrySetResult();
                }

                return 42;
            }));
        var taskId = TaskId.Parse("root");

        await ((IDurableTaskServer)runtime).ScheduleAsync(taskId, request);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await transport.ScheduledResume.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = runtime.StopAsync(default);
        await caughtShutdown.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(storage.Get(taskId).Result);
        Assert.Empty(transport.Completions);
    }

    [Fact]
    public async Task StopDiscardsFailureProducedAfterExecutionShutdown()
    {
        var (runtime, storage, _, transport) = CreateRuntime();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = CreateRequest(
            1,
            () => DurableTask.Run(async _ =>
            {
                started.TrySetResult();
                try
                {
                    await DurableTask.Delay(TimeSpan.FromDays(1));
                }
                catch (OperationCanceledException exception)
                {
                    throw new InvalidOperationException("Failure after shutdown.", exception);
                }
            }));
        var taskId = TaskId.Parse("root");

        await ((IDurableTaskServer)runtime).ScheduleAsync(taskId, request);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await transport.ScheduledResume.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = runtime.StopAsync(default);
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(storage.Get(taskId).Result);
        Assert.Empty(transport.Completions);
    }

    [Fact]
    public async Task ExecutionContextDispatchesRuntimeOperationsToCapturedScheduler()
    {
        var schedulerPair = new ConcurrentExclusiveSchedulerPair();
        var scheduler = schedulerPair.ExclusiveScheduler;
        var runtime = new SchedulerProbeRuntime(scheduler);
        using var shutdown = new CancellationTokenSource();
        var context = await Task.Factory.StartNew(
            () => new GrainDurableExecutionContext(
                TaskId.Parse("root"),
                runtime,
                scheduler,
                shutdown.Token),
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            scheduler);

        var response = await DurableTaskRuntimeHelper.RunAsync(
            DurableTask.WhenAny<int>([DurableTask.FromResult(1)]),
            context);

        Assert.Equal(TaskId.Parse("root/$when-any-1/0"), response.GetResult<TaskId>());
        Assert.Equal(2, runtime.SchedulerInvocationCount);
        schedulerPair.Complete();
        await schedulerPair.Completion;
    }

    [Fact]
    public async Task ExecutionContextRejectsUnrelatedCompletionSelectionIds()
    {
        var runtime = new SchedulerProbeRuntime(TaskScheduler.Current);
        var context = new GrainDurableExecutionContext(
            TaskId.Parse("root"),
            runtime,
            TaskScheduler.Current,
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => InvokeSelectCompletionCoreAsync(
                context,
                TaskId.Parse("other/decision"),
                [TaskId.Parse("root/child")],
                default).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => InvokeSelectCompletionCoreAsync(
                context,
                TaskId.Parse("root/decision"),
                [TaskId.Parse("other/child")],
                default).AsTask());
    }

    [Fact]
    public void ExecutionContextChildIdsSeparateGeneratedAndExplicitNamespaces()
    {
        var context = new GrainDurableExecutionContext(
            TaskId.Parse("root"),
            new SchedulerProbeRuntime(TaskScheduler.Current),
            TaskScheduler.Current,
            CancellationToken.None);

        var generated = InvokeCreateChildTaskId(context, null);
        var explicitNumeric = InvokeCreateChildTaskId(context, "0");
        var explicitSuffix = InvokeCreateChildTaskId(context, "job.1");
        _ = InvokeCreateChildTaskId(context, "job");
        var repeatedName = InvokeCreateChildTaskId(context, "job");

        Assert.NotEqual(generated, explicitNumeric);
        Assert.NotEqual(explicitSuffix, repeatedName);
        Assert.StartsWith("root/$child-", generated.ToString(), StringComparison.Ordinal);
        Assert.StartsWith("root/$child-", repeatedName.ToString(), StringComparison.Ordinal);
        Assert.Equal(TaskId.Parse("root/0"), explicitNumeric);
        var exception = Assert.Throws<TargetInvocationException>(
            () => InvokeCreateChildTaskId(context, "$reserved"));
        Assert.IsType<ArgumentException>(exception.InnerException);
    }

    [Fact]
    public async Task CompletionDecisionMustRetainCandidateIdentity()
    {
        var (runtime, storage, _, _) = CreateRuntime();
        var decisionId = TaskId.Parse("root/decision");
        var recordedWinner = TaskId.Parse("root/one");
        storage.GetOrCreate(recordedWinner).Result = DurableTaskResponse.Completed;
        Assert.Equal(
            recordedWinner,
            await runtime.SelectCompletionAsync(decisionId, [recordedWinner], default));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.SelectCompletionAsync(
                decisionId,
                [TaskId.Parse("root/two")],
                default).AsTask());

        Assert.Contains("another operation", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompletionDecisionRejectsExpiredTombstoneBeforePolling()
    {
        var (runtime, storage, _, _) = CreateRuntime();
        var decisionId = TaskId.Parse("root/decision");
        var candidateId = TaskId.Parse("root/candidate");
        var decision = storage.GetOrCreate(decisionId);
        decision.RequestFingerprint = "$completion-decision:expired";
        decision.TombstonedAt = runtime.UtcNow;
        storage.GetOrCreate(candidateId);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.SelectCompletionAsync(
                decisionId,
                [candidateId],
                default).AsTask());

        Assert.Contains("retained result has expired", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompletionDecisionRejectsExistingChildOperation()
    {
        var (runtime, _, _, _) = CreateRuntime();
        var decisionId = TaskId.Parse("root/decision");
        await runtime.ScheduleDelayAsync(decisionId, runtime.UtcNow.AddMinutes(1), default);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.SelectCompletionAsync(
                decisionId,
                [TaskId.Parse("root/candidate")],
                default).AsTask());

        Assert.Contains("another operation", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecutionShutdownCancelsAdapterSchedulingWithoutRequestingDurableCancellation()
    {
        var runtime = new ShutdownProbeRuntime();
        using var shutdown = new CancellationTokenSource();
        var context = new GrainDurableExecutionContext(
            TaskId.Parse("root"),
            runtime,
            TaskScheduler.Current,
            shutdown.Token);
        var invocation = DurableTaskRuntimeHelper.RunAsync(
            DurableTask.Delay(TimeSpan.FromDays(1)),
            context).AsTask();
        var executionCancellation = await runtime.SchedulingStarted.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(context.CancellationToken.IsCancellationRequested);
        Assert.False(executionCancellation.IsCancellationRequested);
        await shutdown.CancelAsync();
        var response = await invocation.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DurableTaskStatus.Canceled, response.Status);
        Assert.True(executionCancellation.IsCancellationRequested);
        Assert.False(context.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task DurableCancellationStillTerminalizesRunningExecution()
    {
        var (runtime, storage, _, _) = CreateRuntime();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = CreateRequest(
            1,
            () => DurableTask.Run(async durableCancellation =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, durableCancellation);
            }));
        var taskId = TaskId.Parse("root");

        await ((IDurableTaskServer)runtime).ScheduleAsync(taskId, request);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await runtime.SignalCancellationAsync(taskId, default);
        await WaitUntilAsync(() => storage.Get(taskId).Result is { IsCompleted: true });

        Assert.NotNull(storage.Get(taskId).CancellationRequestedAt);
        Assert.Equal(DurableTaskStatus.Canceled, storage.Get(taskId).Result!.Status);
        await runtime.StopAsync(default);
    }

    [Fact]
    public async Task StopEstablishesShutdownDespiteCanceledCallerToken()
    {
        var (runtime, _, _, _) = CreateRuntime();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await runtime.StopAsync(cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runtime.ResumePendingTasksAsync(default));
    }

    [Fact]
    public async Task StopWaitsForStagedResponseCommitBeforeCompletingHandles()
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var taskId = TaskId.Parse("root/remote");
        var target = GrainId.Create("target", "one");
        var state = storage.GetOrCreate(taskId);
        storage.SetRemoteRequest(taskId, state, target, "fingerprint");
        var handle = runtime.GetScheduledTaskHandle(taskId);
        await runtime.AcceptResponseAsync(
            taskId,
            DurableTaskResponse.FromResult(42),
            target,
            default,
            persist: false);

        var stopping = runtime.StopAsync(default);
        Assert.False(stopping.IsCompleted);
        await manager.WriteStateAsync(default);
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(42, (await handle.WaitAsync(default)).GetResult<int>());
    }

    [Fact]
    public async Task StopDrainsUncooperativeExecutionBeforeReplacementReplay()
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maxActive = 0;
        var request = CreateRequest(
            1,
            () => DurableTask.Run(async _ =>
            {
                var count = Interlocked.Increment(ref active);
                maxActive = Math.Max(maxActive, count);
                started.TrySetResult();
                try
                {
                    await release.Task;
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            }));
        var taskId = TaskId.Parse("root");

        await ((IDurableTaskServer)runtime).ScheduleAsync(taskId, request);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var firstStop = runtime.StopAsync(default);
        Assert.Same(firstStop, runtime.StopAsync(default));
        Assert.False(firstStop.IsCompleted);
        release.TrySetResult();
        await firstStop.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, active);
        Assert.Null(storage.Get(taskId).Result);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runtime.ResumePendingTasksAsync(default));

        started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var (replacement, _, _, _) = CreateRuntime(storage, manager);
        await replacement.ResumePendingTasksAsync(default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, maxActive);
        var replacementStop = replacement.StopAsync(default);
        release.TrySetResult();
        await replacementStop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(storage.Get(taskId).Result);
    }

    [Fact]
    public async Task FirstTerminalResponseWinsAndAcknowledgementRemovesOnlyItsWaiter()
    {
        var (runtime, storage, _, transport) = CreateRuntime();
        var taskId = TaskId.Parse("root/child");
        var target = GrainId.Create("target", "one");
        var other = GrainId.Create("target", "two");
        var state = storage.GetOrCreate(taskId);
        state.CompletionDestinations.Add(target);
        state.CompletionDestinations.Add(other);
        var handle = runtime.GetScheduledTaskHandle(taskId);

        runtime.AcceptResponse(taskId, DurableTaskResponse.FromResult(7));
        runtime.AcceptResponse(taskId, DurableTaskResponse.FromResult(8));
        await runtime.AcknowledgeCompletionAsync(taskId, target, default);

        Assert.Equal(7, storage.Get(taskId).Result!.GetResult<int>());
        Assert.Equal(7, (await handle.WaitAsync(default)).GetResult<int>());
        Assert.DoesNotContain(target, storage.Get(taskId).CompletionDestinations);
        Assert.Contains(other, storage.Get(taskId).CompletionDestinations);
        Assert.Empty(transport.Completions);
    }

    [Fact]
    public async Task ReplayedLocalChildReplacesRehydratedPlaceholder()
    {
        var (runtime, storage, _, _) = CreateRuntime();
        var taskId = TaskId.Parse("root/local");
        storage.GetOrCreate(taskId);

        var handle = await runtime.ScheduleChildAsync(
            taskId,
            DurableTask.Run(_ => { }),
            default);

        Assert.Equal(taskId, handle.TaskId);
    }

    [Fact]
    public async Task NewLocalChildCommitsBeforeUserInvocation()
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var invoked = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var taskId = TaskId.Parse("root/local");
        manager.BeforeWrite = () => Assert.False(invoked.Task.IsCompleted);

        _ = await runtime.ScheduleChildAsync(
            taskId,
            DurableTask.Run(_ => invoked.TrySetResult(manager.WriteCount)),
            default);

        Assert.True(storage.Contains(taskId));
        Assert.Equal(1, await invoked.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task LocalChildReusesHandleCreatedWhileInitialWriteIsPending()
    {
        var (runtime, _, manager, _) = CreateRuntime();
        var taskId = TaskId.Parse("root/local");
        IScheduledTaskHandle? polledHandle = null;
        manager.BeforeWrite = () => polledHandle = runtime.GetScheduledTaskHandle(taskId);

        var scheduledHandle = await runtime.ScheduleChildAsync(
            taskId,
            new TestStateManager.PendingDurableTask(),
            default);

        Assert.Same(polledHandle, scheduledHandle);
    }

    [Fact]
    public async Task FailedLocalChildCommitPreventsUserInvocation()
    {
        var (runtime, _, manager, _) = CreateRuntime();
        var invoked = false;
        var taskId = TaskId.Parse("root/local");
        manager.BeforeWrite = () => throw new InvalidOperationException("Expected write failure.");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.ScheduleChildAsync(
                taskId,
                DurableTask.Run(_ => invoked = true),
                default).AsTask());

        Assert.Equal("Expected write failure.", exception.Message);
        Assert.False(invoked);
        Assert.Equal(0, manager.WriteCount);
    }

    [Fact]
    public async Task CleanupRecursivelyPrunesDescendantsBeforeTombstoningParent()
    {
        var (runtime, storage, manager, _) = CreateRuntime(TimeSpan.Zero);
        var rootId = TaskId.Parse("root");
        var childId = TaskId.Parse("root/child");
        var grandchildId = TaskId.Parse("root/child/grandchild");
        var waiter = GrainId.Create("caller", "one");
        var completedAt = DateTimeOffset.UtcNow.AddMinutes(-1);

        var root = storage.GetOrCreate(rootId);
        root.Result = DurableTaskResponse.Completed;
        root.CompletedAt = completedAt;
        root.RequestFingerprint = "root";
        root.CompletionDestinations.Add(waiter);
        var child = storage.GetOrCreate(childId);
        child.Result = DurableTaskResponse.Completed;
        child.CompletedAt = completedAt;
        child.RequestFingerprint = "child";
        var grandchild = storage.GetOrCreate(grandchildId);
        grandchild.Result = DurableTaskResponse.Completed;
        grandchild.CompletedAt = completedAt;
        manager.BeforeWrite = () =>
        {
            Assert.NotNull(storage.Get(rootId).TombstonedAt);
            Assert.NotNull(storage.Get(childId).TombstonedAt);
            Assert.False(storage.Contains(grandchildId));
        };

        await runtime.AcknowledgeCompletionAsync(rootId, waiter, default);

        Assert.NotNull(storage.Get(rootId).TombstonedAt);
        Assert.NotNull(storage.Get(childId).TombstonedAt);
        Assert.False(storage.Contains(grandchildId));
    }

    [Fact]
    public async Task CompletionPruningIsPersistedAfterHandleCompletion()
    {
        var (runtime, storage, manager, _) = CreateRuntime(TimeSpan.Zero);
        var taskId = TaskId.Parse("root/local");
        var observedPrunedWrite = false;
        manager.BeforeWrite = () =>
        {
            if (manager.WriteCount >= 2)
            {
                observedPrunedWrite = !storage.Contains(taskId);
            }
        };

        var handle = await runtime.ScheduleChildAsync(taskId, DurableTask.Run(_ => { }), default);
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, (await handle.WaitAsync(default)).Status);
        await WaitUntilAsync(() => manager.WriteCount >= 3);

        Assert.True(observedPrunedWrite);
        Assert.False(storage.Contains(taskId));
    }

    private static RuntimeTestDurableTaskRequest CreateRequest(
        int argument,
        Func<DurableTask>? createTask = null) =>
        new(
            createTask,
            interfaceName: "ITestGrain",
            methodName: "Execute",
            arguments: [argument])
        {
            Context = new DurableTaskRequestContext
            {
                CallerId = GrainId.Create("caller", "one"),
                TargetId = GrainId.Create("test", "one"),
                SupportsDurableCompletion = true,
            },
        };

    private static RuntimeTestDurableTaskRequest CreateRemoteRequest(int argument)
    {
        var request = CreateRequest(argument);
        request.Context!.TargetId = GrainId.Create("target", "one");
        return request;
    }

    private static TestDurableTaskRequest CreateSerializableRequest(GrainId target) =>
        new()
        {
            Context = new DurableTaskRequestContext
            {
                CallerId = GrainId.Create("caller", "one"),
                TargetId = target,
                SupportsDurableCompletion = true,
            },
        };

    private static IInboxHandlerContext CreateHandlerContext(GrainId grainId, DurableEnvelope envelope)
    {
        var context = Substitute.For<IInboxHandlerContext>();
        context.GrainId.Returns(grainId);
        context.Envelope.Returns(envelope);
        return context;
    }

    private static ValueTask<TaskId> InvokeSelectCompletionCoreAsync(
        GrainDurableExecutionContext context,
        TaskId decisionId,
        IReadOnlyList<TaskId> candidates,
        CancellationToken cancellationToken) =>
        (ValueTask<TaskId>)typeof(GrainDurableExecutionContext)
            .GetMethod("SelectCompletionCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(context, [decisionId, candidates, cancellationToken])!;

    private static TaskId InvokeCreateChildTaskId(GrainDurableExecutionContext context, string? name) =>
        (TaskId)typeof(GrainDurableExecutionContext)
            .GetMethod("CreateChildTaskId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(context, [name])!;

    private static DurableEnvelope CreateEnvelope<T>(
        GrainId sender,
        GrainId target,
        string route,
        T body,
        GrainId? replyTo = null)
    {
        var builder = new DurableEnvelopeBuilder(
                EnvelopeServiceProvider.GetRequiredService<SerializerSessionPool>(),
                sender)
            .To(target, route)
            .WithBody(body);
        if (replyTo is { } address)
        {
            builder.WithReplyTo(address);
        }

        return builder.Build();
    }

    private static IJobRunContext CreateRunContext(TaskId taskId, long generation)
    {
        var context = Substitute.For<IJobRunContext>();
        context.Job.Returns(new DurableJob
        {
            Id = Guid.NewGuid().ToString(),
            Name = DurableTaskMessageTransport.ResumeJobName,
            DueTime = DateTimeOffset.UtcNow,
            TargetGrainId = GrainId.Create("target", "one"),
            ShardId = "test",
            Metadata = new Dictionary<string, string>
            {
                [DurableTaskMessageTransport.ResumeTaskIdMetadata] = taskId.ToString(),
                [DurableTaskMessageTransport.ResumeGenerationMetadata] = generation.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
            },
        });
        return context;
    }

    private static (
        DurableTaskGrainRuntime Runtime,
        TestStorage Storage,
        TestStateManager Manager,
        RecordingDurableTaskMessageTransport Transport) CreateRuntime(
            TimeSpan? resultRetentionPeriod = null,
            bool initialize = true)
    {
        var manager = new TestStateManager();
        var storage = new TestStorage(manager);
        return CreateRuntime(storage, manager, resultRetentionPeriod, initialize);
    }

    private static (
        DurableTaskGrainRuntime Runtime,
        TestStorage Storage,
        TestStateManager Manager,
        RecordingDurableTaskMessageTransport Transport) CreateRuntime(
            TestStorage storage,
            TestStateManager manager,
            TimeSpan? resultRetentionPeriod = null,
            bool initialize = true)
    {
        var context = Substitute.For<IGrainContext>();
        context.GrainId.Returns(GrainId.Create("test", "one"));
        var accessor = Substitute.For<IGrainContextAccessor>();
        accessor.GrainContext.Returns(context);
        var shared = new DurableTaskGrainRuntimeShared(
            accessor,
            TimeProvider.System,
            NullLogger<DurableTaskGrainRuntime>.Instance,
            Options.Create(new DurableTaskOptions
            {
                ResultRetentionPeriod = resultRetentionPeriod ?? TimeSpan.FromHours(1),
            }),
            CreateSerializer());
        var transport = new RecordingDurableTaskMessageTransport();
        var runtime = new DurableTaskGrainRuntime(storage, shared, [transport], manager);
        if (initialize)
        {
            runtime.InitializeForActivation();
        }
        manager.RegisterObserver(runtime);
        return (runtime, storage, manager, transport);
    }

    private static Serializer CreateSerializer()
    {
        var services = new ServiceCollection();
        services.AddSerializer(builder => builder.AddAssembly(typeof(DurableTaskRuntimeInvariantTests).Assembly));
        return services.BuildServiceProvider().GetRequiredService<Serializer>();
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class ShutdownProbeRuntime : IDurableTaskGrainRuntime
    {
        private readonly TaskCompletionSource<CancellationToken> _schedulingStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
        public Task<CancellationToken> SchedulingStarted => _schedulingStarted.Task;

        public ValueTask<DurableTaskResponse> ScheduleDelayAsync(
            TaskId taskId,
            DateTimeOffset dueTime,
            CancellationToken cancellationToken)
        {
            _schedulingStarted.TrySetResult(cancellationToken);
            return WaitForCancellationAsync(cancellationToken);
        }

        public ValueTask<IScheduledTaskHandle> ScheduleChildAsync(
            TaskId taskId,
            DurableTask taskDefinition,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DurableTaskResponse> ScheduleRemoteAsync(
            TaskId taskId,
            IDurableTaskRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask CancelRemoteAsync(
            TaskId taskId,
            GrainId target,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<TaskId> SelectCompletionAsync(
            TaskId decisionId,
            IReadOnlyList<TaskId> candidates,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public IScheduledTaskHandle GetScheduledTaskHandle(TaskId taskId) => throw new NotSupportedException();

        private static async ValueTask<DurableTaskResponse> WaitForCancellationAsync(
            CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return DurableTaskResponse.Completed;
            }
            catch (OperationCanceledException exception)
            {
                return DurableTaskResponse.FromCanceled(exception);
            }
        }
    }

    private sealed class SchedulerProbeRuntime(TaskScheduler scheduler) : IDurableTaskGrainRuntime
    {
        private int _schedulerInvocationCount;

        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
        public int SchedulerInvocationCount => Volatile.Read(ref _schedulerInvocationCount);

        public ValueTask<IScheduledTaskHandle> ScheduleChildAsync(
            TaskId taskId,
            DurableTask taskDefinition,
            CancellationToken cancellationToken)
        {
            AssertScheduler();
            return new(new SchedulerProbeHandle(taskId));
        }

        public ValueTask<TaskId> SelectCompletionAsync(
            TaskId decisionId,
            IReadOnlyList<TaskId> candidates,
            CancellationToken cancellationToken)
        {
            AssertScheduler();
            return new(candidates[0]);
        }

        public ValueTask<DurableTaskResponse> ScheduleDelayAsync(
            TaskId taskId,
            DateTimeOffset dueTime,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DurableTaskResponse> ScheduleRemoteAsync(
            TaskId taskId,
            IDurableTaskRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask CancelRemoteAsync(
            TaskId taskId,
            GrainId target,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public IScheduledTaskHandle GetScheduledTaskHandle(TaskId taskId) =>
            new SchedulerProbeHandle(taskId);

        private void AssertScheduler()
        {
            Assert.Same(scheduler, TaskScheduler.Current);
            Interlocked.Increment(ref _schedulerInvocationCount);
        }
    }

    private sealed class SchedulerProbeHandle(TaskId taskId) : IScheduledTaskHandle
    {
        public TaskId TaskId { get; } = taskId;

        public ValueTask CancelAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<DurableTaskResponse> PollAsync(
            PollingOptions options,
            CancellationToken cancellationToken) => new(DurableTaskResponse.Completed);

        public ValueTask<DurableTaskResponse> WaitAsync(CancellationToken cancellationToken) =>
            new(DurableTaskResponse.Completed);
    }

    private sealed class TestStorage(TestStateManager manager) : IDurableTaskGrainStorage
    {
        private readonly Dictionary<TaskId, DurableTaskState> _states = [];

        public IEnumerable<(TaskId Id, IDurableTaskState State)> Tasks =>
            _states.Select(entry => (entry.Key, (IDurableTaskState)entry.Value));

        public DurableTaskState Get(TaskId id) => _states[id];
        public bool Contains(TaskId id) => _states.ContainsKey(id);
        public DurableTaskState GetOrCreate(TaskId id) => (DurableTaskState)GetOrCreateTask(id, null);

        public IEnumerable<(TaskId Id, IDurableTaskState State)> GetChildren(TaskId task) =>
            Tasks.Where(entry => task.IsParentOf(entry.Id));

        public IDurableTaskState GetOrCreateTask(TaskId taskId, IDurableTaskRequest? request)
        {
            if (!_states.TryGetValue(taskId, out var state))
            {
                state = new DurableTaskState
                {
                    Request = request,
                    CreatedAt = DateTimeOffset.UtcNow,
                };
                _states.Add(taskId, state);
            }

            return state;
        }

        public void SetRequest(TaskId taskId, IDurableTaskState state, IDurableTaskRequest request) =>
            ((DurableTaskState)state).Request = request;

        public void SetRequestFingerprint(TaskId taskId, IDurableTaskState state, string fingerprint) =>
            ((DurableTaskState)state).RequestFingerprint = fingerprint;

        public void SetRemoteRequest(TaskId taskId, IDurableTaskState state, GrainId target, string fingerprint)
        {
            ((DurableTaskState)state).RemoteTarget = target;
            ((DurableTaskState)state).RemoteRequestFingerprint = fingerprint;
        }

        public void SetCallerId(TaskId taskId, IDurableTaskState state, GrainId callerId) =>
            ((DurableTaskState)state).CallerId = callerId;

        public void SetResponse(TaskId taskId, IDurableTaskState state, DurableTaskResponse response)
        {
            ((DurableTaskState)state).Result = response;
            ((DurableTaskState)state).CompletedAt = DateTimeOffset.UtcNow;
            ((DurableTaskState)state).DueTime = null;
            if (((DurableTaskState)state).ResumeGeneration > 0)
            {
                ((DurableTaskState)state).ResumeGeneration++;
            }
        }

        public void RequestCancellation(TaskId taskId, IDurableTaskState state) =>
            ((DurableTaskState)state).CancellationRequestedAt ??= DateTimeOffset.UtcNow;

        public void SetDelay(TaskId taskId, IDurableTaskState state, DateTimeOffset dueTime, long generation)
        {
            ((DurableTaskState)state).DueTime = dueTime;
            ((DurableTaskState)state).ResumeGeneration = generation;
        }

        public void AddCompletionDestination(TaskId taskId, IDurableTaskState state, GrainId destination) =>
            ((DurableTaskState)state).CompletionDestinations.Add(destination);

        public void RemoveCompletionDestination(TaskId taskId, IDurableTaskState state, GrainId destination) =>
            ((DurableTaskState)state).CompletionDestinations.Remove(destination);

        public void CreateTombstone(TaskId taskId, IDurableTaskState state)
        {
            ((DurableTaskState)state).Request = null;
            ((DurableTaskState)state).Result = null;
            ((DurableTaskState)state).TombstonedAt = DateTimeOffset.UtcNow;
        }

        public bool TryGetTask(TaskId taskId, [NotNullWhen(true)] out IDurableTaskState? state)
        {
            var found = _states.TryGetValue(taskId, out var value);
            state = value;
            return found;
        }

        public bool RemoveTask(TaskId taskId) => _states.Remove(taskId);
        public void Clear() => _states.Clear();
        public ValueTask WriteAsync(CancellationToken cancellationToken) => manager.WriteStateAsync(cancellationToken);
        public ValueTask ReadAsync(CancellationToken cancellationToken) => default;
    }

    private sealed class TestStateManager : IJournaledStateManager
    {
        private readonly List<IJournaledStateObserver> _observers = [];
        public int WriteCount { get; private set; }
        public long PendingWriteByteCount { get; set; }
        public Action? BeforeWrite { get; set; }
        public Func<ValueTask>? AfterWriteStarted { get; set; }
        public bool SupportsRollback => true;
        public void RegisterObserver(IJournaledStateObserver observer) => _observers.Add(observer);
        public ValueTask InitializeAsync(CancellationToken cancellationToken) => default;
        public void RegisterState(string name, IJournaledState state) { }
        public bool TryGetState(string name, [NotNullWhen(true)] out IJournaledState? state)
        {
            state = null;
            return false;
        }

        public async ValueTask WriteStateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeforeWrite?.Invoke();
            foreach (var observer in _observers)
            {
                await observer.OnWritePreparingAsync(cancellationToken);
                observer.OnWriteStarted();
            }

            if (AfterWriteStarted is { } afterWriteStarted)
            {
                await afterWriteStarted();
            }

            WriteCount++;
            foreach (var observer in _observers)
            {
                observer.OnWriteCompleted();
            }
        }

        internal sealed class PendingDurableTask : DurableTask
        {
            protected override ValueTask<DurableTaskResponse> RunAsync(DurableExecutionContext context) =>
                new(DurableTaskResponse.Pending);
        }

        internal sealed class TestRemoteDurableTask(IDurableTaskRequest request) : DurableTask, ISchedulableTask, IDurableTaskRequest
        {
            private int _scheduleAsyncCallCount;

            public DurableTaskRequestContext? Context => request.Context;
            public int ScheduleAsyncCallCount => Volatile.Read(ref _scheduleAsyncCallCount);
            public InvokeMethodOptions Options => request.Options;
            public DurableTask CreateTask() => request.CreateTask();
            public object GetTarget() => request.GetTarget()!;
            public void SetTarget(ITargetHolder holder) => request.SetTarget(holder);
            public ValueTask<Response> Invoke() => request.Invoke();
            public int GetArgumentCount() => request.GetArgumentCount();
            public object GetArgument(int index) => request.GetArgument(index)!;
            public void SetArgument(int index, object value) => request.SetArgument(index, value);
            public void Dispose() => request.Dispose();
            public string GetMethodName() => request.GetMethodName();
            public string GetInterfaceName() => request.GetInterfaceName();
            public string GetActivityName() => request.GetActivityName();
            public Type GetInterfaceType() => request.GetInterfaceType();
            public System.Reflection.MethodInfo GetMethod() => request.GetMethod();
            public void AddInvokeMethodOptions(InvokeMethodOptions options) => request.AddInvokeMethodOptions(options);
            public ValueTask<DurableTaskResponse> ScheduleAsync(TaskId taskId, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _scheduleAsyncCallCount);
                return new(DurableTaskResponse.Pending);
            }
            public IScheduledTaskHandle GetHandle(TaskId taskId) => throw new NotSupportedException();
            protected override ValueTask<DurableTaskResponse> RunAsync(DurableExecutionContext context) =>
                throw new NotSupportedException();
        }

        public ValueTask RevertPendingChangesAsync(CancellationToken cancellationToken)
        {
            foreach (var observer in _observers)
            {
                observer.OnRecoveryCompleted();
            }

            return default;
        }

        public ValueTask DeleteStateAsync(CancellationToken cancellationToken) => default;
    }
}
