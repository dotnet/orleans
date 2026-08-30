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
using Orleans.Storage;
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
            () => runtime.SignalCancellationAsync(TaskId.Parse("root"), TestContext.Current.CancellationToken).AsTask());

        Assert.Contains(nameof(DurableGrain), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OrdinaryGrainActivationRejectsPollingAndDiagnostics()
    {
        var (runtime, _, _, _) = CreateRuntime(initialize: false);
        var extension = (IDurableTaskGrainExtension)runtime;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.SubscribeOrPollAsync(TaskId.Parse("root"), default, TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await extension.GetTasksAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken).MoveNextAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await extension.GetRunningTasksAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken).MoveNextAsync());
    }

    [Fact]
    public async Task InboxSchedulingStartsOnlyAfterCommit()
    {
        var (runtime, _, manager, _) = CreateRuntime();
        var invoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = CreateRequest(1, () => DurableTask.Run(_ => invoked.TrySetResult()));
        var taskId = TaskId.Parse("root");

        var response = await runtime.ScheduleFromInboxAsync(taskId, request, TestContext.Current.CancellationToken);

        Assert.Equal(DurableTaskResponseKind.Subscribed, response.ResponseKind);
        Assert.False(invoked.Task.IsCompleted);
        Assert.Equal(0, manager.WriteCount);

        await manager.WriteStateAsync(TestContext.Current.CancellationToken);

        await invoked.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(1, request.CreateTaskCallCount);
    }

    [Fact]
    public async Task DeferredStartRechecksCommittedCancellationBeforeInvocation()
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var request = CreateRequest(1);
        var taskId = TaskId.Parse("root");
        await runtime.ScheduleFromInboxAsync(taskId, request, TestContext.Current.CancellationToken);
        manager.AfterWriteStarted = () =>
        {
            manager.AfterWriteStarted = null;
            var state = storage.Get(taskId);
            storage.RequestCancellation(taskId, state);
            return default;
        };

        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
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

        await runtime.ScheduleFromInboxAsync(taskId, first, TestContext.Current.CancellationToken);
        await runtime.ScheduleFromInboxAsync(taskId, retry, TestContext.Current.CancellationToken);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => first.CreateTaskCallCount + retry.CreateTaskCallCount == 1);

        Assert.Equal(1, first.CreateTaskCallCount + retry.CreateTaskCallCount);
    }

    [Fact]
    public async Task ConflictingRequestForSameTaskIdFailsBeforeExecution()
    {
        var (runtime, _, _, _) = CreateRuntime();
        var taskId = TaskId.Parse("root");
        await runtime.ScheduleFromInboxAsync(taskId, CreateRequest(1), TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.ScheduleFromInboxAsync(taskId, CreateRequest(2), TestContext.Current.CancellationToken).AsTask());

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
            async () => await handler.HandleAsync(wrongTarget, TestContext.Current.CancellationToken));
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
            async () => await handler.HandleAsync(wrongReply, TestContext.Current.CancellationToken));
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

        await handler.HandleAsync(CreateHandlerContext(
                receiver,
                CreateEnvelope(
                    caller,
                    receiver,
                    DurableTaskMessageTransport.CancellationRoute,
                    new DurableTaskCancellationMessage { TaskId = taskId })), TestContext.Current.CancellationToken);

        Assert.Equal(caller, storage.Get(taskId).CallerId);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        var cancellationException = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await handler.HandleAsync(CreateHandlerContext(
                    receiver,
                    CreateEnvelope(
                        otherCaller,
                        receiver,
                        DurableTaskMessageTransport.CancellationRoute,
                        new DurableTaskCancellationMessage { TaskId = taskId })), TestContext.Current.CancellationToken));
        Assert.Contains("already associated with caller", cancellationException.Message, StringComparison.Ordinal);

        var request = CreateSerializableRequest(receiver);
        var invocationException = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await handler.HandleAsync(CreateHandlerContext(
                    receiver,
                    CreateEnvelope(
                        otherCaller,
                        receiver,
                        DurableTaskMessageTransport.InvocationRoute,
                        new DurableTaskInvocationMessage { TaskId = taskId, Request = request },
                        replyTo: otherCaller)), TestContext.Current.CancellationToken));
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

        await handler.HandleAsync(CreateHandlerContext(
                receiver,
                CreateEnvelope(
                    caller,
                    receiver,
                    DurableTaskMessageTransport.CancellationRoute,
                    new DurableTaskCancellationMessage { TaskId = rootId })), TestContext.Current.CancellationToken);

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
        await runtime.ScheduleFromInboxAsync(taskId, request, TestContext.Current.CancellationToken);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var writesBeforeCancellation = manager.WriteCount;
        var handler = (IInboxHandler)new DurableTaskMessageHandler(runtime);

        await handler.HandleAsync(CreateHandlerContext(
                receiver,
                CreateEnvelope(
                    caller,
                    receiver,
                    DurableTaskMessageTransport.CancellationRoute,
                    new DurableTaskCancellationMessage { TaskId = taskId })), TestContext.Current.CancellationToken);

        Assert.Equal(writesBeforeCancellation, manager.WriteCount);
        Assert.Null(storage.Get(taskId).Result);

        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
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
        var childHandle = await runtime.ScheduleChildAsync(childId, DurableTask.Run(async cancellationToken =>
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
                        TimeSpan.Zero,
                        default);
                    throw;
                }
            }), TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var handler = (IInboxHandler)new DurableTaskMessageHandler(runtime);

        await handler.HandleAsync(CreateHandlerContext(
                receiver,
                CreateEnvelope(
                    caller,
                    receiver,
                    DurableTaskMessageTransport.CancellationRoute,
                    new DurableTaskCancellationMessage { TaskId = rootId })), TestContext.Current.CancellationToken);
        Assert.False((await childHandle.PollAsync(new PollingOptions { PollTimeout = TimeSpan.Zero }, TestContext.Current.CancellationToken)).IsCompleted);

        await manager.WriteStateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            DurableTaskStatus.Canceled,
            (await childHandle.WaitAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task CompletionRequiresRecordedRemoteTarget()
    {
        var (runtime, storage, _, transport) = CreateRuntime();
        var receiver = GrainId.Create("test", "one");
        var request = CreateRequest(1);
        var expectedTarget = request.Context!.TargetId;
        var taskId = TaskId.Parse("root/remote");
        await runtime.ScheduleChildAsync(taskId, new TestStateManager.TestRemoteDurableTask(request), TestContext.Current.CancellationToken);
        var handler = (IInboxHandler)new DurableTaskMessageHandler(runtime);
        var completion = new DurableTaskCompletionMessage
        {
            TaskId = taskId,
            Response = DurableTaskResponse.FromResult(42),
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await handler.HandleAsync(CreateHandlerContext(
                    receiver,
                    CreateEnvelope(
                        GrainId.Create("target", "other"),
                        receiver,
                        DurableTaskMessageTransport.CompletionRoute,
                        completion)), TestContext.Current.CancellationToken));
        Assert.Contains("does not accept completions", exception.Message, StringComparison.Ordinal);
        Assert.Null(storage.Get(taskId).Result);
        Assert.Empty(transport.CompletionAcknowledgements);

        await handler.HandleAsync(CreateHandlerContext(
                receiver,
                CreateEnvelope(
                    expectedTarget,
                    receiver,
                    DurableTaskMessageTransport.CompletionRoute,
                    completion)), TestContext.Current.CancellationToken);

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

        var response = await runtime.ScheduleFromInboxAsync(taskId, request, TestContext.Current.CancellationToken);

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

        await runtime.ScheduleRemoteAsync(taskId, request, TestContext.Current.CancellationToken);

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

        var response = await runtime.ScheduleRemoteAsync(taskId, request, TestContext.Current.CancellationToken);

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
            () => runtime.ScheduleRemoteAsync(taskId, request, TestContext.Current.CancellationToken).AsTask());

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

        await runtime.AcceptResponseAsync(taskId, DurableTaskResponse.FromResult(42), target, TestContext.Current.CancellationToken, persist: false);

        Assert.False((await handle.PollAsync(new PollingOptions { PollTimeout = TimeSpan.Zero }, TestContext.Current.CancellationToken)).IsCompleted);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(42, (await handle.WaitAsync(TestContext.Current.CancellationToken)).GetResult<int>());
    }

    [Fact]
    public async Task InboxCompletionPersistsEligiblePruningAfterOuterWrite()
    {
        var (runtime, storage, manager, transport) = CreateRuntime(TimeSpan.Zero);
        var taskId = TaskId.Parse("root/remote");
        var target = GrainId.Create("target", "one");
        var state = storage.GetOrCreate(taskId);
        storage.SetRemoteRequest(taskId, state, target, "fingerprint");
        var pruningWriteCompleted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.AfterWriteCompleted = writeCount =>
        {
            if (writeCount == 2)
            {
                pruningWriteCompleted.TrySetResult(writeCount);
            }
        };

        await runtime.AcceptResponseAsync(taskId, DurableTaskResponse.FromResult(42), target, TestContext.Current.CancellationToken, persist: false);
        Assert.True(storage.Contains(taskId));

        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.True(
            await pruningWriteCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken) >= 2);

        var tombstone = storage.Get(taskId);
        Assert.Same(state, tombstone);
        Assert.Null(tombstone.RequestFingerprint);
        Assert.Equal("fingerprint", tombstone.RemoteRequestFingerprint);
        Assert.Equal(target, tombstone.RemoteTarget);
        Assert.NotNull(tombstone.TombstonedAt);
        Assert.Null(tombstone.Request);
        Assert.Null(tombstone.Result);
        Assert.Empty(tombstone.CompletionDestinations);
        Assert.Empty(transport.Invocations);
        Assert.Equal(2, manager.WriteCount);
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
        var pruningWriteCompleted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.AfterWriteCompleted = writeCount =>
        {
            if (writeCount == 2 && second.TombstonedAt.HasValue)
            {
                pruningWriteCompleted.TrySetResult(writeCount);
            }
        };
        await runtime.AcceptResponseAsync(firstId, DurableTaskResponse.FromResult(1), target, TestContext.Current.CancellationToken, persist: false);
        manager.AfterWriteStarted = () => runtime.AcceptResponseAsync(
            secondId,
            DurableTaskResponse.FromResult(2),
            target,
            default,
            persist: false);

        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        manager.AfterWriteStarted = null;

        Assert.Equal(
            2,
            (await secondHandle.WaitAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).GetResult<int>());
        Assert.Equal(
            2,
            await pruningWriteCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.NotNull(second.TombstonedAt);
        Assert.Same(second, storage.Get(secondId));
        Assert.Null(second.RequestFingerprint);
        Assert.Equal("second", second.RemoteRequestFingerprint);
        Assert.Equal(target, second.RemoteTarget);
        Assert.Null(second.Request);
        Assert.Null(second.Result);
        Assert.Empty(second.CompletionDestinations);
    }

    [Fact]
    public async Task RecoveryDiscardsProvisionalCompletionHandle()
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var taskId = TaskId.Parse("root/remote");
        var target = GrainId.Create("target", "one");
        var state = storage.GetOrCreate(taskId);
        storage.SetRemoteRequest(taskId, state, target, "fingerprint");

        await runtime.AcceptResponseAsync(taskId, DurableTaskResponse.FromResult(42), target, TestContext.Current.CancellationToken, persist: false);
        var handle = runtime.GetScheduledTaskHandle(taskId);
        state.Result = null;
        state.CompletedAt = null;
        await manager.RevertPendingChangesAsync(TestContext.Current.CancellationToken);

        Assert.False((await handle.PollAsync(new PollingOptions { PollTimeout = TimeSpan.Zero }, TestContext.Current.CancellationToken)).IsCompleted);
    }

    [Fact]
    public async Task LongPollDoesNotBlockCancellation()
    {
        var (runtime, storage, _, _) = CreateRuntime();
        var taskId = TaskId.Parse("root");
        storage.GetOrCreate(taskId);
        var polling = runtime.SubscribeOrPollAsync(taskId, new SubscribeOrPollOptions { PollTimeout = TimeSpan.FromMinutes(1) }, TestContext.Current.CancellationToken).AsTask();

        await runtime.SignalCancellationAsync(taskId, TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(DurableTaskStatus.Canceled, (await polling.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).Status);
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

        await handler.HandleAsync(CreateHandlerContext(
                receiver,
                CreateEnvelope(
                    caller,
                    receiver,
                    DurableTaskMessageTransport.CancellationRoute,
                    new DurableTaskCancellationMessage { TaskId = taskId })), TestContext.Current.CancellationToken);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        var request = CreateSerializableRequest(receiver);
        await handler.HandleAsync(CreateHandlerContext(
                receiver,
                CreateEnvelope(
                    caller,
                    receiver,
                    DurableTaskMessageTransport.InvocationRoute,
                    new DurableTaskInvocationMessage { TaskId = taskId, Request = request },
                    replyTo: caller)), TestContext.Current.CancellationToken);

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

        await runtime.SignalCancellationAsync(taskId, TestContext.Current.CancellationToken);
        var response = await ((IDurableTaskServer)runtime).ScheduleAsync(taskId, request, TestContext.Current.CancellationToken);

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
            () => ((IDurableTaskServer)runtime).ScheduleAsync(taskId, request, TestContext.Current.CancellationToken).AsTask());

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

        _ = await ((IDurableTaskServer)runtime).ScheduleAsync(taskId, request, TestContext.Current.CancellationToken);

        Assert.Equal(default, storage.Get(taskId).CallerId);
        Assert.Empty(storage.Get(taskId).CompletionDestinations);
    }

    [Fact]
    public async Task StaleResumeGenerationCannotCompleteDelay()
    {
        var (runtime, storage, _, _) = CreateRuntime();
        var taskId = TaskId.Parse("root/delay");
        await runtime.ScheduleDelayAsync(taskId, TimeSpan.Zero, TestContext.Current.CancellationToken);
        var state = storage.Get(taskId);

        var stale = CreateRunContext(taskId, state.ResumeGeneration + 1);
        Assert.Same(DurableJobRunResult.Completed, await runtime.ExecuteJobAsync(stale, TestContext.Current.CancellationToken));
        Assert.Null(storage.Get(taskId).Result);

        var current = CreateRunContext(taskId, state.ResumeGeneration);
        Assert.Same(DurableJobRunResult.Completed, await runtime.ExecuteJobAsync(current, TestContext.Current.CancellationToken));
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, storage.Get(taskId).Result!.Status);
    }

    [Fact]
    public async Task DelayReplayUsesPersistedDueTimeAndValidatesDuration()
    {
        var (runtime, storage, _, transport) = CreateRuntime();
        var taskId = TaskId.Parse("root/replayed-delay");
        var duration = TimeSpan.FromMinutes(5);

        await runtime.ScheduleDelayAsync(
            taskId,
            duration,
            TestContext.Current.CancellationToken);
        var persistedDueTime = storage.Get(taskId).DueTime;
        await Task.Delay(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken);

        var replay = await runtime.ScheduleDelayAsync(
            taskId,
            duration,
            TestContext.Current.CancellationToken);

        Assert.Same(DurableTaskResponse.Pending, replay);
        Assert.Equal(persistedDueTime, storage.Get(taskId).DueTime);
        Assert.Equal(duration, storage.Get(taskId).DelayDuration);
        Assert.Single(transport.ScheduledResumes);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.ScheduleDelayAsync(
                taskId,
                TimeSpan.FromMinutes(6),
                TestContext.Current.CancellationToken).AsTask());
        Assert.Contains("duration", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingResumeStateWithUnsupportedPendingWriteSamplingReschedules()
    {
        var (runtime, _, manager, _) = CreateRuntime();
        manager.PendingWriteByteCount = -1;
        var result = await runtime.ExecuteJobAsync(CreateRunContext(TaskId.Parse("root/missing"), generation: 1), TestContext.Current.CancellationToken);

        Assert.NotSame(DurableJobRunResult.Completed, result);
    }

    [Fact]
    public async Task PermanentlyMissingResumeStateEventuallyCompletes()
    {
        var (runtime, _, manager, _) = CreateRuntime();
        manager.PendingWriteByteCount = -1;
        var context = CreateRunContext(TaskId.Parse("root/missing"), generation: 1);
        Assert.NotSame(DurableJobRunResult.Completed, await runtime.ExecuteJobAsync(context, TestContext.Current.CancellationToken));
        var result = await runtime.ExecuteJobAsync(context, TestContext.Current.CancellationToken);

        Assert.Same(DurableJobRunResult.Completed, result);
    }

    [Fact]
    public async Task LocalDelayRemainsPendingUntilResumeAndRecoveryReschedulesIt()
    {
        var (runtime, storage, _, transport) = CreateRuntime();
        var localTaskId = TaskId.Parse("root/local");
        var localHandle = await runtime.ScheduleChildAsync(localTaskId, new TestStateManager.PendingDurableTask(), TestContext.Current.CancellationToken);
        Assert.Equal(DurableTaskStatus.Pending, (await localHandle.PollAsync(default, TestContext.Current.CancellationToken)).Status);
        Assert.Null(storage.Get(localTaskId).Result);
        Assert.Null(storage.Get(localTaskId).CompletedAt);

        var taskId = TaskId.Parse("root/delay");
        Assert.Equal(
            DurableTaskStatus.Pending,
            (await runtime.ScheduleDelayAsync(taskId, TimeSpan.Zero, TestContext.Current.CancellationToken)).Status);
        var handle = runtime.GetScheduledTaskHandle(taskId);

        Assert.Equal(
            DurableTaskStatus.Pending,
            (await handle.PollAsync(default, TestContext.Current.CancellationToken)).Status);
        Assert.Null(storage.Get(taskId).Result);
        Assert.Null(storage.Get(taskId).CompletedAt);

        transport.ScheduledResumes.Clear();
        await runtime.ResumePendingTasksAsync(TestContext.Current.CancellationToken);
        var resume = Assert.Single(transport.ScheduledResumes);
        Assert.Equal(taskId, resume.TaskId);
        Assert.Null(storage.Get(taskId).CompletedAt);

        var state = storage.Get(taskId);
        Assert.Same(DurableJobRunResult.Completed, await runtime.ExecuteJobAsync(CreateRunContext(taskId, state.ResumeGeneration), TestContext.Current.CancellationToken));
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, (await handle.WaitAsync(TestContext.Current.CancellationToken)).Status);
        Assert.NotNull(storage.Get(taskId).CompletedAt);
    }

    [Fact]
    public async Task RecoveredCanceledDelayTerminalizesBeforeRescheduleAndStaleResumesAreHarmless()
    {
        var (runtime, storage, _, transport) = CreateRuntime();
        var taskId = TaskId.Parse("root/delay");
        await runtime.ScheduleDelayAsync(taskId, TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
        var generation = storage.Get(taskId).ResumeGeneration;

        storage.Get(taskId).CancellationRequestedAt = runtime.UtcNow;
        transport.ScheduledResumes.Clear();
        await runtime.ResumePendingTasksAsync(TestContext.Current.CancellationToken);

        var canceled = storage.Get(taskId);
        Assert.Equal(DurableTaskStatus.Canceled, canceled.Result!.Status);
        Assert.NotNull(canceled.CompletedAt);
        Assert.Null(canceled.DueTime);
        Assert.True(canceled.ResumeGeneration > generation);
        Assert.Empty(transport.ScheduledResumes);

        await runtime.ExecuteJobAsync(CreateRunContext(taskId, generation), TestContext.Current.CancellationToken);
        await runtime.ExecuteJobAsync(CreateRunContext(taskId, generation), TestContext.Current.CancellationToken);
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

        var response = await ((IDurableTaskServer)runtime).ScheduleAsync(taskId, request, TestContext.Current.CancellationToken);
        Assert.Equal(DurableTaskResponseKind.Pending, response.ResponseKind);
        await WaitUntilAsync(() => storage.Get(taskId).Result is { IsCompleted: true });

        Assert.Empty(storage.Get(taskId).CompletionDestinations);
        Assert.Empty(transport.Completions);
        Assert.Equal(
            DurableTaskStatus.CompletedSuccessfully,
            (await runtime.SubscribeOrPollAsync(taskId, default, TestContext.Current.CancellationToken)).Status);
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

        await runtime.ScheduleChildAsync(childId, new TestStateManager.TestRemoteDurableTask(request), TestContext.Current.CancellationToken);
        var child = storage.Get(childId);
        var fingerprint = child.RemoteRequestFingerprint;
        Assert.Equal(target, child.RemoteTarget);
        Assert.NotNull(fingerprint);

        var (recovered, _, _, recoveredTransport) = CreateRuntime(storage, manager);
        manager.BeforeWrite = () => Assert.Contains(
            recoveredTransport.Cancellations,
            cancellation => cancellation.TaskId == childId && cancellation.Target == target);
        await recovered.SignalCancellationAsync(rootId, TestContext.Current.CancellationToken);

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

        await runtime.CancelRemoteAsync(taskId, target, TestContext.Current.CancellationToken);

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

        await recovered.ResumePendingTasksAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, manager.WriteCount);
        Assert.Equal(DurableTaskStatus.Canceled, storage.Get(childId).Result!.Status);
    }

    [Fact]
    public async Task LocalAndRemoteRequestIdentitiesCannotShareTaskId()
    {
        var (runtime, storage, _, _) = CreateRuntime();
        var taskId = TaskId.Parse("identity");
        await runtime.ScheduleFromInboxAsync(taskId, CreateRequest(1), TestContext.Current.CancellationToken);

        var remoteException = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.ScheduleChildAsync(taskId, new TestStateManager.TestRemoteDurableTask(CreateRemoteRequest(2)), TestContext.Current.CancellationToken).AsTask());
        Assert.Contains("different request", remoteException.Message, StringComparison.Ordinal);

        var remoteTaskId = TaskId.Parse("remote-identity");
        await runtime.ScheduleChildAsync(remoteTaskId, new TestStateManager.TestRemoteDurableTask(CreateRemoteRequest(3)), TestContext.Current.CancellationToken);
        var topLevelException = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.ScheduleFromInboxAsync(remoteTaskId, CreateRequest(4), TestContext.Current.CancellationToken).AsTask());
        Assert.Contains("remote child request", topLevelException.Message, StringComparison.Ordinal);
        Assert.NotNull(storage.Get(remoteTaskId).RemoteRequestFingerprint);

        var delayTaskId = TaskId.Parse("delay-identity");
        await runtime.ScheduleDelayAsync(delayTaskId, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
        var delayException = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.ScheduleChildAsync(delayTaskId, new TestStateManager.TestRemoteDurableTask(CreateRemoteRequest(5)), TestContext.Current.CancellationToken).AsTask());
        Assert.Contains("different request", delayException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunningLocalChildRejectsRemoteReuseWithoutChangingIdentity()
    {
        var (runtime, storage, _, _) = CreateRuntime();
        var taskId = TaskId.Parse("root/local");
        var localTask = new TestSchedulableTask(
            static (_, _) => new(DurableTaskResponse.Pending));
        _ = await runtime.ScheduleChildAsync(taskId, localTask, TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.ScheduleChildAsync(taskId, new TestStateManager.TestRemoteDurableTask(CreateRemoteRequest(2)), TestContext.Current.CancellationToken).AsTask());

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
            () => runtime.ScheduleChildAsync(taskId, task, TestContext.Current.CancellationToken).AsTask());
        _ = await runtime.ScheduleChildAsync(taskId, task, TestContext.Current.CancellationToken);

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

        var handle = await runtime.ScheduleChildAsync(taskId, task, TestContext.Current.CancellationToken);

        Assert.Equal(DurableTaskStatus.Canceled, (await handle.WaitAsync(TestContext.Current.CancellationToken)).Status);
        Assert.Equal(0, task.ScheduleAsyncCallCount);
    }

    [Fact]
    public async Task PersistedRemoteChildCancellationTerminalizesBeforeResend()
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var taskId = TaskId.Parse("root/remote");
        var request = CreateRemoteRequest(1);
        var original = new TestStateManager.TestRemoteDurableTask(request);
        _ = await runtime.ScheduleChildAsync(taskId, original, TestContext.Current.CancellationToken);
        storage.RequestCancellation(taskId, storage.Get(taskId));
        var (recovered, _, _, transport) = CreateRuntime(storage, manager);
        var retry = new TestStateManager.TestRemoteDurableTask(CreateRemoteRequest(1));

        var handle = await recovered.ScheduleChildAsync(taskId, retry, TestContext.Current.CancellationToken);

        Assert.Equal(DurableTaskStatus.Canceled, (await handle.WaitAsync(TestContext.Current.CancellationToken)).Status);
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
        var handle = await runtime.ScheduleChildAsync(taskId, DurableTask.Run(async cancellationToken =>
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
            }), TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var writesBeforeCancellation = stateManager.WriteCount;

        await handle.CancelAsync(TestContext.Current.CancellationToken);

        var observedWriteCount = await writeCountObservedDuringCancellation.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
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

        await ((IDurableTaskServer)runtime).ScheduleAsync(taskId, request, TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await transport.ScheduledResume.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var stopping = runtime.StopAsync(TestContext.Current.CancellationToken);
        await caughtShutdown.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await stopping.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

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

        await ((IDurableTaskServer)runtime).ScheduleAsync(taskId, request, TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await transport.ScheduledResume.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var stopping = runtime.StopAsync(TestContext.Current.CancellationToken);
        await stopping.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Null(storage.Get(taskId).Result);
        Assert.Empty(transport.Completions);
    }

    [Fact]
    public async Task RecoveryCancelsStaleExecutionAndRestartsRecoveredPendingRequest()
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var taskId = TaskId.Parse("root/recovery-restart");
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempt = 0;
        var request = CreateRequest(
            1,
            () => DurableTask.Run(async cancellationToken =>
            {
                if (Interlocked.Increment(ref attempt) == 1)
                {
                    firstStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return 0;
                }

                secondStarted.TrySetResult();
                return 42;
            }));

        await runtime.ScheduleFromInboxAsync(taskId, request, TestContext.Current.CancellationToken);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        runtime.OnRecoveryCompleted();

        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var response = await runtime.GetScheduledTaskHandle(taskId)
            .WaitAsync(TestContext.Current.CancellationToken)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(2, attempt);
        Assert.Equal(42, response.GetResult<int>());
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, storage.Get(taskId).Result!.Status);
    }

    [Fact]
    public async Task RecoveryRestartsPendingRequestAfterRecoveryTriggeringExecutionFaults()
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var taskId = TaskId.Parse("root/recovery-trigger-fault");
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempt = 0;
        var request = CreateRequest(
            1,
            () => DurableTask.Run(async _ =>
            {
                if (Interlocked.Increment(ref attempt) == 1)
                {
                    await releaseFirst.Task;
                    return 1;
                }

                secondStarted.TrySetResult();
                return 42;
            }));

        await runtime.ScheduleFromInboxAsync(taskId, request, TestContext.Current.CancellationToken);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        manager.AfterWriteStarted = () =>
        {
            manager.AfterWriteStarted = null;
            storage.Get(taskId).Result = null;
            runtime.OnRecoveryCompleted();
            return ValueTask.FromException(
                new InconsistentStateException("Expected recovery-triggering completion failure."));
        };

        releaseFirst.TrySetResult();

        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var response = await runtime.GetScheduledTaskHandle(taskId)
            .WaitAsync(TestContext.Current.CancellationToken)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(2, attempt);
        Assert.Equal(42, response.GetResult<int>());
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, storage.Get(taskId).Result!.Status);
    }

    [Fact]
    public async Task InitialRecoveryDoesNotStartPendingRequestBeforeActivation()
    {
        var (runtime, storage, _, _) = CreateRuntime(initialize: false);
        runtime.InitializeForActivation();
        var taskId = TaskId.Parse("root/initial-recovery");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = CreateRequest(1, () => DurableTask.Run(_ => started.TrySetResult()));
        var state = storage.GetOrCreate(taskId);
        storage.SetRequest(taskId, state, request);
        storage.SetRequestFingerprint(
            taskId,
            state,
            IDurableTaskRequest.GetFingerprint(request, CreateSerializer()));

        runtime.OnRecoveryCompleted();

        Assert.False(started.Task.IsCompleted);
        Assert.Equal(0, request.CreateTaskCallCount);

        runtime.MarkActivated();
        await runtime.ResumePendingTasksAsync(TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(1, request.CreateTaskCallCount);
    }

    [Fact]
    public async Task RecoveryMarksStaleLocalChildHandleRestartable()
    {
        var (runtime, _, _, _) = CreateRuntime();
        var taskId = TaskId.Parse("root/restartable-local-child");
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstHandle = await runtime.ScheduleChildAsync(
            taskId,
            DurableTask.Run(async cancellationToken =>
            {
                firstStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    firstCanceled.TrySetResult();
                }
            }),
            TestContext.Current.CancellationToken);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        runtime.OnRecoveryCompleted();
        await firstCanceled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var secondHandle = await runtime.ScheduleChildAsync(
            taskId,
            DurableTask.FromResult(42),
            TestContext.Current.CancellationToken);
        var response = await secondHandle.WaitAsync(TestContext.Current.CancellationToken)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Same(firstHandle, secondHandle);
        Assert.Equal(42, response.GetResult<int>());
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
            () => InvokeSelectCompletionCoreAsync(context, TaskId.Parse("other/decision"), [TaskId.Parse("root/child")], TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => InvokeSelectCompletionCoreAsync(context, TaskId.Parse("root/decision"), [TaskId.Parse("other/child")], TestContext.Current.CancellationToken).AsTask());
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
            await runtime.SelectCompletionAsync(decisionId, [recordedWinner], TestContext.Current.CancellationToken));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.SelectCompletionAsync(decisionId, [TaskId.Parse("root/two")], TestContext.Current.CancellationToken).AsTask());

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
            () => runtime.SelectCompletionAsync(decisionId, [candidateId], TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("retained result has expired", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompletionDecisionRejectsExistingChildOperation()
    {
        var (runtime, _, _, _) = CreateRuntime();
        var decisionId = TaskId.Parse("root/decision");
        await runtime.ScheduleDelayAsync(decisionId, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.SelectCompletionAsync(decisionId, [TaskId.Parse("root/candidate")], TestContext.Current.CancellationToken).AsTask());

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
        var executionCancellation = await runtime.SchedulingStarted.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(context.CancellationToken.IsCancellationRequested);
        Assert.False(executionCancellation.IsCancellationRequested);
        await shutdown.CancelAsync();
        var response = await invocation.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

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

        await ((IDurableTaskServer)runtime).ScheduleAsync(taskId, request, TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await runtime.SignalCancellationAsync(taskId, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => storage.Get(taskId).Result is { IsCompleted: true });

        Assert.NotNull(storage.Get(taskId).CancellationRequestedAt);
        Assert.Equal(DurableTaskStatus.Canceled, storage.Get(taskId).Result!.Status);
        await runtime.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StopEstablishesShutdownDespiteCanceledCallerToken()
    {
        var (runtime, _, _, _) = CreateRuntime();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await runtime.StopAsync(cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runtime.ResumePendingTasksAsync(TestContext.Current.CancellationToken));
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
        await runtime.AcceptResponseAsync(taskId, DurableTaskResponse.FromResult(42), target, TestContext.Current.CancellationToken, persist: false);

        var stopping = runtime.StopAsync(TestContext.Current.CancellationToken);
        Assert.False(stopping.IsCompleted);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await stopping.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(42, (await handle.WaitAsync(TestContext.Current.CancellationToken)).GetResult<int>());
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

        await ((IDurableTaskServer)runtime).ScheduleAsync(taskId, request, TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var firstStop = runtime.StopAsync(TestContext.Current.CancellationToken);
        Assert.Same(firstStop, runtime.StopAsync(TestContext.Current.CancellationToken));
        Assert.False(firstStop.IsCompleted);
        release.TrySetResult();
        await firstStop.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(0, active);
        Assert.Null(storage.Get(taskId).Result);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runtime.ResumePendingTasksAsync(TestContext.Current.CancellationToken));

        started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var (replacement, _, _, _) = CreateRuntime(storage, manager);
        await replacement.ResumePendingTasksAsync(TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(1, maxActive);
        var replacementStop = replacement.StopAsync(TestContext.Current.CancellationToken);
        release.TrySetResult();
        await replacementStop.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
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
        await runtime.AcknowledgeCompletionAsync(taskId, target, TestContext.Current.CancellationToken);

        Assert.Equal(7, storage.Get(taskId).Result!.GetResult<int>());
        Assert.Equal(7, (await handle.WaitAsync(TestContext.Current.CancellationToken)).GetResult<int>());
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

        var handle = await runtime.ScheduleChildAsync(taskId, DurableTask.Run(_ => { }), TestContext.Current.CancellationToken);

        Assert.Equal(taskId, handle.TaskId);
    }

    [Fact]
    public async Task NewLocalChildCommitsBeforeUserInvocation()
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var invoked = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var taskId = TaskId.Parse("root/local");
        manager.BeforeWrite = () => Assert.False(invoked.Task.IsCompleted);

        _ = await runtime.ScheduleChildAsync(taskId, DurableTask.Run(_ => invoked.TrySetResult(manager.WriteCount)), TestContext.Current.CancellationToken);

        Assert.True(storage.Contains(taskId));
        Assert.Equal(1, await invoked.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LocalChildReusesHandleCreatedWhileInitialWriteIsPending()
    {
        var (runtime, _, manager, _) = CreateRuntime();
        var taskId = TaskId.Parse("root/local");
        IScheduledTaskHandle? polledHandle = null;
        manager.BeforeWrite = () => polledHandle = runtime.GetScheduledTaskHandle(taskId);

        var scheduledHandle = await runtime.ScheduleChildAsync(taskId, new TestStateManager.PendingDurableTask(), TestContext.Current.CancellationToken);

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
            () => runtime.ScheduleChildAsync(taskId, DurableTask.Run(_ => invoked = true), TestContext.Current.CancellationToken).AsTask());

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

        await runtime.AcknowledgeCompletionAsync(rootId, waiter, TestContext.Current.CancellationToken);

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

        var handle = await runtime.ScheduleChildAsync(taskId, DurableTask.Run(_ => { }), TestContext.Current.CancellationToken);
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, (await handle.WaitAsync(TestContext.Current.CancellationToken)).Status);
        await WaitUntilAsync(() => manager.WriteCount >= 3);

        Assert.True(observedPrunedWrite);
        Assert.False(storage.Contains(taskId));
    }

    [Fact]
    public async Task ScheduleChildAsync_ConcurrentAncestorCancellation_DoesNotPersistOrStartChild()
    {
        var (runtime, storage, manager, transport) = CreateRuntime();
        var rootId = TaskId.Parse("root");
        var childId = TaskId.Parse("root/remote-child");
        storage.GetOrCreate(rootId);
        var rootHandle = runtime.GetScheduledTaskHandle(rootId);
        var receiver = GrainId.Create("test", "one");
        var caller = GrainId.Create("caller", "one");
        var handler = (IInboxHandler)new DurableTaskMessageHandler(runtime);
        var cancellationWriteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCancellationWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.AfterWriteStarted = async () =>
        {
            manager.AfterWriteStarted = null;
            cancellationWriteEntered.TrySetResult();
            await releaseCancellationWrite.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        };

        await handler.HandleAsync(
            CreateHandlerContext(
                receiver,
                CreateEnvelope(
                    caller,
                    receiver,
                    DurableTaskMessageTransport.CancellationRoute,
                    new DurableTaskCancellationMessage { TaskId = rootId })),
            TestContext.Current.CancellationToken);
        var cancellationWrite = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        await cancellationWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var remoteRequest = CreateRemoteRequest(17);
        var childTask = new TestStateManager.TestRemoteDurableTask(remoteRequest);

        var scheduling = runtime.ScheduleChildAsync(
            childId,
            childTask,
            TestContext.Current.CancellationToken).AsTask();

        try
        {
            Assert.False(scheduling.IsCompleted);
            Assert.False(storage.Contains(childId));
            Assert.Equal(0, childTask.ScheduleAsyncCallCount);
            Assert.Empty(transport.Invocations);
            Assert.Equal(0, manager.WriteCount);
        }
        finally
        {
            releaseCancellationWrite.TrySetResult();
        }

        await cancellationWrite;
        var handle = await scheduling;
        var rootResponse = Assert.IsType<CanceledDurableTaskResponse>(
            await rootHandle.WaitAsync(TestContext.Current.CancellationToken)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        var response = Assert.IsType<CanceledDurableTaskResponse>(
            await handle.WaitAsync(TestContext.Current.CancellationToken)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        Assert.Equal(childId, handle.TaskId);
        Assert.Equal(DurableTaskStatus.Canceled, rootResponse.Status);
        Assert.Same(DurableTaskResponse.Canceled, response);
        Assert.Equal(DurableTaskResponseKind.Canceled, response.ResponseKind);
        Assert.Equal(DurableTaskStatus.Canceled, response.Status);
        Assert.Equal("The operation was canceled.", response.Exception.Message);
        Assert.NotNull(storage.Get(rootId).CancellationRequestedAt);
        Assert.False(storage.Contains(childId));
        Assert.Equal(0, childTask.ScheduleAsyncCallCount);
        Assert.Empty(transport.Invocations);
        Assert.Equal(2, manager.WriteCount);
    }

    [Fact]
    public async Task ScheduleChildAsync_CanceledAncestorPreservesCompletedChildResult()
    {
        var (runtime, storage, _, transport) = CreateRuntime();
        var rootId = TaskId.Parse("root");
        var childId = TaskId.Parse("root/completed-child");
        var request = CreateRemoteRequest(17);
        var childTask = new TestStateManager.TestRemoteDurableTask(request);
        var child = storage.GetOrCreate(childId);
        storage.SetRemoteRequest(
            childId,
            child,
            request.Context!.TargetId,
            IDurableTaskRequest.GetFingerprint(childTask, CreateSerializer()));
        child.Result = DurableTaskResponse.FromResult(42);
        child.CompletedAt = DateTimeOffset.UtcNow;
        storage.RequestCancellation(rootId, storage.GetOrCreate(rootId));

        var handle = await runtime.ScheduleChildAsync(
            childId,
            childTask,
            TestContext.Current.CancellationToken);

        Assert.Equal(42, (await handle.WaitAsync(TestContext.Current.CancellationToken)).GetResult<int>());
        Assert.Empty(transport.Invocations);
    }

    [Fact]
    public async Task ScheduleChildAsync_CanceledAncestorPreservesTombstonedChildFailure()
    {
        var (runtime, storage, _, transport) = CreateRuntime();
        var rootId = TaskId.Parse("root");
        var childId = TaskId.Parse("root/tombstoned-child");
        var request = CreateRemoteRequest(17);
        var childTask = new TestStateManager.TestRemoteDurableTask(request);
        var child = storage.GetOrCreate(childId);
        storage.SetRemoteRequest(
            childId,
            child,
            request.Context!.TargetId,
            IDurableTaskRequest.GetFingerprint(childTask, CreateSerializer()));
        storage.CreateTombstone(childId, child);
        storage.RequestCancellation(rootId, storage.GetOrCreate(rootId));

        var handle = await runtime.ScheduleChildAsync(
            childId,
            childTask,
            TestContext.Current.CancellationToken);
        var response = Assert.IsType<ExceptionDurableTaskResponse>(
            await handle.WaitAsync(TestContext.Current.CancellationToken));

        var failure = Assert.IsType<DurableTaskTerminalFailure>(response.Exception);
        Assert.Equal(childId, failure.TaskId);
        Assert.Empty(transport.Invocations);
    }

    [Fact]
    public async Task ScheduleChildAsync_CanceledAncestorStillRejectsConflictingChildIdentity()
    {
        var (runtime, storage, _, transport) = CreateRuntime();
        var rootId = TaskId.Parse("root");
        var childId = TaskId.Parse("root/conflicting-child");
        var originalRequest = CreateRemoteRequest(17);
        var originalTask = new TestStateManager.TestRemoteDurableTask(originalRequest);
        var child = storage.GetOrCreate(childId);
        storage.SetRemoteRequest(
            childId,
            child,
            originalRequest.Context!.TargetId,
            IDurableTaskRequest.GetFingerprint(originalTask, CreateSerializer()));
        storage.RequestCancellation(rootId, storage.GetOrCreate(rootId));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.ScheduleChildAsync(
                childId,
                new TestStateManager.TestRemoteDurableTask(CreateRemoteRequest(18)),
                TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(
            $"Durable child task '{childId}' is already associated with a different request.",
            exception.Message);
        Assert.Empty(transport.Invocations);
    }

    [Fact]
    public async Task ScheduleChildAsync_SpawnedRuntimeWriteWaitsForJournalGate()
    {
        var (runtime, _, manager, transport) = CreateRuntime();
        var childId = TaskId.Parse("root/custom-child");
        var nestedId = TaskId.Parse("nested/remote");
        var nestedRequest = CreateRemoteRequest(17);
        Task<DurableTaskResponse>? nestedScheduling = null;
        var writeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.AfterWriteStarted = async () =>
        {
            manager.AfterWriteStarted = null;
            writeEntered.TrySetResult();
            await releaseWrite.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        };
        var task = new TestSchedulableTask((_, _) =>
        {
            nestedScheduling = Task.Run(
                () => runtime.ScheduleRemoteAsync(
                    nestedId,
                    nestedRequest,
                    TestContext.Current.CancellationToken).AsTask(),
                TestContext.Current.CancellationToken);
            return new(DurableTaskResponse.Pending);
        });

        var scheduling = runtime.ScheduleChildAsync(
            childId,
            task,
            TestContext.Current.CancellationToken).AsTask();
        await writeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        try
        {
            Assert.NotNull(nestedScheduling);
            Assert.False(nestedScheduling.IsCompleted);
            Assert.Empty(transport.Invocations);
        }
        finally
        {
            releaseWrite.TrySetResult();
        }

        await scheduling.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await nestedScheduling.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Single(transport.Invocations);
    }

    [Fact]
    public async Task ScheduleChildAsync_CustomSchedulerCanAwaitNestedRuntimeWriteAndReturnsItsHandle()
    {
        var (runtime, _, _, transport) = CreateRuntime();
        var childId = TaskId.Parse("root/custom-await");
        var nestedId = TaskId.Parse("nested/remote-await");
        var nestedRequest = CreateRemoteRequest(17);
        var expectedHandle = Substitute.For<IScheduledTaskHandle>();
        expectedHandle.TaskId.Returns(childId);
        var task = new TestSchedulableTask(
            async (_, cancellationToken) =>
            {
                await runtime.ScheduleRemoteAsync(nestedId, nestedRequest, cancellationToken);
                return DurableTaskResponse.Pending;
            },
            _ => expectedHandle);

        var handle = await runtime.ScheduleChildAsync(
                childId,
                task,
                TestContext.Current.CancellationToken)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Same(expectedHandle, handle);
        var invocation = Assert.Single(transport.Invocations);
        Assert.Equal(nestedId, invocation.TaskId);
        Assert.Same(nestedRequest, invocation.Request);
    }

    [Fact]
    public async Task ScheduleChildAsync_CancellationDuringCustomSchedulingCancelsSchedulerOwnedHandle()
    {
        var (runtime, storage, _, _) = CreateRuntime();
        var childId = TaskId.Parse("root/custom-cancel");
        var schedulingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseScheduling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var schedulerHandle = Substitute.For<IScheduledTaskHandle>();
        schedulerHandle.TaskId.Returns(childId);
        schedulerHandle.CancelAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cancellationObserved.TrySetResult();
                return ValueTask.CompletedTask;
            });
        var task = new TestSchedulableTask(
            async (_, _) =>
            {
                schedulingStarted.TrySetResult();
                await releaseScheduling.Task;
                return DurableTaskResponse.Pending;
            },
            _ => schedulerHandle);

        var scheduling = runtime.ScheduleChildAsync(
            childId,
            task,
            TestContext.Current.CancellationToken).AsTask();
        await schedulingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var cancellation = runtime.SignalCancellationAsync(
            childId,
            TestContext.Current.CancellationToken).AsTask();

        Assert.False(cancellation.IsCompleted);
        releaseScheduling.TrySetResult();

        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await cancellation.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var result = await scheduling.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var response = await result.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Same(DurableTaskResponse.Canceled, response);
        Assert.NotNull(storage.Get(childId).CancellationRequestedAt);
        await schedulerHandle.Received(1).CancelAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScheduleChildAsync_CustomFinalizationFailureCancelsHandleAndAllowsRetry()
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var childId = TaskId.Parse("root/custom-finalization-failure");
        var schedulerHandle = Substitute.For<IScheduledTaskHandle>();
        schedulerHandle.TaskId.Returns(childId);
        var scheduleCount = 0;
        var task = new TestSchedulableTask(
            (_, _) =>
            {
                scheduleCount++;
                return new(DurableTaskResponse.Pending);
            },
            _ => schedulerHandle);
        manager.BeforeWrite = () =>
        {
            manager.BeforeWrite = null;
            throw new IOException("Expected custom finalization write failure.");
        };

        var exception = await Assert.ThrowsAsync<IOException>(
            () => runtime.ScheduleChildAsync(
                childId,
                task,
                TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("Expected custom finalization write failure.", exception.Message);
        Assert.False(storage.Contains(childId));
        await schedulerHandle.Received(1).CancelAsync(Arg.Any<CancellationToken>());

        var retryHandle = await runtime.ScheduleChildAsync(
            childId,
            task,
            TestContext.Current.CancellationToken);

        Assert.Same(schedulerHandle, retryHandle);
        Assert.Equal(2, scheduleCount);
    }

    [Fact]
    public async Task RemoteTaskIdReuseAfterPruning_IsRejectedWithoutSendingInvocation()
    {
        var (runtime, storage, manager, transport) = CreateRuntime(TimeSpan.Zero);
        var taskId = TaskId.Parse("root/remote-reuse");
        var originalRequest = CreateRemoteRequest(17);
        var target = originalRequest.Context!.TargetId;
        var originalFingerprint = IDurableTaskRequest.GetFingerprint(originalRequest, CreateSerializer());
        var pruningWriteCompleted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.AfterWriteCompleted = writeCount =>
        {
            if (writeCount == 3)
            {
                pruningWriteCompleted.TrySetResult(writeCount);
            }
        };

        await runtime.ScheduleRemoteAsync(taskId, originalRequest, TestContext.Current.CancellationToken);
        await runtime.AcceptResponseAsync(
            taskId,
            DurableTaskResponse.FromResult(42),
            target,
            TestContext.Current.CancellationToken,
            persist: false);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            3,
            await pruningWriteCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        var tombstone = storage.Get(taskId);
        var tombstonedAt = Assert.IsType<DateTimeOffset>(tombstone.TombstonedAt);
        Assert.Null(tombstone.RequestFingerprint);
        Assert.Equal(originalFingerprint, tombstone.RemoteRequestFingerprint);
        Assert.Equal(target, tombstone.RemoteTarget);
        Assert.Null(tombstone.Request);
        Assert.Null(tombstone.Result);
        Assert.Empty(tombstone.CompletionDestinations);
        var originalInvocation = Assert.Single(transport.Invocations);
        Assert.Equal(taskId, originalInvocation.TaskId);
        Assert.Equal(target, originalInvocation.Target);
        Assert.Same(originalRequest, originalInvocation.Request);
        Assert.Equal(3, manager.WriteCount);

        var incompatibleRequest = CreateRemoteRequest(18);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.ScheduleRemoteAsync(
                taskId,
                incompatibleRequest,
                TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(
            $"Durable task '{taskId}' is already associated with a different request.",
            exception.Message);
        Assert.Same(tombstone, storage.Get(taskId));
        Assert.Null(tombstone.RequestFingerprint);
        Assert.Equal(originalFingerprint, tombstone.RemoteRequestFingerprint);
        Assert.Equal(target, tombstone.RemoteTarget);
        Assert.Equal(tombstonedAt, tombstone.TombstonedAt);
        Assert.Null(tombstone.Request);
        Assert.Null(tombstone.Result);
        Assert.Empty(tombstone.CompletionDestinations);
        var currentInvocation = Assert.Single(transport.Invocations);
        Assert.Equal(originalInvocation.Sender, currentInvocation.Sender);
        Assert.Equal(originalInvocation.Target, currentInvocation.Target);
        Assert.Equal(originalInvocation.TaskId, currentInvocation.TaskId);
        Assert.Same(originalInvocation.Request, currentInvocation.Request);
        Assert.Equal(3, manager.WriteCount);
    }

    [Fact]
    public async Task SubscribeOrPollAsync_TombstonedTask_ReturnsExpiredTerminalFailure()
    {
        var (runtime, storage, manager, transport) = CreateRuntime();
        var taskId = TaskId.Parse("root/expired-poll");
        var tombstone = storage.GetOrCreate(taskId);
        tombstone.RemoteRequestFingerprint = "stable-remote-fingerprint";
        storage.CreateTombstone(taskId, tombstone);

        var response = await ((IDurableTaskServer)runtime).SubscribeOrPollAsync(
            taskId,
            new SubscribeOrPollOptions { PollTimeout = TimeSpan.Zero },
            TestContext.Current.CancellationToken);
        var serializer = EnvelopeServiceProvider.GetRequiredService<Serializer>();
        var wireResponse = serializer.Deserialize<DurableTaskResponse>(
            serializer.SerializeToArray<DurableTaskResponse>(response));

        var failedResponse = Assert.IsType<ExceptionDurableTaskResponse>(wireResponse);
        var failure = Assert.IsType<DurableTaskTerminalFailure>(failedResponse.Exception);
        Assert.True(failedResponse.IsCompleted);
        Assert.Equal(DurableTaskResponseKind.Failed, failedResponse.ResponseKind);
        Assert.Equal(DurableTaskStatus.Failed, failedResponse.Status);
        Assert.Equal(DurableTaskTerminalFailureCode.ExpiredOrTombstoned, failure.Code);
        Assert.Equal(taskId, failure.TaskId);
        Assert.Equal(
            $"Durable task '{taskId}' has expired and its result is no longer available.",
            failure.Message);
        Assert.Same(tombstone, storage.Get(taskId));
        Assert.NotNull(tombstone.TombstonedAt);
        Assert.Null(tombstone.Result);
        Assert.Equal(0, manager.WriteCount);
        Assert.Empty(transport.Invocations);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    public async Task TaskHandlePollAsync_PropagatesCallerCancellation(int pollTimeoutMilliseconds)
    {
        var (runtime, storage, _, _) = CreateRuntime();
        var taskId = TaskId.Parse("root/canceled-poll");
        storage.GetOrCreate(taskId);
        var handle = runtime.GetScheduledTaskHandle(taskId);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => handle.PollAsync(
                new PollingOptions
                {
                    PollTimeout = TimeSpan.FromMilliseconds(pollTimeoutMilliseconds)
                },
                cancellation.Token).AsTask());

        Assert.Equal(cancellation.Token, exception.CancellationToken);
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
            runtime.MarkActivated();
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
            TimeSpan duration,
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
            TimeSpan duration,
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
            ((DurableTaskState)state).DelayDuration = null;
            if (((DurableTaskState)state).ResumeGeneration > 0)
            {
                ((DurableTaskState)state).ResumeGeneration++;
            }
        }

        public void RequestCancellation(TaskId taskId, IDurableTaskState state) =>
            ((DurableTaskState)state).CancellationRequestedAt ??= DateTimeOffset.UtcNow;

        public void SetDelay(TaskId taskId, IDurableTaskState state, DateTimeOffset dueTime, TimeSpan duration, long generation)
        {
            ((DurableTaskState)state).DueTime = dueTime;
            ((DurableTaskState)state).DelayDuration = duration;
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
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        public int WriteCount { get; private set; }
        public long PendingWriteByteCount { get; set; }
        public Action? BeforeWrite { get; set; }
        public Func<ValueTask>? AfterWriteStarted { get; set; }
        public Action<int>? AfterWriteCompleted { get; set; }
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
            await _writeGate.WaitAsync(cancellationToken);
            try
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

                AfterWriteCompleted?.Invoke(WriteCount);
            }
            finally
            {
                _writeGate.Release();
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
