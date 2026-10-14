using System.Diagnostics.CodeAnalysis;
using System.Collections.Concurrent;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using System.Reflection;
using NSubstitute;
using Orleans.CodeGeneration;
using Orleans.Configuration;
using Orleans.Invocation;
using Orleans.Journaling;
using Orleans.Messaging;
using Orleans.Messaging.Configuration;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Invocation;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed partial class DurableTaskRuntimeInvariantTests
{
    [Fact]
    public void RuntimeJournalHookWritesNoEntries()
    {
        var (runtime, _, manager, _) = CreateRuntime();

        Assert.True(manager.TryGetStateMachine(DurableTaskGrainRuntime.StateMachineName, out var stateMachine));
        Assert.Same(runtime, stateMachine);
        var exception = Assert.Throws<InvalidOperationException>(
            () => stateMachine.ReplayEntry(default, default));
        Assert.Contains(DurableTaskGrainRuntime.StateMachineName, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public Task OrdinaryGrainActivationFailsBeforeUsingUninitializedJournalAdapter() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, _, _, _) = CreateRuntime(initialize: false);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.SignalCancellationAsync(TaskId.Parse("root"), TestContext.Current.CancellationToken).AsTask());

        Assert.Contains(nameof(DurableGrain), exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(IMessagingGrain), exception.Message, StringComparison.Ordinal);
    });

    [Fact]
    public Task OrdinaryGrainActivationRejectsPollingAndDiagnostics() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, _, _, _) = CreateRuntime(initialize: false);
        var extension = (IDurableTaskGrainExtension)runtime;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.SubscribeOrPollAsync(TaskId.Parse("root"), default, TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await extension.GetTasksAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken).MoveNextAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await extension.GetRunningTasksAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken).MoveNextAsync());
    });

    [Fact]
    public Task InboxSchedulingStartsOnlyAfterCommit() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, _, manager, _) = CreateRuntime();
        var invoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = CreateRequest(1, () => DurableTask.Run(_ => invoked.TrySetResult()));
        var taskId = TaskId.Parse("root");

        var response = runtime.ScheduleFromInbox(taskId, request);

        Assert.Equal(DurableTaskResponseKind.Subscribed, response.ResponseKind);
        Assert.False(invoked.Task.IsCompleted);
        Assert.Equal(0, manager.WriteCount);

        await manager.WriteStateAsync(TestContext.Current.CancellationToken);

        await invoked.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(1, request.CreateTaskCallCount);
    });

    [Fact]
    public Task DeferredStartRechecksCommittedCancellationBeforeInvocation() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var request = CreateRequest(1);
        var taskId = TaskId.Parse("root");
        runtime.ScheduleFromInbox(taskId, request);
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
    });

    [Fact]
    public Task EquivalentDuplicateDeliveryExecutesOnce() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, _, manager, _) = CreateRuntime();
        var first = CreateRequest(1);
        var retry = CreateRequest(1);
        var taskId = TaskId.Parse("root");

        runtime.ScheduleFromInbox(taskId, first);
        runtime.ScheduleFromInbox(taskId, retry);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => first.CreateTaskCallCount + retry.CreateTaskCallCount == 1);

        Assert.Equal(1, first.CreateTaskCallCount + retry.CreateTaskCallCount);
    });

    [Fact]
    public void ConflictingRequestForSameTaskIdFailsBeforeExecution()
    {
        var (runtime, _, _, _) = CreateRuntime();
        var taskId = TaskId.Parse("root");
        runtime.ScheduleFromInbox(taskId, CreateRequest(1));

        var exception = Assert.Throws<DurableTaskConflictException>(
            () => runtime.ScheduleFromInbox(taskId, CreateRequest(2)));

        Assert.Contains("different request", exception.Message, StringComparison.Ordinal);
        Assert.Equal(taskId, exception.TaskId);
    }

    [Fact]
    public void InvocationWithoutRequestContextFailsBeforeChangingDurableState()
    {
        var (runtime, storage, manager, transport) = CreateRuntime();
        var sender = GrainId.Create("caller", "one");
        var receiver = GrainId.Create("test", "one");
        var message = InvocationMessage(sender, receiver, TaskId.Parse("no-context"), new TestDurableTaskRequest());
        var handler = CreateHandler(runtime);

        // The inbox dead-letters a message which its sink rejects; the sink stages nothing first.
        var exception = Assert.Throws<InvalidOperationException>(
            () => handler.TryAccept(message, out _));

        Assert.Equal("The durable task invocation request has no context.", exception.Message);
        Assert.Empty(storage.Tasks);
        Assert.Equal(0, manager.WriteCount);
        Assert.Empty(transport.Invocations);
        Assert.Empty(transport.Completions);
        Assert.Empty(transport.Cancellations);
        Assert.Empty(transport.Resumes);
    }

    [Fact]
    public void InboxInvocationRequiresMatchingTargetAndSender()
    {
        var (runtime, storage, _, transport) = CreateRuntime();
        var sender = GrainId.Create("caller", "one");
        var receiver = GrainId.Create("test", "one");
        var request = CreateSerializableRequest(GrainId.Create("target", "other"));
        var handler = CreateHandler(runtime);

        var wrongTarget = InvocationMessage(sender, receiver, TaskId.Parse("wrong-target"), request);
        var targetException = Assert.Throws<InvalidOperationException>(
            () => handler.TryAccept(wrongTarget, out _));
        Assert.Contains("not receiver", targetException.Message, StringComparison.Ordinal);

        request.Context!.TargetId = receiver;
        var missingSender = InvocationMessage(default, receiver, TaskId.Parse("missing-sender"), request, messageIdSender: sender);
        var senderException = Assert.Throws<InvalidOperationException>(
            () => handler.TryAccept(missingSender, out _));
        Assert.Contains("no sender", senderException.Message, StringComparison.Ordinal);

        var selfInvocation = InvocationMessage(receiver, receiver, TaskId.Parse("self"), request);
        var selfException = Assert.Throws<InvalidOperationException>(() => handler.TryAccept(selfInvocation, out _));
        Assert.Contains("owning grain", selfException.Message, StringComparison.Ordinal);

        Assert.Empty(storage.Tasks);
        Assert.Empty(transport.Completions);
    }

    [Fact]
    public void InboxSinkAcceptsDurableRpcCallsAndHandlerHandlesOnlyResumes()
    {
        var (runtime, storage, _, _) = CreateRuntime();
        var handler = CreateHandler(runtime);
        var cancellation = CancellationMessage(GrainId.Create("caller", "one"), GrainId.Create("test", "one"), TaskId.Parse("root"));
        var resume = MessagingTestHelpers.CreateResumeMessage(runtime.GrainId, TaskId.Parse("delay"), 1, DateTimeOffset.UtcNow);
        var other = new InboxMessage
        {
            Envelope = new Envelope
            {
                MessageId = MessageId.Create("other", "one"),
                Payload = [],
                Subject = "orders/reserve",
            },
        };
        var raw = new InboxMessage
        {
            Envelope = new Envelope
            {
                MessageId = MessageId.Create("raw", "one"),
                Payload = [],
            },
        };

        Assert.False(handler.TryAccept(resume, out _));
        Assert.False(handler.TryAccept(other, out _));
        Assert.False(handler.TryAccept(raw, out _));
        Assert.Empty(storage.Tasks);
        Assert.True(handler.TryAccept(cancellation, out _));
        Assert.True(handler.CanHandle(MessagingTestHelpers.CreateHandlerContext(resume)));
        Assert.False(handler.CanHandle(MessagingTestHelpers.CreateHandlerContext(cancellation)));
        Assert.False(handler.CanHandle(MessagingTestHelpers.CreateHandlerContext(other)));
        Assert.False(handler.CanHandle(MessagingTestHelpers.CreateHandlerContext(raw)));
    }

    [Fact]
    public Task InboxCancellationBindsTaskToSender() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var receiver = GrainId.Create("test", "one");
        var caller = GrainId.Create("caller", "one");
        var otherCaller = GrainId.Create("caller", "two");
        var taskId = TaskId.Parse("bound-cancellation");
        var handler = CreateHandler(runtime);

        Accept(handler, CancellationMessage(caller, receiver, taskId));

        Assert.Equal(caller, storage.Get(taskId).CallerId);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        var otherCancellation = CancellationMessage(otherCaller, receiver, taskId);
        var cancellationException = Assert.Throws<InvalidOperationException>(
            () => handler.TryAccept(otherCancellation, out _));
        Assert.Contains("already associated with caller", cancellationException.Message, StringComparison.Ordinal);

        var request = CreateSerializableRequest(receiver);
        var otherInvocation = InvocationMessage(otherCaller, receiver, taskId, request);
        var invocationException = Assert.Throws<InvalidOperationException>(
            () => handler.TryAccept(otherInvocation, out _));
        Assert.Contains("already associated with caller", invocationException.Message, StringComparison.Ordinal);
    });

    [Fact]
    public Task InboxCancellationDefersTaskAndMessageCommitToOuterWrite() => ActivationScheduler.RunAsync(async () =>
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
        var handler = CreateHandler(runtime);

        Accept(handler, CancellationMessage(caller, receiver, rootId));

        Assert.Equal(0, manager.WriteCount);
        Assert.NotNull(storage.Get(rootId).CancellationRequestedAt);
        Assert.NotNull(storage.Get(childId).CancellationRequestedAt);
        var cancellation = Assert.Single(transport.Cancellations);
        Assert.Equal(childId, cancellation.TaskId);
        Assert.Equal(target, cancellation.Target);
    });

    [Fact]
    public Task RunningInboxCancellationDefersTerminalResponseUntilOuterWrite() => ActivationScheduler.RunAsync(async () =>
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
        runtime.ScheduleFromInbox(taskId, request);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var writesBeforeCancellation = manager.WriteCount;
        var handler = CreateHandler(runtime);

        Accept(handler, CancellationMessage(caller, receiver, taskId));

        Assert.Equal(writesBeforeCancellation, manager.WriteCount);
        Assert.Null(storage.Get(taskId).Result);

        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => storage.Get(taskId).Result is { IsCompleted: true });
        Assert.Equal(DurableTaskStatus.Canceled, storage.Get(taskId).Result!.Status);
    });

    [Fact]
    public Task InboxCancellationPropagatesToRunningDescendantsAfterOuterWrite() => ActivationScheduler.RunAsync(async () =>
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
        var handler = CreateHandler(runtime);

        Accept(handler, CancellationMessage(caller, receiver, rootId));
        Assert.False((await childHandle.PollAsync(new PollingOptions { PollTimeout = TimeSpan.Zero }, TestContext.Current.CancellationToken)).IsCompleted);

        await manager.WriteStateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            DurableTaskStatus.Canceled,
            (await childHandle.WaitAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).Status);
    });

    [Fact]
    public Task InboxBatchStagesCancellationsAndCompletionsWithoutWaitingForBatchWrite() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, transport) = CreateRuntime();
        var receiver = GrainId.Create("test", "one");
        var caller = GrainId.Create("caller", "one");
        var target = GrainId.Create("target", "one");
        var handler = CreateHandler(runtime);
        TaskId[] rootIds = [TaskId.Parse("root-a"), TaskId.Parse("root-b"), TaskId.Parse("root-c")];
        TaskId[] remoteIds = [TaskId.Parse("remote-a"), TaskId.Parse("remote-b")];
        var started = rootIds.ToDictionary(
            static id => id,
            static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        foreach (var rootId in rootIds)
        {
            var rootStarted = started[rootId];
            runtime.ScheduleFromInbox(
                rootId,
                CreateRequest(
                    Array.IndexOf(rootIds, rootId),
                    () => DurableTask.Run(async cancellationToken =>
                    {
                        rootStarted.TrySetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    })));
        }

        var remoteHandles = new List<IScheduledTaskHandle>();
        foreach (var remoteId in remoteIds)
        {
            storage.SetRemoteRequest(remoteId, storage.GetOrCreate(remoteId), target, $"fingerprint-{remoteId}");
            remoteHandles.Add(runtime.GetScheduledTaskHandle(remoteId));
        }

        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await Task.WhenAll(started.Values.Select(static source => source.Task))
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var writesBeforeBatch = manager.WriteCount;

        // The inbox accepts every message before the write which commits them, so no message may wait for it.
        InboxMessage[] batch =
        [
            CancellationMessage(caller, receiver, rootIds[0]),
            CompletionMessage(target, receiver, remoteIds[0], DurableTaskResponse.FromResult(10)),
            CancellationMessage(caller, receiver, rootIds[1]),
            CompletionMessage(target, receiver, remoteIds[1], DurableTaskResponse.FromResult(20)),
            CancellationMessage(caller, receiver, rootIds[2]),
            CancellationMessage(caller, receiver, rootIds[0]),
        ];
        foreach (var message in batch)
        {
            Accept(handler, message);
        }

        Assert.Equal(writesBeforeBatch, manager.WriteCount);
        Assert.All(rootIds, rootId => Assert.Null(storage.Get(rootId).Result));
        foreach (var handle in remoteHandles)
        {
            Assert.False((await handle.PollAsync(new PollingOptions { PollTimeout = TimeSpan.Zero }, TestContext.Current.CancellationToken)).IsCompleted);
        }

        await manager.WriteStateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(10, (await remoteHandles[0].WaitAsync(TestContext.Current.CancellationToken)).GetResult<int>());
        Assert.Equal(20, (await remoteHandles[1].WaitAsync(TestContext.Current.CancellationToken)).GetResult<int>());
        await WaitUntilAsync(() => rootIds.All(rootId => storage.Get(rootId).Result is { IsCompleted: true }));
        Assert.All(rootIds, rootId => Assert.Equal(DurableTaskStatus.Canceled, storage.Get(rootId).Result!.Status));
    });

    [Fact]
    public Task CompletionRequiresRecordedRemoteTarget() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, _, transport) = CreateRuntime();
        var receiver = GrainId.Create("test", "one");
        var request = CreateRemoteRequest(1);
        var expectedTarget = request.Context!.TargetId;
        var taskId = TaskId.Parse("root/remote");
        await runtime.ScheduleChildAsync(taskId, new TestStateManager.TestRemoteDurableTask(request), TestContext.Current.CancellationToken);
        var handler = CreateHandler(runtime);
        var response = DurableTaskResponse.FromResult(42);

        var wrongSender = CompletionMessage(GrainId.Create("target", "other"), receiver, taskId, response);
        var exception = Assert.Throws<InvalidOperationException>(
            () => handler.TryAccept(wrongSender, out _));
        Assert.Contains("does not accept completions", exception.Message, StringComparison.Ordinal);
        Assert.Null(storage.Get(taskId).Result);

        Accept(handler, CompletionMessage(expectedTarget, receiver, taskId, response));

        // The accepted completion is staged once, in task state, and nothing is sent back to the child.
        Assert.Equal(42, storage.Get(taskId).Result!.GetResult<int>());
        Assert.Empty(transport.Completions);
        Assert.Empty(transport.Cancellations);
        Assert.Single(transport.Invocations);
    });

    [Fact]
    public void CompletedRequestReplayDoesNotResendPendingCompletion()
    {
        var (runtime, storage, _, transport) = CreateRuntime();
        var taskId = TaskId.Parse("root");
        var request = CreateRequest(1);
        var state = storage.GetOrCreate(taskId);
        storage.SetRequest(taskId, state, request);
        storage.SetRequestFingerprint(taskId, state, IDurableTaskRequest.GetFingerprint(request, CreateSerializer()));
        storage.AddCompletionDestination(taskId, state, request.Context!.CallerId);
        storage.SetResponse(taskId, state, DurableTaskResponse.FromResult(42));

        // A listed destination has a completion pending in the outbox, which retires it.
        var response = runtime.ScheduleFromInbox(taskId, request);

        Assert.Equal(42, response.GetResult<int>());
        Assert.Empty(transport.Completions);
    }

    [Fact]
    public void CompletedRequestReplayResendsCompletionAfterItWasDeadLettered()
    {
        var (runtime, storage, _, transport) = CreateRuntime();
        var taskId = TaskId.Parse("root");
        var request = CreateRequest(1);
        var caller = request.Context!.CallerId;
        var state = storage.GetOrCreate(taskId);
        storage.SetRequest(taskId, state, request);
        storage.SetRequestFingerprint(taskId, state, IDurableTaskRequest.GetFingerprint(request, CreateSerializer()));
        storage.SetResponse(taskId, state, DurableTaskResponse.FromResult(42));

        var response = runtime.ScheduleFromInbox(taskId, request);

        Assert.Equal(42, response.GetResult<int>());
        var completion = Assert.Single(transport.Completions);
        Assert.Equal(caller, completion.Target);
        Assert.Equal(taskId, completion.TaskId);
        Assert.Contains(caller, storage.Get(taskId).CompletionDestinations);
        Assert.Equal(42, completion.Materialize(runtime).Response.GetResult<int>());
    }

    [Fact]
    public Task RootRemoteScheduleCreatesCallerStateBeforeAdvertisingCompletion() => ActivationScheduler.RunAsync(async () =>
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
    });

    [Fact]
    public Task RootRemoteScheduleRejectsSelfTargetBeforeCreatingState() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, transport) = CreateRuntime();
        var taskId = TaskId.Parse("root/self");
        var request = CreateRequest(1);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.ScheduleRemoteAsync(taskId, request, TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("owning grain", exception.Message, StringComparison.Ordinal);
        Assert.False(storage.Contains(taskId));
        Assert.Empty(transport.Invocations);
        Assert.Equal(0, manager.WriteCount);
    });

    [Fact]
    public Task RootRemoteScheduleReturnsPersistedTerminalResultWithoutResending() => ActivationScheduler.RunAsync(async () =>
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
    });

    [Fact]
    public Task CompletionHandleAdvancesOnlyAfterCommit() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var taskId = TaskId.Parse("root/remote");
        var target = GrainId.Create("target", "one");
        var state = storage.GetOrCreate(taskId);
        storage.SetRemoteRequest(taskId, state, target, "fingerprint");
        var handle = runtime.GetScheduledTaskHandle(taskId);

        runtime.AcceptResponseFromInbox(taskId, DurableTaskResponse.FromResult(42), target);

        Assert.False((await handle.PollAsync(new PollingOptions { PollTimeout = TimeSpan.Zero }, TestContext.Current.CancellationToken)).IsCompleted);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(42, (await handle.WaitAsync(TestContext.Current.CancellationToken)).GetResult<int>());
    });

    [Fact]
    public Task InboxCompletionPersistsEligiblePruningAfterOuterWrite() => ActivationScheduler.RunAsync(async () =>
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

        runtime.AcceptResponseFromInbox(taskId, DurableTaskResponse.FromResult(42), target);
        Assert.True(storage.Contains(taskId));

        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.True(
            await pruningWriteCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken) >= 2);

        Assert.False(storage.Contains(taskId));
        Assert.Empty(transport.Invocations);
        Assert.Equal(2, manager.WriteCount);
    });

    [Fact]
    public Task FailedScheduleWriteFencesWithoutRevertingStagedIdentity() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var taskId = TaskId.Parse("root/existing-pending");
        var request = CreateRequest(1);
        var state = storage.GetOrCreate(taskId);
        storage.SetRequest(taskId, state, request);
        _ = runtime.GetScheduledTaskHandle(taskId);
        manager.BeforeWrite = () =>
        {
            manager.BeforeWrite = null;
            throw new IOException("Expected existing pending write failure.");
        };

        await Assert.ThrowsAsync<IOException>(
            () => ((IDurableTaskServer)runtime).ScheduleAsync(
                taskId,
                request,
                TestContext.Current.CancellationToken).AsTask());

        // The staged identity is safe to commit, so it is retained; the failed journal is fenced instead of reverted.
        Assert.Equal(IDurableTaskRequest.GetFingerprint(request, CreateSerializer()), storage.Get(taskId).RequestFingerprint);
        Assert.True(manager.IsFenced);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ((IDurableTaskServer)runtime).ScheduleAsync(
                taskId,
                request,
                TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(0, request.CreateTaskCallCount);
        Assert.Equal(0, manager.WriteCount);
    });

    [Fact]
    public Task ResponseStagedAfterWriteSnapshotIsCommittedBeforePruning() => ActivationScheduler.RunAsync(async () =>
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
            if (writeCount == 2 && !storage.Contains(secondId))
            {
                pruningWriteCompleted.TrySetResult(writeCount);
            }
        };
        runtime.AcceptResponseFromInbox(firstId, DurableTaskResponse.FromResult(1), target);
        manager.AfterWriteStarted = () =>
        {
            runtime.AcceptResponseFromInbox(secondId, DurableTaskResponse.FromResult(2), target);
            return default;
        };

        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        manager.AfterWriteStarted = null;

        Assert.Equal(
            2,
            (await secondHandle.WaitAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).GetResult<int>());
        Assert.Equal(
            2,
            await pruningWriteCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.False(storage.Contains(secondId));
        Assert.False(storage.Contains(firstId));
    });

    [Fact]
    public Task JournalResetDiscardsStagedCompletionEffects() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var taskId = TaskId.Parse("root/remote");
        var target = GrainId.Create("target", "one");
        var state = storage.GetOrCreate(taskId);
        storage.SetRemoteRequest(taskId, state, target, "fingerprint");

        runtime.AcceptResponseFromInbox(taskId, DurableTaskResponse.FromResult(42), target);
        var handle = runtime.GetScheduledTaskHandle(taskId);
        state.Result = null;
        state.CompletedAt = null;
        ((IStateMachine)runtime).Reset(default);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);

        Assert.False((await handle.PollAsync(new PollingOptions { PollTimeout = TimeSpan.Zero }, TestContext.Current.CancellationToken)).IsCompleted);
    });

    [Fact]
    public Task LongPollDoesNotBlockCancellation() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, _, _) = CreateRuntime();
        var taskId = TaskId.Parse("root");
        storage.GetOrCreate(taskId);
        var polling = runtime.SubscribeOrPollAsync(taskId, new SubscribeOrPollOptions { PollTimeout = TimeSpan.FromMinutes(1) }, TestContext.Current.CancellationToken).AsTask();

        await runtime.SignalCancellationAsync(taskId, TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(DurableTaskStatus.Canceled, (await polling.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).Status);
    });

    [Fact]
    public Task ConcurrentHandleLookupReturnsOneHandle() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, _, _) = CreateRuntime();
        var taskId = TaskId.Parse("root");
        storage.GetOrCreate(taskId);

        var handles = await Task.WhenAll(
            Enumerable.Range(0, 100)
                .Select(_ => Task.Run(() => runtime.GetScheduledTaskHandle(taskId))));

        Assert.All(handles, handle => Assert.Same(handles[0], handle));
    });

    [Fact]
    public Task PreCanceledInboxInvocationStagesOneCompletion() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, _, manager, transport) = CreateRuntime();
        var receiver = GrainId.Create("test", "one");
        var caller = GrainId.Create("caller", "one");
        var taskId = TaskId.Parse("pre-canceled");
        var handler = CreateHandler(runtime);

        Accept(handler, CancellationMessage(caller, receiver, taskId));
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        var request = CreateSerializableRequest(receiver);
        Accept(handler, InvocationMessage(caller, receiver, taskId, request));

        var completion = Assert.Single(transport.Completions);
        Assert.Equal(caller, completion.Target);
        Assert.Equal(DurableTaskStatus.Canceled, completion.Materialize(runtime).Response.Status);
    });

    [Fact]
    public Task CancellationBeforeInvocationPreventsExecution() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, _, _) = CreateRuntime();
        var request = CreateRequest(1);
        var taskId = TaskId.Parse("root");

        await runtime.SignalCancellationAsync(taskId, TestContext.Current.CancellationToken);
        var response = await ((IDurableTaskServer)runtime).ScheduleAsync(taskId, request, TestContext.Current.CancellationToken);

        Assert.Equal(DurableTaskStatus.Canceled, response.Status);
        Assert.Equal(0, request.CreateTaskCallCount);
        Assert.True(storage.Get(taskId).CancellationRequestedAt.HasValue);
    });

    [Fact]
    public Task DirectScheduleRejectsMismatchedTargetBeforeStateMutation() => ActivationScheduler.RunAsync(async () =>
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
    });

    [Fact]
    public Task DirectScheduleClearsForgedCallerIdentity() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, _, _) = CreateRuntime();
        var request = CreateRequest(1);
        request.Context!.CallerId = GrainId.Create("forged", "caller");
        request.Context.SupportsDurableCompletion = true;
        var taskId = TaskId.Parse("root");

        _ = await ((IDurableTaskServer)runtime).ScheduleAsync(taskId, request, TestContext.Current.CancellationToken);

        Assert.Equal(default, storage.Get(taskId).CallerId);
        Assert.Empty(storage.Get(taskId).CompletionDestinations);
    });

    [Fact]
    public Task DelayStagesScheduledSelfResumeInItsWrite() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, transport) = CreateRuntime();
        var taskId = TaskId.Parse("root/scheduled-delay");
        manager.BeforeWrite = () =>
        {
            // The delay state and its resume message are staged before the write which commits both.
            var resume = Assert.Single(transport.Resumes);
            Assert.Equal(storage.Get(taskId).ResumeGeneration, resume.Generation);
            Assert.Equal(storage.Get(taskId).DueTime, resume.DueTime);
        };

        Assert.Same(
            DurableTaskResponse.Pending,
            await runtime.ScheduleDelayAsync(taskId, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken));

        var state = storage.Get(taskId);
        var sent = Assert.Single(transport.Resumes);
        Assert.Equal(runtime.GrainId, sent.GrainId);
        Assert.Equal(taskId, sent.TaskId);
        Assert.Equal(1, sent.Generation);
        Assert.Equal(1, state.ResumeGeneration);
        Assert.Equal(state.DueTime, sent.DueTime);
        Assert.Equal(1, manager.WriteCount);
        Assert.Null(state.Result);
    });

    [Fact]
    public Task DelayCompletesWhenItsResumeMessageIsHandled() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, transport) = CreateRuntime();
        var taskId = TaskId.Parse("root/delay");
        await runtime.ScheduleDelayAsync(taskId, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
        var handle = runtime.GetScheduledTaskHandle(taskId);
        var resume = Assert.Single(transport.Resumes);

        var context = ResumeContext(runtime, taskId, resume.Generation, resume.DueTime);
        await HandleAsync(CreateHandler(runtime), context);

        // The completion is staged with the inbox outcome and the handle completes only after their write.
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, storage.Get(taskId).Result!.Status);
        Assert.Null(storage.Get(taskId).DueTime);
        Assert.False((await handle.PollAsync(new PollingOptions { PollTimeout = TimeSpan.Zero }, TestContext.Current.CancellationToken)).IsCompleted);
        Assert.Equal(1, manager.WriteCount);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, (await handle.WaitAsync(TestContext.Current.CancellationToken)).Status);
        Assert.Single(transport.Resumes);
    });

    [Fact]
    public Task StaleResumeGenerationCannotCompleteDelay() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var taskId = TaskId.Parse("root/delay");
        await runtime.ScheduleDelayAsync(taskId, TimeSpan.Zero, TestContext.Current.CancellationToken);
        var generation = storage.Get(taskId).ResumeGeneration;
        var dueTime = storage.Get(taskId).DueTime!.Value;
        var handler = CreateHandler(runtime);

        await HandleAsync(handler, ResumeContext(runtime, taskId, generation + 1, dueTime));
        await HandleAsync(handler, ResumeContext(runtime, taskId, generation - 1, dueTime));
        Assert.Null(storage.Get(taskId).Result);
        Assert.Equal(1, manager.WriteCount);

        await HandleAsync(handler, ResumeContext(runtime, taskId, generation, dueTime));
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, storage.Get(taskId).Result!.Status);

        // A duplicate resume of the completed delay is harmless.
        await HandleAsync(handler, ResumeContext(runtime, taskId, generation, dueTime));
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, storage.Get(taskId).Result!.Status);
    });

    [Fact]
    public Task DelayCanceledBeforeDueTimeTerminalizesAsCanceledAndLaterResumeIsNoOp() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, transport) = CreateRuntime();
        var taskId = TaskId.Parse("root/delay");
        await runtime.ScheduleDelayAsync(taskId, TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
        var handle = runtime.GetScheduledTaskHandle(taskId);
        var resume = Assert.Single(transport.Resumes);

        await handle.CancelAsync(TestContext.Current.CancellationToken);

        var canceled = storage.Get(taskId);
        Assert.Equal(DurableTaskStatus.Canceled, canceled.Result!.Status);
        Assert.Null(canceled.DueTime);
        Assert.True(canceled.ResumeGeneration > resume.Generation);
        Assert.Equal(DurableTaskStatus.Canceled, (await handle.WaitAsync(TestContext.Current.CancellationToken)).Status);
        var writes = manager.WriteCount;

        await HandleAsync(CreateHandler(runtime), ResumeContext(runtime, taskId, resume.Generation, resume.DueTime));

        Assert.Equal(DurableTaskStatus.Canceled, storage.Get(taskId).Result!.Status);
        Assert.Equal(canceled.CompletedAt, storage.Get(taskId).CompletedAt);
        Assert.Equal(writes, manager.WriteCount);
        Assert.Empty(transport.Completions);
        Assert.Single(transport.Resumes);
    });

    [Fact]
    public Task ResumeOfDelayWithRequestedCancellationTerminalizesAsCanceled() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, transport) = CreateRuntime();
        var taskId = TaskId.Parse("root/delay");
        await runtime.ScheduleDelayAsync(taskId, TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
        var handle = runtime.GetScheduledTaskHandle(taskId);
        var resume = Assert.Single(transport.Resumes);
        storage.Get(taskId).CancellationRequestedAt = runtime.UtcNow;

        await HandleAsync(CreateHandler(runtime), ResumeContext(runtime, taskId, resume.Generation, resume.DueTime));
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DurableTaskStatus.Canceled, storage.Get(taskId).Result!.Status);
        Assert.Equal(DurableTaskStatus.Canceled, (await handle.WaitAsync(TestContext.Current.CancellationToken)).Status);
    });

    [Fact]
    public Task ResumeFromAnotherGrainIsRejectedBeforeChangingState() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, transport) = CreateRuntime();
        var taskId = TaskId.Parse("root/delay");
        await runtime.ScheduleDelayAsync(taskId, TimeSpan.Zero, TestContext.Current.CancellationToken);
        var resume = Assert.Single(transport.Resumes);
        var forged = GrainId.Create("forged", "grain");
        var context = MessagingTestHelpers.CreateHandlerContext(MessagingTestHelpers.MessageTypes.Resume.Create(
            DurableTaskMessageTransport.CreateResumeMessageId(forged, taskId, resume.Generation, resume.DueTime),
            runtime.GrainId,
            new DurableTaskResumeMessage { TaskId = taskId, Generation = resume.Generation, Sender = forged },
            resume.DueTime).ToInboxMessage());

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await CreateHandler(runtime).HandleAsync(context, CancellationToken.None));

        context.DidNotReceive().Complete();
        Assert.Null(storage.Get(taskId).Result);
        Assert.Equal(1, manager.WriteCount);
    });

    [Fact]
    public Task DelayReplayKeepsTheRecordedDueTime() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, _, transport) = CreateRuntime();
        var taskId = TaskId.Parse("root/replayed-delay");

        await runtime.ScheduleDelayAsync(
            taskId,
            TimeSpan.FromMinutes(5),
            TestContext.Current.CancellationToken);
        var recordedDueTime = storage.Get(taskId).DueTime;
        var generation = storage.Get(taskId).ResumeGeneration;

        // A replay which requests another duration, for example after configuration changed, keeps the commitment.
        var replay = await runtime.ScheduleDelayAsync(
            taskId,
            TimeSpan.FromMinutes(6),
            TestContext.Current.CancellationToken);

        Assert.Same(DurableTaskResponse.Pending, replay);
        Assert.Equal(recordedDueTime, storage.Get(taskId).DueTime);
        Assert.Equal(generation, storage.Get(taskId).ResumeGeneration);

        // The resume message committed with the delay, so a replay sends nothing new.
        var resume = Assert.Single(transport.Resumes);
        Assert.Equal(recordedDueTime, resume.DueTime);
        await HandleAsync(CreateHandler(runtime), ResumeContext(runtime, taskId, resume.Generation, resume.DueTime));
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, storage.Get(taskId).Result!.Status);
    });

    [Fact]
    public Task CancellationWhileDelayWriteIsInFlightCancelsTheDelayPromptly() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, transport) = CreateRuntime();
        var rootId = TaskId.Parse("root");
        var delayWriteStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelayWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.AfterWriteStarted = async () =>
        {
            // Hold the write which commits the delay and its resume message.
            if (transport.Resumes.Count == 1 && delayWriteStarted.TrySetResult())
            {
                await releaseDelayWrite.Task;
            }
        };
        var request = CreateRequest(1, () => DurableTask.Run(async _ => await DurableTask.Delay(TimeSpan.FromDays(1))));

        await ((IDurableTaskServer)runtime).ScheduleAsync(rootId, request, TestContext.Current.CancellationToken);
        await delayWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var canceling = ((IDurableTaskServer)runtime).CancelAsync(rootId, TestContext.Current.CancellationToken).AsTask();
        releaseDelayWrite.SetResult();
        await canceling.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var response = await runtime.GetScheduledTaskHandle(rootId)
            .WaitAsync(TestContext.Current.CancellationToken)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(DurableTaskStatus.Canceled, response.Status);
        var resume = Assert.Single(transport.Resumes);
        var delay = storage.Get(resume.TaskId);
        Assert.Equal(DurableTaskStatus.Canceled, delay.Result!.Status);
        Assert.Null(delay.DueTime);

        // The resume committed with the delay is stale, so it changes nothing.
        var writes = manager.WriteCount;
        await HandleAsync(CreateHandler(runtime), ResumeContext(runtime, resume.TaskId, resume.Generation, resume.DueTime));
        Assert.Equal(writes, manager.WriteCount);
        Assert.Equal(DurableTaskStatus.Canceled, storage.Get(resume.TaskId).Result!.Status);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task DelayAwaitedWithoutAnIdentifierIsItsOwnChildTask(bool inAsyncMethod) => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, _, transport) = CreateRuntime();
        var rootId = TaskId.Parse("root");
        static async DurableTask WaitADay() => await DurableTask.Delay(TimeSpan.FromDays(1));
        var request = CreateRequest(
            1,
            () => inAsyncMethod
                ? WaitADay()
                : DurableTask.Run(async _ => await DurableTask.Delay(TimeSpan.FromDays(1))));

        await ((IDurableTaskServer)runtime).ScheduleAsync(rootId, request, TestContext.Current.CancellationToken);
        await transport.ResumeSent.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Only the delay's own task records a due time; its parent keeps running and replays it by identifier.
        var resume = Assert.Single(transport.Resumes);
        Assert.True(rootId.IsParentOf(resume.TaskId));
        Assert.All(storage.Tasks, task => Assert.Equal(task.Id == resume.TaskId, task.State.DueTime is not null));
    });

    [Fact]
    public Task DiagnosticsDescribeEachTaskStatusAndResult() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, _, _) = CreateRuntime();
        var dueTime = new DateTimeOffset(2040, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var target = GrainId.Create("target", "one");
        void Stage(string id, Action<TaskId, TestTaskState> stage)
        {
            var taskId = TaskId.Parse(id);
            stage(taskId, storage.GetOrCreate(taskId));
        }

        Stage("root/completed", (id, state) => storage.SetResponse(id, state, DurableTaskResponse.FromResult(42)));
        Stage("root/faulted", (id, state) => storage.SetResponse(id, state, DurableTaskResponse.FromException(new InvalidOperationException("boom"))));
        Stage("root/canceled", (id, state) => storage.SetResponse(id, state, DurableTaskResponse.Canceled));
        Stage("root/canceling", (id, state) => storage.RequestCancellation(id, state));
        Stage("root/delayed", (id, state) => storage.SetDelay(id, state, dueTime, generation: 1));
        Stage("root/remote", (id, state) => storage.SetRemoteRequest(id, state, target, "fingerprint"));

        var diagnostics = new Dictionary<string, DurableTaskDiagnosticState>();
        await foreach (var (taskId, state) in ((IDurableTaskGrainExtension)runtime).GetTasksAsync(TestContext.Current.CancellationToken))
        {
            diagnostics[taskId.ToString()] = state;
        }

        Assert.Equal(("Completed", "42"), (diagnostics["root/completed"].Status, diagnostics["root/completed"].Response));
        Assert.Equal(
            ("Faulted", "InvalidOperationException: boom"),
            (diagnostics["root/faulted"].Status, diagnostics["root/faulted"].Response));
        Assert.Equal(("Canceled", null), (diagnostics["root/canceled"].Status, diagnostics["root/canceled"].Response));
        Assert.Equal("Canceling", diagnostics["root/canceling"].Status);
        Assert.Equal(("Delayed", dueTime), (diagnostics["root/delayed"].Status, diagnostics["root/delayed"].DueTime));
        Assert.Equal(("Pending", target.ToString()), (diagnostics["root/remote"].Status, diagnostics["root/remote"].RemoteTarget));
        Assert.Equal(6, diagnostics.Count);
    });

    [Fact]
    public Task ResumeForMissingTaskCompletesWithoutCreatingState() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var taskId = TaskId.Parse("root/missing");

        await HandleAsync(CreateHandler(runtime), ResumeContext(runtime, taskId, generation: 1, DateTimeOffset.UtcNow));

        Assert.False(storage.Contains(taskId));
        Assert.Equal(0, manager.WriteCount);
    });

    [Fact]
    public Task LocalDelayRemainsPendingUntilResumeAndRecoveryDoesNotResendIt() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, transport) = CreateRuntime();
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

        // The resume message is durable with the delay, so recovery leaves the delay pending without resending it.
        var writes = manager.WriteCount;
        await runtime.ResumePendingTasksAsync(TestContext.Current.CancellationToken);
        Assert.Single(transport.Resumes);
        Assert.Equal(writes, manager.WriteCount);
        Assert.Null(storage.Get(taskId).CompletedAt);

        var state = storage.Get(taskId);
        await HandleAsync(CreateHandler(runtime), ResumeContext(runtime, taskId, state.ResumeGeneration, state.DueTime!.Value));
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, (await handle.WaitAsync(TestContext.Current.CancellationToken)).Status);
        Assert.NotNull(storage.Get(taskId).CompletedAt);
    });

    [Fact]
    public Task RecoveredCanceledDelayTerminalizesAndStaleResumesAreHarmless() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, _, transport) = CreateRuntime();
        var taskId = TaskId.Parse("root/delay");
        await runtime.ScheduleDelayAsync(taskId, TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
        var resume = Assert.Single(transport.Resumes);

        storage.Get(taskId).CancellationRequestedAt = runtime.UtcNow;
        await runtime.ResumePendingTasksAsync(TestContext.Current.CancellationToken);

        var canceled = storage.Get(taskId);
        Assert.Equal(DurableTaskStatus.Canceled, canceled.Result!.Status);
        Assert.NotNull(canceled.CompletedAt);
        Assert.Null(canceled.DueTime);
        Assert.True(canceled.ResumeGeneration > resume.Generation);
        Assert.Single(transport.Resumes);

        var handler = CreateHandler(runtime);
        await HandleAsync(handler, ResumeContext(runtime, taskId, resume.Generation, resume.DueTime));
        await HandleAsync(handler, ResumeContext(runtime, taskId, resume.Generation, resume.DueTime));
        Assert.Equal(DurableTaskStatus.Canceled, storage.Get(taskId).Result!.Status);
        Assert.Equal(canceled.CompletedAt, storage.Get(taskId).CompletedAt);
    });

    [Fact]
    public Task PollingOnlyCallerIsNotRegisteredAsCompletionDestination() => ActivationScheduler.RunAsync(async () =>
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
    });

    [Fact]
    public Task RecoveredRemoteChildRetainsCancellationTargetAndReplayIdentity() => ActivationScheduler.RunAsync(async () =>
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
    });

    [Fact]
    public Task RemoteCancellationMarksStateBeforeStagingMessage() => ActivationScheduler.RunAsync(async () =>
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
            Assert.Equal(0, manager.WriteCount);
        };

        await runtime.CancelRemoteAsync(taskId, target, reason: null, TestContext.Current.CancellationToken);

        // The cancellation and its message commit in one write; the outbox delivers only committed messages.
        Assert.Equal(1, manager.WriteCount);
        Assert.Single(transport.Cancellations);
        Assert.NotNull(storage.Get(taskId).CancellationRequestedAt);
    });

    [Fact]
    public Task FailedCancellationWriteFencesAndKeepsMonotonicCancellation() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var taskId = TaskId.Parse("root/cancellation-retry");
        storage.GetOrCreate(taskId);
        manager.BeforeWrite = () =>
        {
            manager.BeforeWrite = null;
            throw new IOException("Expected cancellation write failure.");
        };

        await Assert.ThrowsAsync<IOException>(
            () => runtime.SignalCancellationAsync(
                taskId,
                TestContext.Current.CancellationToken).AsTask());

        Assert.NotNull(storage.Get(taskId).CancellationRequestedAt);
        Assert.True(manager.IsFenced);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.SignalCancellationAsync(
                TaskId.Parse("root/other"),
                TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(0, manager.WriteCount);
    });

    [Fact]
    public Task RecoveryLeavesCanceledRemoteChildAwaitingItsCallee() => ActivationScheduler.RunAsync(async () =>
    {
        var (_, storage, manager, _) = CreateRuntime();
        var childId = TaskId.Parse("root/remote");
        var target = GrainId.Create("target", "one");
        var child = storage.GetOrCreate(childId);
        storage.SetRemoteRequest(childId, child, target, "fingerprint");
        storage.RequestCancellation(childId, child);
        var (recovered, _, _, transport) = CreateRuntime(storage, manager);

        await recovered.ResumePendingTasksAsync(TestContext.Current.CancellationToken);

        // The cancellation message committed with the request; the callee decides the child's outcome.
        Assert.Equal(0, manager.WriteCount);
        Assert.Null(storage.Get(childId).Result);
        Assert.Empty(transport.Cancellations);
        recovered.AcceptResponseFromInbox(childId, DurableTaskResponse.FromResult(5), target);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(5, storage.Get(childId).Result!.GetResult<int>());
    });

    [Fact]
    public Task LocalAndRemoteRequestIdentitiesCannotShareTaskId() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, _, _) = CreateRuntime();
        var taskId = TaskId.Parse("identity");
        runtime.ScheduleFromInbox(taskId, CreateRequest(1));

        var remoteException = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.ScheduleChildAsync(taskId, new TestStateManager.TestRemoteDurableTask(CreateRemoteRequest(2)), TestContext.Current.CancellationToken).AsTask());
        Assert.Contains("different request", remoteException.Message, StringComparison.Ordinal);

        var remoteTaskId = TaskId.Parse("remote-identity");
        await runtime.ScheduleChildAsync(remoteTaskId, new TestStateManager.TestRemoteDurableTask(CreateRemoteRequest(3)), TestContext.Current.CancellationToken);
        var topLevelException = Assert.Throws<DurableTaskConflictException>(
            () => runtime.ScheduleFromInbox(remoteTaskId, CreateRequest(4)));
        Assert.Contains("remote child request", topLevelException.Message, StringComparison.Ordinal);
        Assert.Equal(remoteTaskId, topLevelException.TaskId);
        Assert.NotNull(storage.Get(remoteTaskId).RemoteRequestFingerprint);

        var delayTaskId = TaskId.Parse("delay-identity");
        await runtime.ScheduleDelayAsync(delayTaskId, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
        var delayException = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.ScheduleChildAsync(delayTaskId, new TestStateManager.TestRemoteDurableTask(CreateRemoteRequest(5)), TestContext.Current.CancellationToken).AsTask());
        Assert.Contains("different request", delayException.Message, StringComparison.Ordinal);
    });

    [Fact]
    public Task RunningLocalChildRejectsRemoteReuseWithoutChangingIdentity() => ActivationScheduler.RunAsync(async () =>
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
    });

    [Fact]
    public Task FailedSchedulingRemovesTransientHandle() => ActivationScheduler.RunAsync(async () =>
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
    });

    [Fact]
    public Task PersistedLocalChildCancellationTerminalizesBeforeScheduling() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, _, _) = CreateRuntime();
        var taskId = TaskId.Parse("root/local");
        var state = storage.GetOrCreate(taskId);
        storage.RequestCancellation(taskId, state);
        var task = new TestSchedulableTask(static (_, _) => new(DurableTaskResponse.Pending));

        var handle = await runtime.ScheduleChildAsync(taskId, task, TestContext.Current.CancellationToken);

        Assert.Equal(DurableTaskStatus.Canceled, (await handle.WaitAsync(TestContext.Current.CancellationToken)).Status);
        Assert.Equal(0, task.ScheduleAsyncCallCount);
    });

    [Fact]
    public Task ReplayedCanceledRemoteChildAwaitsItsCalleeOutcome() => ActivationScheduler.RunAsync(async () =>
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

        Assert.False((await handle.PollAsync(new PollingOptions { PollTimeout = TimeSpan.Zero }, TestContext.Current.CancellationToken)).IsCompleted);
        Assert.Empty(transport.Cancellations);
        Assert.Equal(0, retry.ScheduleAsyncCallCount);
        recovered.AcceptResponseFromInbox(taskId, DurableTaskResponse.FromResult(9), request.Context!.TargetId);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(9, (await handle.WaitAsync(TestContext.Current.CancellationToken)).GetResult<int>());
    });

    [Fact]
    public Task CancelingLocalChildSignalsActiveDurableContext() => ActivationScheduler.RunAsync(async () =>
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
        Assert.Equal(DurableTaskStatus.Canceled, (await handle.WaitAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).Status);
        Assert.Equal(DurableTaskStatus.Canceled, storage.Get(taskId).Result!.Status);
    });

    [Fact]
    public Task StopAbortsRunningAttemptDiscardsItsResultAndReplaysInNextActivation() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, transport) = CreateRuntime();
        var caller = GrainId.Create("caller", "one");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aborted = new TaskCompletionSource<(bool ExecutionAborted, bool DurablyCanceled)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var request = CreateRequest(
            1,
            () => DurableTask.Run(async executionToken =>
            {
                if (Interlocked.Increment(ref attempts) > 1)
                {
                    return 7;
                }

                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, executionToken);
                }
                catch (OperationCanceledException) when (executionToken.IsCancellationRequested)
                {
                    // Host abort reaches the delegate token, but it is not durable cancellation.
                    var context = DurableTask.Current;
                    aborted.TrySetResult((executionToken.IsCancellationRequested, context.IsCancellationRequested));
                }

                return 42;
            }));
        var taskId = TaskId.Parse("root");

        runtime.ScheduleFromInbox(taskId, request);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var stopping = runtime.StopAsync(TestContext.Current.CancellationToken);
        var observed = await aborted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await stopping.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(observed.ExecutionAborted);
        Assert.False(observed.DurablyCanceled);
        Assert.Null(storage.Get(taskId).Result);
        Assert.Null(storage.Get(taskId).CancellationRequestedAt);
        Assert.Empty(transport.Completions);

        var (replacement, _, _, replacementTransport) = CreateRuntime(storage, manager);
        await replacement.ResumePendingTasksAsync(TestContext.Current.CancellationToken);
        var response = await replacement.GetScheduledTaskHandle(taskId)
            .WaitAsync(TestContext.Current.CancellationToken)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(2, attempts);
        Assert.Equal(7, response.GetResult<int>());
        Assert.Equal(7, storage.Get(taskId).Result!.GetResult<int>());
        var completion = Assert.Single(replacementTransport.Completions);
        Assert.Equal(caller, completion.Target);
        Assert.Equal(7, completion.Materialize(replacement).Response.GetResult<int>());
        Assert.Empty(transport.Completions);
    });

    [Fact]
    public Task StopDiscardsFailureProducedAfterExecutionShutdown() => ActivationScheduler.RunAsync(async () =>
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
        await transport.ResumeSent.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var stopping = runtime.StopAsync(TestContext.Current.CancellationToken);
        await stopping.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Null(storage.Get(taskId).Result);
        Assert.Empty(transport.Completions);
    });

    [Fact]
    public Task RecoveredPendingRequestStartsOnlyOnActivation() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, _, _) = CreateRuntime();
        var stateMachine = (IStateMachine)runtime;
        var taskId = TaskId.Parse("root/initial-recovery");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = CreateRequest(1, () => DurableTask.Run(_ => started.TrySetResult()));

        // Recovery resets the runtime's volatile state, replays journaled task state, and completes before activation.
        stateMachine.Reset(default);
        var state = storage.GetOrCreate(taskId);
        storage.SetRequest(taskId, state, request);
        storage.SetRequestFingerprint(
            taskId,
            state,
            IDurableTaskRequest.GetFingerprint(request, CreateSerializer()));
        stateMachine.OnRecoveryCompleted();

        Assert.False(started.Task.IsCompleted);
        Assert.Equal(0, request.CreateTaskCallCount);

        await runtime.RecoverAsync(TestContext.Current.CancellationToken);

        // Recovery defers the execution until the activation has completed.
        Assert.Equal(0, request.CreateTaskCallCount);

        runtime.StartRecoveredExecutions();
        runtime.StartRecoveredExecutions();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(1, request.CreateTaskCallCount);
    });

    [Fact]
    public Task RecoveredRequestWhichACallStartsFirstStartsOnce() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, _, _) = CreateRuntime();
        var stateMachine = (IStateMachine)runtime;
        var taskId = TaskId.Parse("root/call-starts-recovered");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = CreateRequest(1, () => DurableTask.Run(async _ =>
        {
            started.TrySetResult();
            await release.Task;
        }));

        stateMachine.Reset(default);
        var state = storage.GetOrCreate(taskId);
        storage.SetRequest(taskId, state, request);
        storage.SetRequestFingerprint(
            taskId,
            state,
            IDurableTaskRequest.GetFingerprint(request, CreateSerializer()));
        stateMachine.OnRecoveryCompleted();
        await runtime.RecoverAsync(TestContext.Current.CancellationToken);

        // A call which reaches the recovered task before the activation completes starts it.
        var response = await ((IDurableTaskServer)runtime).ScheduleAsync(taskId, request, TestContext.Current.CancellationToken);
        Assert.Equal(DurableTaskStatus.Pending, response.Status);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(1, request.CreateTaskCallCount);

        runtime.StartRecoveredExecutions();
        release.SetResult();
        var result = await runtime.GetScheduledTaskHandle(taskId)
            .WaitAsync(TestContext.Current.CancellationToken)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, result.Status);
        Assert.Equal(1, request.CreateTaskCallCount);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task RecoveryPreservesLocalChildHandleIdentityAcrossStorageOrder(bool parentFirst) => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, _, _) = CreateRuntime();
        var parentId = TaskId.Parse("root");
        var childId = TaskId.Parse("root/child");
        IScheduledTaskHandle? capturedChildHandle = null;
        var parentRequest = CreateRequest(
            1,
            () => new AwaitExistingChildTask(childId, handle => capturedChildHandle = handle));
        var childRequest = CreateRequest(2, () => DurableTask.FromResult(42));

        if (parentFirst)
        {
            AddRequest(parentId, parentRequest);
            AddRequest(childId, childRequest);
        }
        else
        {
            AddRequest(childId, childRequest);
            AddRequest(parentId, parentRequest);
        }

        await runtime.ResumePendingTasksAsync(TestContext.Current.CancellationToken);
        var response = await runtime.GetScheduledTaskHandle(parentId)
            .WaitAsync(TestContext.Current.CancellationToken)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var capturedInnerHandle = Assert.IsAssignableFrom<IScheduledTaskHandle>(
            capturedChildHandle!.GetType()
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(field => typeof(IScheduledTaskHandle).IsAssignableFrom(field.FieldType))
                .GetValue(capturedChildHandle));

        Assert.Equal(42, response.GetResult<int>());
        Assert.Same(runtime.GetScheduledTaskHandle(childId), capturedInnerHandle);
        Assert.Equal(1, parentRequest.CreateTaskCallCount);
        Assert.Equal(1, childRequest.CreateTaskCallCount);

        void AddRequest(TaskId taskId, RuntimeTestDurableTaskRequest request)
        {
            var state = storage.GetOrCreate(taskId);
            storage.SetRequest(taskId, state, request);
            storage.SetRequestFingerprint(
                taskId,
                state,
                IDurableTaskRequest.GetFingerprint(request, CreateSerializer()));
        }
    });

    [Fact]
    public Task RecoveryReusesPersistedLogicalTime() => ActivationScheduler.RunAsync(async () =>
    {
        var initialTime = DateTimeOffset.UtcNow;
        var clock = new FakeTimeProvider(initialTime);
        var (runtime, storage, manager, _) = CreateRuntime(timeProvider: clock);
        var taskId = TaskId.Parse("root/logical-time");
        var observations = new ConcurrentQueue<DateTimeOffset>();
        var request = CreateRequest(
            1,
            () => DurableTask.Run(_ => observations.Enqueue(DurableTask.Current.UtcNow)));
        var state = storage.GetOrCreate(taskId);
        storage.SetRequest(taskId, state, request);
        storage.SetRequestFingerprint(
            taskId,
            state,
            IDurableTaskRequest.GetFingerprint(request, CreateSerializer()));

        await runtime.ResumePendingTasksAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => observations.Count == 1);
        await WaitUntilAsync(() => state.Result is { IsCompleted: true });
        state.Result = null;
        state.CompletedAt = null;
        clock.Advance(TimeSpan.FromMinutes(10));

        var (replacement, _, _, _) = CreateRuntime(storage, manager, timeProvider: clock);
        await replacement.ResumePendingTasksAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => observations.Count == 2);

        Assert.Equal([initialTime, initialTime], observations);
        Assert.Equal(initialTime, storage.LogicalTimes[taskId]);
    });

    [Fact]
    public Task CancelingLocalTaskCancelsRemoteDescendantsBeforeTerminalizingParent() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, transport) = CreateRuntime();
        var rootId = TaskId.Parse("root");
        var childId = TaskId.Parse("root/local-child");
        var grandchildId = TaskId.Parse("root/local-child/remote-grandchild");
        var unrelatedId = TaskId.Parse("unrelated");
        var target = GrainId.Create("target", "one");
        var root = storage.GetOrCreate(rootId);
        var child = storage.GetOrCreate(childId);
        var grandchild = storage.GetOrCreate(grandchildId);
        storage.SetRemoteRequest(grandchildId, grandchild, target, "fingerprint");
        var unrelated = storage.GetOrCreate(unrelatedId);
        var rootHandle = runtime.GetScheduledTaskHandle(rootId);
        transport.BeforeSendCancellation = (_, _, taskId) =>
        {
            Assert.Equal(grandchildId, taskId);
            Assert.Null(root.Result);
        };
        manager.BeforeWrite = () =>
        {
            Assert.NotNull(child.CancellationRequestedAt);
            Assert.NotNull(grandchild.CancellationRequestedAt);
            Assert.Single(transport.Cancellations);
        };

        await rootHandle.CancelAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(root.CancellationRequestedAt);
        Assert.Equal(DurableTaskStatus.Canceled, root.Result!.Status);
        Assert.NotNull(child.CancellationRequestedAt);
        Assert.NotNull(grandchild.CancellationRequestedAt);
        Assert.Null(unrelated.CancellationRequestedAt);
        Assert.Null(unrelated.Result);
        var cancellation = Assert.Single(transport.Cancellations);
        Assert.Equal(grandchildId, cancellation.TaskId);
        Assert.Equal(target, cancellation.Target);
        Assert.Equal(2, manager.WriteCount);
    });

    [Fact]
    public Task AncestorCancellationPreservesCachedSuccessfulChild() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, _, _) = CreateRuntime();
        var rootId = TaskId.Parse("root");
        var childId = TaskId.Parse("root/child");
        var root = storage.GetOrCreate(rootId);
        var firstHandle = await runtime.ScheduleChildAsync(
            childId,
            DurableTask.FromResult(42),
            TestContext.Current.CancellationToken);
        Assert.Equal(42, (await firstHandle.WaitAsync(TestContext.Current.CancellationToken)).GetResult<int>());
        storage.RequestCancellation(rootId, root);

        var reattached = await runtime.ScheduleChildAsync(
            childId,
            DurableTask.FromResult(42),
            TestContext.Current.CancellationToken);

        Assert.Same(firstHandle, reattached);
        Assert.Equal(42, (await reattached.WaitAsync(TestContext.Current.CancellationToken)).GetResult<int>());
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, storage.Get(childId).Result!.Status);
    });

    [Fact]
    public Task WhenAnyAwaitsItsCandidatesWithoutPolling() => ActivationScheduler.RunAsync(async () =>
    {
        // The clock never advances, so only awaiting the candidates can select a winner.
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var (runtime, storage, _, _) = CreateRuntime(timeProvider: time);
        var rootId = TaskId.Parse("root");
        var first = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = CreateRequest(1, () => DurableTask.Run(async _ =>
        {
            var (index, result) = await DurableTask.WhenAny(
            [
                DurableTask.Run(async _ =>
                {
                    started.TrySetResult();
                    return await first.Task;
                }),
                DurableTask.Run(async cancellationToken =>
                {
                    secondStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return 0;
                }),
            ]);
            return index * 100 + result;
        }));

        await ((IDurableTaskServer)runtime).ScheduleAsync(rootId, request, TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Complete the first candidate only once both candidates run, so the selection has found no winner and waits.
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);
        first.SetResult(7);
        var response = await runtime.GetScheduledTaskHandle(rootId)
            .WaitAsync(TestContext.Current.CancellationToken)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(7, response.GetResult<int>());

        // The candidates' completion order selects the winner, so no decision is recorded.
        Assert.DoesNotContain(storage.Tasks, task => task.Id.ToString().EndsWith("/$winner", StringComparison.Ordinal));
        await runtime.StopAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    });

    [Fact]
    public Task LogicalTimeAdvancesToObservedChildCompletionsAndReplays() => ActivationScheduler.RunAsync(async () =>
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2040, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var start = time.GetUtcNow();
        var manager = new TestStateManager();
        var storage = new TestStorage(manager, time);
        var (runtime, _, _, _) = CreateRuntime(storage, manager, timeProvider: time);
        var rootId = TaskId.Parse("root");
        var observations = new ConcurrentQueue<(DateTimeOffset Before, DateTimeOffset After)>();
        var attempts = 0;
        var firstAttemptObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = CreateRequest(1, () => DurableTask.Run(async cancellationToken =>
        {
            var context = DurableTask.Current;
            var before = context.UtcNow;
            await DurableTask.Run(_ => time.Advance(TimeSpan.FromHours(1))).WithId("step");
            observations.Enqueue((before, context.UtcNow));
            if (Interlocked.Increment(ref attempts) == 1)
            {
                // The first attempt stops here and is aborted, so the next activation replays it.
                firstAttemptObserved.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        }));

        await ((IDurableTaskServer)runtime).ScheduleAsync(rootId, request, TestContext.Current.CancellationToken);
        await firstAttemptObserved.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await runtime.StopAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromHours(4));

        var (replacement, _, _, _) = CreateRuntime(storage, manager, timeProvider: time);
        await replacement.ResumePendingTasksAsync(TestContext.Current.CancellationToken);
        await replacement.GetScheduledTaskHandle(rootId)
            .WaitAsync(TestContext.Current.CancellationToken)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(
            [(start, start.AddHours(1)), (start, start.AddHours(1))],
            observations.ToArray());
    });

    [Fact]
    public Task ExecutionContextDispatchesRuntimeOperationsToCapturedScheduler() => ActivationScheduler.RunAsync(async () =>
    {
        var schedulerPair = new ConcurrentExclusiveSchedulerPair();
        var scheduler = schedulerPair.ExclusiveScheduler;
        var runtime = new SchedulerProbeRuntime(scheduler);
        using var shutdown = new CancellationTokenSource();
        var context = await Task.Factory.StartNew(
            () => new GrainDurableTaskContext(
                TaskId.Parse("root"),
                runtime,
                scheduler,
                shutdown.Token),
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            scheduler);

        var response = await DurableTaskRuntimeHelper.RunAsync(
            DurableTask.WhenAny([DurableTask.Run(static _ => { })]),
            context);

        Assert.Equal(0, response.GetResult<int>());
        Assert.Equal(2, runtime.SchedulerInvocationCount);
        schedulerPair.Complete();
        await schedulerPair.Completion;
    });

    [Fact]
    public Task ExecutionContextRejectsUnrelatedCompletionSelectionIds() => ActivationScheduler.RunAsync(async () =>
    {
        var runtime = new SchedulerProbeRuntime(TaskScheduler.Current);
        var context = new GrainDurableTaskContext(
            TaskId.Parse("root"),
            runtime,
            TaskScheduler.Current,
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => InvokeSelectCompletionCoreAsync(context, TaskId.Parse("other/decision"), [TaskId.Parse("root/child")], TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => InvokeSelectCompletionCoreAsync(context, TaskId.Parse("root/decision"), [TaskId.Parse("other/child")], TestContext.Current.CancellationToken).AsTask());
    });

    [Fact]
    public void ExecutionContextChildIdsSeparateGeneratedAndExplicitNamespaces()
    {
        var context = new GrainDurableTaskContext(
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
        Assert.Equal(TaskId.Parse("root/job$2"), repeatedName);
        Assert.Equal(TaskId.Parse("root/0"), explicitNumeric);
        var exception = Assert.Throws<TargetInvocationException>(
            () => InvokeCreateChildTaskId(context, "$reserved"));
        Assert.IsType<ArgumentException>(exception.InnerException);
    }

    [Fact]
    public Task CompletionDecisionMustRetainCandidateIdentity() => ActivationScheduler.RunAsync(async () =>
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

        Assert.Equal(
            $"Durable completion decision '{decisionId}' is already associated with another operation. It records a selection of the first task to complete, and this attempt requested a selection of the first task to complete among 1 candidate. A replay schedules the same operation, with the same arguments, under each identifier; new workflow steps use new identifiers.",
            exception.Message);
    });

    [Fact]
    public Task CompletionDecisionRejectsExistingChildOperation() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, _, _) = CreateRuntime();
        var decisionId = TaskId.Parse("root/decision");
        await runtime.ScheduleDelayAsync(decisionId, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
        var dueTime = storage.Get(decisionId).DueTime!.Value;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.SelectCompletionAsync(
                decisionId,
                [TaskId.Parse("root/candidate/0"), TaskId.Parse("root/candidate/1")],
                TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(
            $"Durable completion decision '{decisionId}' is already associated with another operation. It records a delay due at {dueTime:O}, and this attempt requested a selection of the first task to complete among 2 candidates. A replay schedules the same operation, with the same arguments, under each identifier; new workflow steps use new identifiers.",
            exception.Message);
    });

    [Fact]
    public Task ChildScheduledUnderARecordedSelectionNamesTheSelection() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, _, _) = CreateRuntime();
        var decisionId = TaskId.Parse("root/race/$winner");
        var winner = TaskId.Parse("root/race/0");
        storage.GetOrCreate(winner).Result = DurableTaskResponse.Completed;
        await runtime.SelectCompletionAsync(decisionId, [winner], TestContext.Current.CancellationToken);

        // The workflow changed: a replay schedules a local step where the first execution recorded a race's selection.
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.ScheduleChildAsync(decisionId, DurableTask.FromResult(1), TestContext.Current.CancellationToken).AsTask());

        Assert.Contains(
            "It records a selection of the first task to complete, and this attempt requested a local child task.",
            exception.Message,
            StringComparison.Ordinal);
    });

    [Fact]
    public Task ExecutionShutdownCancelsAdapterSchedulingWithoutRequestingDurableCancellation() => ActivationScheduler.RunAsync(async () =>
    {
        var runtime = new ShutdownProbeRuntime();
        using var shutdown = new CancellationTokenSource();
        var context = new GrainDurableTaskContext(
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
        Assert.True(context.CancellationToken.IsCancellationRequested);
        Assert.False(context.IsCancellationRequested);
    });

    [Fact]
    public Task DurableCancellationStillTerminalizesRunningExecution() => ActivationScheduler.RunAsync(async () =>
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
    });

    [Fact]
    public Task StopEstablishesShutdownDespiteCanceledCallerToken() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, _, _, _) = CreateRuntime();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await runtime.StopAsync(cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runtime.ResumePendingTasksAsync(TestContext.Current.CancellationToken));
    });

    [Fact]
    public Task StopDoesNotWaitForStagedInboxResponseCommit() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var taskId = TaskId.Parse("root/remote");
        var target = GrainId.Create("target", "one");
        var state = storage.GetOrCreate(taskId);
        storage.SetRemoteRequest(taskId, state, target, "fingerprint");
        var handle = runtime.GetScheduledTaskHandle(taskId);
        runtime.AcceptResponseFromInbox(taskId, DurableTaskResponse.FromResult(42), target);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await runtime.StopAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Local waiters observe shutdown; the staged response still commits with the inbox batch for the next activation.
        Assert.Equal(DurableTaskStatus.Canceled, (await handle.WaitAsync(TestContext.Current.CancellationToken)).Status);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(42, storage.Get(taskId).Result!.GetResult<int>());
        Assert.Equal(DurableTaskStatus.Canceled, (await handle.WaitAsync(TestContext.Current.CancellationToken)).Status);
    });

    [Fact]
    public Task StopDrainsUncooperativeExecutionBeforeReplacementReplay() => ActivationScheduler.RunAsync(async () =>
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
    });

    [Fact]
    public Task FirstTerminalResponseWinsAndRetirementRemovesOnlyItsDestination() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, transport) = CreateRuntime();
        var taskId = TaskId.Parse("root/child");
        var target = GrainId.Create("target", "one");
        var waiter = GrainId.Create("caller", "one");
        var other = GrainId.Create("caller", "two");
        var state = storage.GetOrCreate(taskId);
        storage.SetRemoteRequest(taskId, state, target, "fingerprint");
        state.RequestFingerprint = "invocation";
        state.CompletionDestinations.Add(waiter);
        state.CompletionDestinations.Add(other);
        var handle = runtime.GetScheduledTaskHandle(taskId);

        runtime.AcceptResponseFromInbox(taskId, DurableTaskResponse.FromResult(7), target);
        runtime.AcceptResponseFromInbox(taskId, DurableTaskResponse.FromResult(8), target);
        var waiterCompletion = DurableTaskMessageTransport.CreateMessageId(runtime.GrainId, waiter, taskId, DurableTaskMessageTypes.CompletionSubject);
        Assert.True(((IOutboxMessageSource)runtime).TryGetMessage(waiterCompletion, out _));
        ((IOutboxMessageSource)runtime).OnRetired(waiterCompletion, OutboxMessageRetirement.Delivered);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(7, storage.Get(taskId).Result!.GetResult<int>());
        Assert.Equal(7, (await handle.WaitAsync(TestContext.Current.CancellationToken)).GetResult<int>());
        Assert.DoesNotContain(waiter, storage.Get(taskId).CompletionDestinations);
        Assert.Contains(other, storage.Get(taskId).CompletionDestinations);
        Assert.False(((IOutboxMessageSource)runtime).TryGetMessage(waiterCompletion, out _));
        Assert.Empty(transport.Completions);
    });

    [Fact]
    public Task ReplayedLocalChildReplacesRehydratedPlaceholder() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, _, _) = CreateRuntime();
        var taskId = TaskId.Parse("root/local");
        storage.GetOrCreate(taskId);

        var handle = await runtime.ScheduleChildAsync(taskId, DurableTask.Run(_ => { }), TestContext.Current.CancellationToken);

        Assert.Equal(taskId, handle.TaskId);
    });

    [Fact]
    public Task SynchronousLocalStepCommitsItsRecordWithItsResultInOneWrite() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var taskId = TaskId.Parse("root/local");
        var invocations = 0;

        // The step runs first, so the write which records the step also records its result.
        manager.BeforeWrite = () => Assert.Equal(42, storage.Get(taskId).Result!.GetResult<int>());
        var handle = await runtime.ScheduleChildAsync(
            taskId,
            DurableTask.Run(_ =>
            {
                Interlocked.Increment(ref invocations);
                return 42;
            }),
            TestContext.Current.CancellationToken);
        var response = await handle.WaitAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(42, response.GetResult<int>());
        Assert.Equal(1, invocations);
        Assert.Equal(1, manager.WriteCount);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task LocalStepStagesItsResultInTheWorkItemWhichRunsItsFinalCode(bool asyncMethod) => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, _, _) = CreateRuntime();
        var taskId = TaskId.Parse("root/local");
        var suspended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool>? probe = null;

        // The step's final code queues work on the activation, as a grain operation which requests a journal write while
        // the step completes would. That work runs before any continuation of the step, so the write it requests commits
        // the step's state changes; it must find the step's result staged with them.
        int FinalCode()
        {
            probe = Task.Factory.StartNew(
                () => storage.Get(taskId).Result is { IsCompleted: true },
                CancellationToken.None,
                TaskCreationOptions.None,
                TaskScheduler.Current);
            return 42;
        }

        async Task Suspend()
        {
            suspended.SetResult();
            await resume.Task;
        }

        async DurableTask<int> Step()
        {
            await Suspend();
            return FinalCode();
        }

        var definition = asyncMethod
            ? Step()
            : DurableTask.Run(async _ =>
            {
                await Suspend();
                return FinalCode();
            });
        var handle = await runtime.ScheduleChildAsync(taskId, definition, TestContext.Current.CancellationToken);
        await suspended.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        resume.SetResult();
        var response = await handle.WaitAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(42, response.GetResult<int>());
        Assert.NotNull(probe);
        Assert.True(await probe);
    });

    [Fact]
    public Task OnceCommitsItsRecordWithTheNextWriteAndReplayReturnsItWithoutRunningTheEffect() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, transport) = CreateRuntime();
        var taskId = TaskId.Parse("root");
        var effectId = TaskId.Parse("root/$once:effect");
        var runs = 0;
        var observed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = CreateRequest(
            1,
            () => DurableTask.Run(async _ =>
            {
                var value = DurableTask.Once("effect", () => 40 + Interlocked.Increment(ref runs));
                observed.TrySetResult(value);
                await DurableTask.Delay(TimeSpan.FromDays(1));
                return value;
            }));

        // The record stages without a write of its own, and commits with the delay's write.
        manager.BeforeWrite = () =>
        {
            if (storage.Contains(effectId))
            {
                Assert.True(storage.Contains(TaskId.Parse("root/$child-1")));
            }
        };
        runtime.ScheduleFromInbox(taskId, request);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(41, await observed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        await transport.ResumeSent.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(41, storage.Get(effectId).Result!.GetResult<int>());
        await runtime.StopAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        observed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (replacement, _, _, _) = CreateRuntime(storage, manager);
        await replacement.ResumePendingTasksAsync(TestContext.Current.CancellationToken);

        Assert.Equal(41, await observed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(1, runs);
        await replacement.StopAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    });

    [Fact]
    public Task ReplayReportsReplayingUntilItReachesEveryRecordedOperation() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var observations = new ConcurrentQueue<(int Run, string Point, bool Replaying)>();
        TaskCompletionSource<DurableTaskContext>[] waiting = [new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously)];
        var runs = 0;
        var request = CreateRequest(
            1,
            () => DurableTask.Run(async _ =>
            {
                var run = Interlocked.Increment(ref runs);
                var context = DurableTask.Current;
                observations.Enqueue((run, "start", context.IsReplaying));
                DurableTask.Once("effect", () => 1);
                observations.Enqueue((run, "effect", context.IsReplaying));
                await DurableTask.Run(static _ => 2).WithId("step");
                observations.Enqueue((run, "step", context.IsReplaying));
                waiting[run - 1].TrySetResult(context);
                await DurableTask.Delay(TimeSpan.FromDays(1)).WithId("delay");
            }));

        runtime.ScheduleFromInbox(TaskId.Parse("root"), request);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        var first = await waiting[0].Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => storage.Contains(TaskId.Parse("root/delay")));
        await runtime.StopAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var (replacement, _, _, _) = CreateRuntime(storage, manager);
        await replacement.ResumePendingTasksAsync(TestContext.Current.CancellationToken);
        var replay = await waiting[1].Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // The replay reaches the delay, the last operation which the first run recorded, and then runs live.
        await WaitUntilAsync(() => !replay.IsReplaying);
        Assert.False(first.IsReplaying);
        Assert.Equal(
            [
                (1, "start", false), (1, "effect", false), (1, "step", false),
                (2, "start", true), (2, "effect", true), (2, "step", true),
            ],
            observations);
        await replacement.StopAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    });

    [Fact]
    public Task ReplayWhichStartsAnUnrecordedOperationStopsReplaying() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var observations = new ConcurrentQueue<(int Run, string Point, bool Replaying)>();
        TaskCompletionSource[] waiting = [new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously)];
        var runs = 0;
        var request = CreateRequest(
            1,
            () => DurableTask.Run(async _ =>
            {
                var run = Interlocked.Increment(ref runs);
                var context = DurableTask.Current;
                if (run > 1)
                {
                    // A deployment added an effect before the recorded ones.
                    observations.Enqueue((run, "start", context.IsReplaying));
                    DurableTask.Once("added", () => 0);
                }

                DurableTask.Once("effect", () => 1);
                observations.Enqueue((run, "effect", context.IsReplaying));
                waiting[run - 1].TrySetResult();
                await DurableTask.Delay(TimeSpan.FromDays(1)).WithId("delay");
            }));

        runtime.ScheduleFromInbox(TaskId.Parse("root"), request);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await waiting[0].Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => storage.Contains(TaskId.Parse("root/delay")));
        await runtime.StopAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var (replacement, _, _, _) = CreateRuntime(storage, manager);
        await replacement.ResumePendingTasksAsync(TestContext.Current.CancellationToken);
        await waiting[1].Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal([(1, "effect", false), (2, "start", true), (2, "effect", false)], observations);
        await replacement.StopAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    });

    [Fact]
    public Task ReplayWhichReachesARecordedOperationAgainKeepsReplaying() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var detachedRequest = CreateRemoteRequest(1);
        var observations = new ConcurrentQueue<(int Run, string Point, bool Replaying)>();
        TaskCompletionSource[] waiting = [new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously)];
        var runs = 0;
        var request = CreateRequest(
            1,
            () => DurableTask.Run(async _ =>
            {
                var run = Interlocked.Increment(ref runs);
                var context = DurableTask.Current;

                // The second detach of the same task reaches the marker which the first one reached.
                await new TestStateManager.TestRemoteDurableTask(detachedRequest).DetachAsync("detached", CancellationToken.None);
                await new TestStateManager.TestRemoteDurableTask(detachedRequest).DetachAsync("detached", CancellationToken.None);
                observations.Enqueue((run, "detached", context.IsReplaying));
                DurableTask.Once("effect", () => 1);
                observations.Enqueue((run, "effect", context.IsReplaying));
                waiting[run - 1].TrySetResult();
                await DurableTask.Delay(TimeSpan.FromDays(1)).WithId("delay");
            }));

        runtime.ScheduleFromInbox(TaskId.Parse("root"), request);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await waiting[0].Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => storage.Contains(TaskId.Parse("root/delay")));
        await runtime.StopAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var (replacement, _, _, _) = CreateRuntime(storage, manager);
        await replacement.ResumePendingTasksAsync(TestContext.Current.CancellationToken);
        await waiting[1].Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal([(1, "detached", false), (1, "effect", false), (2, "detached", true), (2, "effect", true)], observations);
        await replacement.StopAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    });

    [Fact]
    public Task OnceOffTheActivationSchedulerFails() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, _, _) = CreateRuntime();
        var taskId = TaskId.Parse("root/local");
        var runs = 0;

        var handle = await runtime.ScheduleChildAsync(
            taskId,
            DurableTask.Run(async token =>
            {
                await Task.Delay(1, token).ConfigureAwait(false);
                return DurableTask.Once("effect", () => ++runs);
            }),
            TestContext.Current.CancellationToken);
        var response = await handle.WaitAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.IsType<InvalidOperationException>(response.Exception);
        Assert.Equal(0, runs);
        Assert.False(storage.Contains(TaskId.Parse("root/local/$once:effect")));
    });

    [Fact]
    public Task OnceUnderAnIdentifierRecordedForAnotherOperationDiverges() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, _, _) = CreateRuntime();
        var taskId = TaskId.Parse("root/local");
        var effectId = TaskId.Parse("root/local/$once:effect");
        storage.SetRequestFingerprint(effectId, storage.GetOrCreate(effectId), "$detach:other");
        var runs = 0;

        var handle = await runtime.ScheduleChildAsync(
            taskId,
            DurableTask.Run(_ => DurableTask.Once("effect", () => ++runs)),
            TestContext.Current.CancellationToken);
        var response = await handle.WaitAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var exception = Assert.IsType<InvalidOperationException>(response.Exception);
        Assert.Contains("a DurableTask.Once effect", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, runs);
    });

    [Fact]
    public Task LocalStepAwaitingADelayCommitsItsRecordWithTheDelay() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, transport) = CreateRuntime();
        var taskId = TaskId.Parse("root/wait");
        manager.BeforeWrite = () =>
        {
            // The step's record, its delay child, and the delay's resume message commit together.
            Assert.True(storage.Contains(taskId));
            Assert.Single(transport.Resumes);
        };

        _ = await runtime.ScheduleChildAsync(
            taskId,
            DurableTask.Run(async _ => await DurableTask.Delay(TimeSpan.FromDays(1))),
            TestContext.Current.CancellationToken);
        await transport.ResumeSent.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => manager.WriteCount == 1);

        Assert.Equal(1, manager.WriteCount);
        await runtime.StopAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    });

    [Fact]
    public Task LocalChildHandleIsTheHandleWhichPollingReturns() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, _, _, _) = CreateRuntime();
        var taskId = TaskId.Parse("root/local");

        var scheduledHandle = await runtime.ScheduleChildAsync(taskId, new TestStateManager.PendingDurableTask(), TestContext.Current.CancellationToken);

        Assert.Same(runtime.GetScheduledTaskHandle(taskId), scheduledHandle);
    });

    [Fact]
    public Task FailedLocalStepWriteFencesTheGrainWithoutCompletingTheStep() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, _, manager, _) = CreateRuntime();
        var invoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var taskId = TaskId.Parse("root/local");
        manager.BeforeWrite = () => throw new InvalidOperationException("Expected write failure.");

        var handle = await runtime.ScheduleChildAsync(taskId, DurableTask.Run(_ => invoked.TrySetResult()), TestContext.Current.CancellationToken);
        await invoked.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => manager.IsFenced);

        // Nothing the step did was recorded, so the next activation replays the step and runs it again.
        Assert.Equal(0, manager.WriteCount);
        Assert.False((await handle.PollAsync(new PollingOptions { PollTimeout = TimeSpan.Zero }, TestContext.Current.CancellationToken)).IsCompleted);
    });

    [Fact]
    public Task LocalStepPreparationFailureRetainsItsResultForTheNextWrite() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var taskId = TaskId.Parse("root/local-retry");
        var invocations = 0;
        manager.BeforePrepare = () =>
        {
            manager.BeforePrepare = null;
            throw new IOException("Expected initial preparation failure.");
        };

        var handle = await runtime.ScheduleChildAsync(
            taskId,
            DurableTask.Run(_ =>
            {
                Interlocked.Increment(ref invocations);
                return 42;
            }),
            TestContext.Current.CancellationToken);

        // The failed write retained the staged result, and a background write commits it and completes the step.
        var response = await handle.WaitAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(42, response.GetResult<int>());
        Assert.Equal(1, invocations);
        Assert.Equal(1, manager.WriteCount);
        Assert.Equal(42, storage.Get(taskId).Result!.GetResult<int>());
    });
    [Fact]
    public Task RootSchedulingPreparationFailureRetainsRequestAndRetryInvokesOnce() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var taskId = TaskId.Parse("root/retry");
        var invoked = 0;
        var request = CreateRequest(
            1,
            () => DurableTask.Run(_ => Interlocked.Increment(ref invoked)));
        manager.BeforePrepare = () =>
        {
            manager.BeforePrepare = null;
            throw new IOException("Expected initial preparation failure.");
        };

        await Assert.ThrowsAsync<JournaledStatePreCommitException>(
            () => ((IDurableTaskServer)runtime).ScheduleAsync(
                taskId,
                request,
                TestContext.Current.CancellationToken).AsTask());

        Assert.True(storage.Contains(taskId));
        Assert.Equal(0, invoked);

        _ = await ((IDurableTaskServer)runtime).ScheduleAsync(
            taskId,
            request,
            TestContext.Current.CancellationToken);
        var response = await runtime.GetScheduledTaskHandle(taskId)
            .WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, invoked);
        Assert.Equal(1, response.GetResult<int>());
        Assert.Equal(2, manager.WriteCount);
    });

    [Fact]
    public Task RetentionDeletesEachExpiredInvokedTaskWithItsExecutionTree() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, _) = CreateRuntime(TimeSpan.Zero);
        var rootId = TaskId.Parse("root");
        var childId = TaskId.Parse("root/child");
        var grandchildId = TaskId.Parse("root/child/grandchild");
        var triggerId = TaskId.Parse("trigger");
        var root = storage.GetOrCreate(rootId);
        storage.SetResponse(rootId, root, DurableTaskResponse.Completed);
        root.RequestFingerprint = "root";
        var child = storage.GetOrCreate(childId);
        storage.SetResponse(childId, child, DurableTaskResponse.Completed);
        child.RequestFingerprint = "child";
        var grandchild = storage.GetOrCreate(grandchildId);
        storage.SetResponse(grandchildId, grandchild, DurableTaskResponse.Completed);
        var observedDeletionWrite = false;
        manager.BeforeWrite = () => observedDeletionWrite |=
            !storage.Contains(rootId) && !storage.Contains(childId) && !storage.Contains(grandchildId);

        var handle = await runtime.ScheduleChildAsync(triggerId, DurableTask.Run(_ => { }), TestContext.Current.CancellationToken);
        await handle.WaitAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => Volatile.Read(ref observedDeletionWrite));

        // The invoked child owns its own tree, which holds the grandchild, and each tree is deleted once it expires.
        Assert.False(storage.Contains(rootId));
        Assert.False(storage.Contains(childId));
        Assert.False(storage.Contains(grandchildId));
    });

    [Fact]
    public Task DeliveredCompletionDeletesTaskWithItsExecutionTreeInTheRetirementWrite() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, transport) = CreateRuntime(TimeSpan.FromDays(1));
        var taskId = TaskId.Parse("root");
        var childId = TaskId.Parse("root/child");
        var loserId = TaskId.Parse("root/$when-any-1/0");
        var decisionId = TaskId.Parse("root/$when-any-1/$winner");
        var nestedId = TaskId.Parse("root/nested-invocation");
        var target = GrainId.Create("target", "one");
        var request = CreateRequest(1);
        var caller = request.Context!.CallerId;
        runtime.ScheduleFromInbox(taskId, request);
        storage.SetResponse(childId, storage.GetOrCreate(childId), DurableTaskResponse.Completed);
        storage.SetRemoteRequest(loserId, storage.GetOrCreate(loserId), target, "loser");
        var decision = storage.GetOrCreate(decisionId);
        decision.RequestFingerprint = "$completion-decision:winner";
        storage.SetResponse(decisionId, decision, DurableTaskResponse.FromResult(childId));
        storage.GetOrCreate(nestedId).RequestFingerprint = "nested";
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => transport.Completions.Count == 1 && manager.WriteCount >= 2);
        await WaitUntilIdleAsync(runtime);
        var completion = Assert.Single(transport.Completions);
        Assert.Equal(caller, completion.Target);
        Assert.Equal(0, completion.Materialize(runtime).Response.GetResult<int>());
        Assert.True(storage.LogicalTimes.ContainsKey(taskId));
        var writes = manager.WriteCount;
        var enumerations = storage.TasksEnumerationCount;

        // The outbox stages the completion's removal and calls the source in the same synchronous block.
        ((IOutboxMessageSource)runtime).OnRetired(completion.MessageId, OutboxMessageRetirement.Delivered);

        Assert.False(storage.Contains(taskId));
        Assert.False(storage.Contains(childId));
        Assert.False(storage.Contains(loserId));
        Assert.False(storage.Contains(decisionId));
        Assert.False(storage.LogicalTimes.ContainsKey(taskId));
        Assert.False(((IOutboxMessageSource)runtime).TryGetMessage(completion.MessageId, out _));
        Assert.Equal((runtime.GrainId, target, loserId), Assert.Single(transport.Cancellations));
        Assert.Equal(writes, manager.WriteCount);

        // An invocation nested under the task's identifier keeps its own lifecycle.
        Assert.True(storage.Contains(nestedId));

        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(writes + 1, manager.WriteCount);

        // Deletion follows the tree rather than scanning every task.
        Assert.Equal(enumerations, storage.TasksEnumerationCount);

        // A late completion of the canceled remote child is accepted without changing state.
        Accept(CreateHandler(runtime), CompletionMessage(target, runtime.GrainId, loserId, DurableTaskResponse.FromResult(5)));
        Assert.False(storage.Contains(loserId));
    });

    [Fact]
    public Task DeadLetteredCompletionRetainsResultForRetentionPolicy() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, transport) = CreateRuntime(TimeSpan.FromDays(1));
        var taskId = TaskId.Parse("root");
        var request = CreateRequest(1);
        runtime.ScheduleFromInbox(taskId, request);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => transport.Completions.Count == 1 && manager.WriteCount >= 2);
        var completion = Assert.Single(transport.Completions);

        ((IOutboxMessageSource)runtime).OnRetired(completion.MessageId, OutboxMessageRetirement.DeadLettered);

        var retained = storage.Get(taskId);
        Assert.Equal(0, retained.Result!.GetResult<int>());
        Assert.Empty(retained.CompletionDestinations);
        Assert.Equal(0, (await runtime.SubscribeOrPollAsync(taskId, new SubscribeOrPollOptions(), TestContext.Current.CancellationToken)).GetResult<int>());

        // A re-invocation after the completion was dead-lettered sends the retained result again.
        Assert.Equal(0, runtime.ScheduleFromInbox(taskId, request).GetResult<int>());
        Assert.Equal(2, transport.Completions.Count);
        Assert.Equal(completion.MessageId, transport.Completions[1].MessageId);
        Assert.True(((IOutboxMessageSource)runtime).TryGetMessage(completion.MessageId, out _));
    });

    [Fact]
    public Task LateDeadLetteredCompletionRetainsResultForRetentionPeriodAfterDeadLetter() => ActivationScheduler.RunAsync(async () =>
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var retention = TimeSpan.FromHours(1);
        var (runtime, storage, manager, transport) = CreateRuntime(retention, timeProvider: clock);
        var taskId = TaskId.Parse("root");
        var childId = TaskId.Parse("root/child");
        runtime.ScheduleFromInbox(taskId, CreateRequest(1));
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => transport.Completions.Count == 1 && manager.WriteCount >= 2);
        await WaitUntilIdleAsync(runtime);
        storage.SetResponse(childId, storage.GetOrCreate(childId), DurableTaskResponse.Completed);
        var completion = Assert.Single(transport.Completions);

        // The outbox dead-letters the completion after OutboxRetryTimeout, which exceeds the retention period.
        clock.Advance(TimeSpan.FromDays(1));
        ((IOutboxMessageSource)runtime).OnRetired(completion.MessageId, OutboxMessageRetirement.DeadLettered);
        Assert.Equal(clock.GetUtcNow(), storage.Get(taskId).RetainedSince);

        clock.Advance(retention - TimeSpan.FromTicks(1));
        Assert.False(InvokeDeleteExpiredTasks(runtime));
        Assert.True(storage.Contains(taskId));
        Assert.Equal(0, (await runtime.SubscribeOrPollAsync(taskId, new SubscribeOrPollOptions(), TestContext.Current.CancellationToken)).GetResult<int>());

        clock.Advance(TimeSpan.FromTicks(1));
        Assert.True(InvokeDeleteExpiredTasks(runtime));
        Assert.False(storage.Contains(taskId));
        Assert.False(storage.Contains(childId));
        await Assert.ThrowsAsync<DurableTaskNotFoundException>(
            async () => await runtime.SubscribeOrPollAsync(taskId, new SubscribeOrPollOptions(), TestContext.Current.CancellationToken));
    });

    [Fact]
    public Task ClientScheduledTaskIsDeletedWithItsExecutionTreeOnceItsRetentionEnds() => ActivationScheduler.RunAsync(async () =>
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var retention = TimeSpan.FromHours(1);
        var (runtime, storage, _, _) = CreateRuntime(retention, timeProvider: clock);
        var taskId = TaskId.Parse("client-root");
        var childId = TaskId.Parse("client-root/child");
        var request = CreateRequest(1);
        await ((IDurableTaskServer)runtime).ScheduleAsync(taskId, request, TestContext.Current.CancellationToken);
        Assert.Equal(0, (await runtime.GetScheduledTaskHandle(taskId).WaitAsync(TestContext.Current.CancellationToken)).GetResult<int>());
        await WaitUntilIdleAsync(runtime);
        storage.SetResponse(childId, storage.GetOrCreate(childId), DurableTaskResponse.Completed);

        clock.Advance(retention - TimeSpan.FromTicks(1));
        Assert.False(InvokeDeleteExpiredTasks(runtime));
        Assert.Equal(0, (await runtime.SubscribeOrPollAsync(taskId, new SubscribeOrPollOptions(), TestContext.Current.CancellationToken)).GetResult<int>());

        clock.Advance(TimeSpan.FromTicks(1));
        Assert.True(InvokeDeleteExpiredTasks(runtime));
        Assert.False(storage.Contains(taskId));
        Assert.False(storage.Contains(childId));
        Assert.False(storage.LogicalTimes.ContainsKey(taskId));
        var failure = await Assert.ThrowsAsync<DurableTaskNotFoundException>(
            async () => await runtime.SubscribeOrPollAsync(taskId, new SubscribeOrPollOptions(), TestContext.Current.CancellationToken));
        Assert.Equal(taskId, failure.TaskId);

        // The task identifier deduplicates schedules only while the result is retained.
        var again = CreateRequest(1);
        await ((IDurableTaskServer)runtime).ScheduleAsync(taskId, again, TestContext.Current.CancellationToken);
        Assert.Equal(0, (await runtime.GetScheduledTaskHandle(taskId).WaitAsync(TestContext.Current.CancellationToken)).GetResult<int>());
        Assert.Equal(1, request.CreateTaskCallCount);
        Assert.Equal(1, again.CreateTaskCallCount);
    });

    [Fact]
    public Task ScheduleOfAnIdleGrainObservesTheEndOfRetention() => ActivationScheduler.RunAsync(async () =>
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var retention = TimeSpan.FromHours(1);
        var (runtime, storage, _, _) = CreateRuntime(retention, timeProvider: clock);
        var polledId = TaskId.Parse("polled-root");
        var scheduledId = TaskId.Parse("scheduled-root");
        var polled = CreateRequest(1);
        var scheduled = CreateRequest(2);
        await ((IDurableTaskServer)runtime).ScheduleAsync(polledId, polled, TestContext.Current.CancellationToken);
        await ((IDurableTaskServer)runtime).ScheduleAsync(scheduledId, scheduled, TestContext.Current.CancellationToken);
        Assert.Equal(0, (await runtime.GetScheduledTaskHandle(polledId).WaitAsync(TestContext.Current.CancellationToken)).GetResult<int>());
        Assert.Equal(0, (await runtime.GetScheduledTaskHandle(scheduledId).WaitAsync(TestContext.Current.CancellationToken)).GetResult<int>());
        await WaitUntilIdleAsync(runtime);

        // No write or activation follows the end of retention, so the schedule applies it itself and runs the request
        // again, and a later poll observes the deletion.
        clock.Advance(retention);
        var again = CreateRequest(2);
        await ((IDurableTaskServer)runtime).ScheduleAsync(scheduledId, again, TestContext.Current.CancellationToken);
        Assert.Equal(0, (await runtime.GetScheduledTaskHandle(scheduledId).WaitAsync(TestContext.Current.CancellationToken)).GetResult<int>());
        Assert.Equal((1, 1), (scheduled.CreateTaskCallCount, again.CreateTaskCallCount));
        Assert.False(storage.Contains(polledId));

        var failure = await Assert.ThrowsAsync<DurableTaskNotFoundException>(
            async () => await runtime.SubscribeOrPollAsync(polledId, new SubscribeOrPollOptions(), TestContext.Current.CancellationToken));
        Assert.Equal(polledId, failure.TaskId);
    });

    [Fact]
    public Task PollOfAnIdleGrainObservesTheEndOfRetention() => ActivationScheduler.RunAsync(async () =>
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var retention = TimeSpan.FromHours(1);
        var (runtime, storage, _, _) = CreateRuntime(retention, timeProvider: clock);
        var taskId = TaskId.Parse("polled-root");
        await ((IDurableTaskServer)runtime).ScheduleAsync(taskId, CreateRequest(1), TestContext.Current.CancellationToken);
        Assert.Equal(0, (await runtime.GetScheduledTaskHandle(taskId).WaitAsync(TestContext.Current.CancellationToken)).GetResult<int>());
        await WaitUntilIdleAsync(runtime);

        clock.Advance(retention);
        var failure = await Assert.ThrowsAsync<DurableTaskNotFoundException>(
            async () => await runtime.SubscribeOrPollAsync(taskId, new SubscribeOrPollOptions(), TestContext.Current.CancellationToken));
        Assert.Equal(taskId, failure.TaskId);
        Assert.False(storage.Contains(taskId));
    });

    [Fact]
    public Task InvokedTaskNestedUnderAnotherTaskKeepsItsOwnLifecycle() => ActivationScheduler.RunAsync(async () =>
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var retention = TimeSpan.FromHours(1);
        var (runtime, storage, manager, transport) = CreateRuntime(retention, timeProvider: clock);
        var outerId = TaskId.Parse("outer");
        var localId = TaskId.Parse("outer/local");
        var nestedId = TaskId.Parse("outer/nested");
        var nestedChildId = TaskId.Parse("outer/nested/child");
        var outer = storage.GetOrCreate(outerId);
        outer.RequestFingerprint = "outer";
        storage.SetResponse(outerId, outer, DurableTaskResponse.Completed);
        storage.SetResponse(localId, storage.GetOrCreate(localId), DurableTaskResponse.Completed);

        // Task identifiers are chosen by callers, so another grain can invoke a task under the outer task's identifier.
        runtime.ScheduleFromInbox(nestedId, CreateRequest(1));
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => transport.Completions.Count == 1);
        await WaitUntilIdleAsync(runtime);
        storage.SetResponse(nestedChildId, storage.GetOrCreate(nestedChildId), DurableTaskResponse.Completed);

        clock.Advance(retention);
        Assert.True(InvokeDeleteExpiredTasks(runtime));
        Assert.False(storage.Contains(outerId));
        Assert.False(storage.Contains(localId));
        Assert.True(storage.Contains(nestedId));
        Assert.True(storage.Contains(nestedChildId));

        ((IOutboxMessageSource)runtime).OnRetired(Assert.Single(transport.Completions).MessageId, OutboxMessageRetirement.Delivered);

        Assert.False(storage.Contains(nestedId));
        Assert.False(storage.Contains(nestedChildId));
    });

    [Fact]
    public Task DeletingTreeCancelsRunningLocalChildAndDiscardsItsResult() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, transport) = CreateRuntime(TimeSpan.FromDays(1));
        var rootId = TaskId.Parse("root");
        var childId = TaskId.Parse("root/child");
        runtime.ScheduleFromInbox(rootId, CreateRequest(1));
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => transport.Completions.Count == 1);
        await WaitUntilIdleAsync(runtime);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var childHandle = await runtime.ScheduleChildAsync(childId, DurableTask.Run(async cancellationToken =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // A deleted attempt can't schedule durable work, which would outlive its tree.
                    canceled.TrySetResult(await Record.ExceptionAsync(async () => await DurableTask.Delay(TimeSpan.FromDays(1))));
                    throw new InvalidOperationException("The deleted attempt fails after cancellation.");
                }
            }), TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        ((IOutboxMessageSource)runtime).OnRetired(Assert.Single(transport.Completions).MessageId, OutboxMessageRetirement.Delivered);

        Assert.False(storage.Contains(rootId));
        Assert.False(storage.Contains(childId));
        Assert.Equal(DurableTaskStatus.Canceled, (await childHandle.WaitAsync(TestContext.Current.CancellationToken)).Status);
        Assert.False(canceled.Task.IsCompleted);

        // The running attempt is signaled once the deletion commits.
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.IsAssignableFrom<OperationCanceledException>(await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Empty(storage.Tasks);
        var writes = manager.WriteCount;

        // Deactivation drains the attempt, whose result is discarded without restoring state.
        await runtime.StopAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(storage.Contains(childId));
        Assert.Equal(writes, manager.WriteCount);
        Assert.Empty(transport.Cancellations);
    });

    [Fact]
    public Task LateCancellationRecordIsDeletedOnceTheOutboxRetryTimeoutEnds() => ActivationScheduler.RunAsync(async () =>
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var retryAge = TimeSpan.FromHours(2);
        var (runtime, storage, manager, _) = CreateRuntime(
            TimeSpan.FromHours(1),
            timeProvider: clock,
            outboxRetryTimeout: retryAge);
        var taskId = TaskId.Parse("root");
        var caller = GrainId.Create("caller", "one");

        // A cancellation for a task which is unknown, for example because it was deleted, records the cancellation in
        // case its invocation is still in flight.
        Accept(CreateHandler(runtime), CancellationMessage(caller, runtime.GrainId, taskId));
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        var record = storage.Get(taskId);
        Assert.NotNull(record.CancellationRequestedAt);
        Assert.Null(record.Result);

        clock.Advance(retryAge - TimeSpan.FromTicks(1));
        Assert.False(InvokeDeleteExpiredTasks(runtime));
        Assert.True(storage.Contains(taskId));

        clock.Advance(TimeSpan.FromTicks(1));
        Assert.True(InvokeDeleteExpiredTasks(runtime));
        Assert.False(storage.Contains(taskId));
    });

    private static bool InvokeDeleteExpiredTasks(DurableTaskGrainRuntime runtime) =>
        (bool)typeof(DurableTaskGrainRuntime)
            .GetMethod("DeleteExpiredTasks", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(runtime, null)!;

    private static Task WaitUntilIdleAsync(DurableTaskGrainRuntime runtime) =>
        WaitUntilAsync(() => !((IDurableTaskGrainExtension)runtime)
            .GetRunningTasksAsync(CancellationToken.None)
            .ToBlockingEnumerable()
            .Any());

    [Fact]
    public Task CompletionPruningIsPersistedAfterHandleCompletion() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, _) = CreateRuntime(TimeSpan.Zero);
        var taskId = TaskId.Parse("root/local");
        var observedPrunedWrite = false;
        manager.BeforeWrite = () =>
        {
            // The first write commits the step with its result; the next deletes it.
            if (manager.WriteCount >= 1)
            {
                observedPrunedWrite = !storage.Contains(taskId);
            }
        };

        var handle = await runtime.ScheduleChildAsync(taskId, DurableTask.Run(_ => { }), TestContext.Current.CancellationToken);
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, (await handle.WaitAsync(TestContext.Current.CancellationToken)).Status);
        await WaitUntilAsync(() => manager.WriteCount >= 2);

        Assert.True(observedPrunedWrite);
        Assert.False(storage.Contains(taskId));
    });

    [Fact]
    public Task ScheduleChildAsync_ConcurrentAncestorCancellation_DoesNotPersistOrStartChild() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, transport) = CreateRuntime();
        var rootId = TaskId.Parse("root");
        var childId = TaskId.Parse("root/remote-child");
        storage.GetOrCreate(rootId);
        var rootHandle = runtime.GetScheduledTaskHandle(rootId);
        var receiver = GrainId.Create("test", "one");
        var caller = GrainId.Create("caller", "one");
        var handler = CreateHandler(runtime);
        var cancellationWriteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCancellationWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.AfterWriteStarted = async () =>
        {
            manager.AfterWriteStarted = null;
            cancellationWriteEntered.TrySetResult();
            await releaseCancellationWrite.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        };

        Accept(handler, CancellationMessage(caller, receiver, rootId));
        var cancellationWrite = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        await cancellationWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var remoteRequest = CreateRemoteRequest(17);
        var childTask = new TestStateManager.TestRemoteDurableTask(remoteRequest);

        IScheduledTaskHandle handle;
        try
        {
            // The staged cancellation is visible before it commits, and any effect derived from it commits after it.
            handle = await runtime.ScheduleChildAsync(
                childId,
                childTask,
                TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.False(cancellationWrite.IsCompleted);
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
        await WaitUntilAsync(() => manager.WriteCount == 2);
    });

    [Fact]
    public Task ScheduleChildAsync_CanceledAncestorPreservesCompletedChildResult() => ActivationScheduler.RunAsync(async () =>
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
    });

    [Fact]
    public Task ScheduleChildAsync_CanceledAncestorStillRejectsConflictingChildIdentity() => ActivationScheduler.RunAsync(async () =>
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

        // The error names the recorded and the requested operations, so a diverging replay is diagnosable.
        Assert.StartsWith(
            $"Durable child task '{childId}' is already associated with a different request. It records a call to grain '{originalRequest.Context!.TargetId}' (fingerprint ",
            exception.Message);
        Assert.Contains("and this attempt requested a call to grain", exception.Message, StringComparison.Ordinal);
        Assert.Empty(transport.Invocations);
    });

    [Fact]
    public Task ScheduleChildAsync_DivergingReplayNamesRecordedAndRequestedOperations() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, _, transport) = CreateRuntime();
        var childId = TaskId.Parse("root/step");
        var target = GrainId.Create("target", "one");
        storage.SetRemoteRequest(childId, storage.GetOrCreate(childId), target, "0123456789ABCDEF0123");

        // The workflow changed: a replay schedules a local step where the first execution recorded a grain call.
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.ScheduleChildAsync(
                childId,
                DurableTask.FromResult(1),
                TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(
            $"Durable child task '{childId}' is already associated with a different request. It records a call to grain '{target}' (fingerprint 0123456789AB), and this attempt requested a local child task. A replay schedules the same operation, with the same arguments, under each identifier; new workflow steps use new identifiers.",
            exception.Message);
        Assert.Empty(transport.Invocations);
    });

    [Fact]
    public Task ScheduleChildAsync_SpawnedRuntimeWriteCommitsAfterInFlightWrite() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, transport) = CreateRuntime();
        var childId = TaskId.Parse("root/custom-child");
        var nestedId = TaskId.Parse("nested/remote");
        var nestedRequest = CreateRemoteRequest(17);
        Task<DurableTaskResponse>? nestedScheduling = null;
        var startNestedScheduling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nestedSchedulingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
                async () =>
                {
                    await startNestedScheduling.Task;
                    nestedSchedulingStarted.TrySetResult();
                    return await runtime.ScheduleRemoteAsync(
                        nestedId,
                        nestedRequest,
                        TestContext.Current.CancellationToken);
                },
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
            startNestedScheduling.TrySetResult();
            await nestedSchedulingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await WaitUntilAsync(() => storage.Contains(nestedId));

            // Staging is shared, but the nested operation is acknowledged only by a write which follows the in-flight one.
            Assert.False(nestedScheduling.IsCompleted);
            Assert.Equal(0, manager.WriteCount);
        }
        finally
        {
            releaseWrite.TrySetResult();
        }

        await scheduling.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await nestedScheduling.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Single(transport.Invocations);
        Assert.Equal(2, manager.WriteCount);
    });

    [Fact]
    public Task ScheduleChildAsync_CustomSchedulerCanAwaitNestedRuntimeWriteAndReturnsItsHandle() => ActivationScheduler.RunAsync(async () =>
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
    });

    [Fact]
    public Task ScheduleChildAsync_CancellationDuringCustomSchedulingCancelsSchedulerOwnedHandle() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, _, _) = CreateRuntime();
        var childId = TaskId.Parse("root/custom-cancel");
        var schedulingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseScheduling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var schedulerHandle = Substitute.For<IScheduledTaskHandle>();
        schedulerHandle.TaskId.Returns(childId);
        schedulerHandle.CancelAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
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
        await schedulerHandle.Received(1).CancelAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>());
    });

    [Fact]
    public Task ScheduleChildAsync_CustomFinalizationPreparationFailureAllowsRetry() => ActivationScheduler.RunAsync(async () =>
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
        manager.BeforePrepare = () =>
        {
            manager.BeforePrepare = null;
            throw new IOException("Expected custom finalization preparation failure.");
        };

        var exception = await Assert.ThrowsAsync<JournaledStatePreCommitException>(
            () => runtime.ScheduleChildAsync(
                childId,
                task,
                TestContext.Current.CancellationToken).AsTask());

        // The child remains staged and the scheduler's operation remains valid for the retry.
        Assert.Equal("Expected custom finalization preparation failure.", exception.InnerException!.Message);
        Assert.True(storage.Contains(childId));
        await schedulerHandle.DidNotReceive().CancelAsync(Arg.Any<CancellationToken>());

        var retryHandle = await runtime.ScheduleChildAsync(
            childId,
            task,
            TestContext.Current.CancellationToken);

        Assert.Same(schedulerHandle, retryHandle);
        Assert.Equal(2, scheduleCount);
        Assert.Equal(1, manager.WriteCount);
    });

    [Fact]
    public Task ScheduleChildAsync_CanceledAncestorPreparationFailureRetainsCanceledChild() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var rootId = TaskId.Parse("root");
        var childId = TaskId.Parse("root/custom-preparation-failure");
        var root = storage.GetOrCreate(rootId);
        root.CancellationRequestedAt = DateTimeOffset.UtcNow;
        storage.GetOrCreate(childId);
        manager.BeforePrepare = () =>
        {
            manager.BeforePrepare = null;
            throw new IOException("Expected custom preparation failure.");
        };
        var task = new TestSchedulableTask((_, _) => throw new InvalidOperationException("The canceled task must not be scheduled."));

        await Assert.ThrowsAsync<JournaledStatePreCommitException>(
            () => runtime.ScheduleChildAsync(
                childId,
                task,
                TestContext.Current.CancellationToken).AsTask());
        Assert.NotNull(storage.Get(childId).CancellationRequestedAt);
        Assert.Same(DurableTaskResponse.Canceled, storage.Get(childId).Result);

        var handle = await runtime.ScheduleChildAsync(
            childId,
            task,
            TestContext.Current.CancellationToken);

        Assert.Same(DurableTaskResponse.Canceled, await handle.WaitAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, task.ScheduleAsyncCallCount);
        Assert.Equal(1, manager.WriteCount);
    });

    [Fact]
    public Task ScheduleChildAsync_CompletedCustomFinalizationPreparationFailureRetainsResult() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var childId = TaskId.Parse("root/custom-existing-finalization-failure");
        storage.GetOrCreate(childId);
        manager.BeforePrepare = () =>
        {
            manager.BeforePrepare = null;
            throw new IOException("Expected custom finalization preparation failure.");
        };
        var scheduleCount = 0;
        var task = new TestSchedulableTask((_, _) =>
        {
            scheduleCount++;
            return new(DurableTaskResponse.FromResult(42));
        });

        await Assert.ThrowsAsync<JournaledStatePreCommitException>(
            () => runtime.ScheduleChildAsync(
                childId,
                task,
                TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(42, storage.Get(childId).Result!.GetResult<int>());

        var handle = await runtime.ScheduleChildAsync(
            childId,
            task,
            TestContext.Current.CancellationToken);
        var response = await handle.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(42, response.GetResult<int>());
        Assert.Equal(1, scheduleCount);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, manager.WriteCount);
    });

    [Fact]
    public Task RemoteTaskIdReuseAfterDeletionSendsNewInvocation() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, transport) = CreateRuntime(TimeSpan.Zero);
        var taskId = TaskId.Parse("root/remote-reuse");
        var originalRequest = CreateRemoteRequest(17);
        var target = originalRequest.Context!.TargetId;
        var deletionWriteCompleted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.AfterWriteCompleted = writeCount =>
        {
            if (writeCount == 3)
            {
                deletionWriteCompleted.TrySetResult(writeCount);
            }
        };

        await runtime.ScheduleRemoteAsync(taskId, originalRequest, TestContext.Current.CancellationToken);
        runtime.AcceptResponseFromInbox(taskId, DurableTaskResponse.FromResult(42), target);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            3,
            await deletionWriteCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.False(storage.Contains(taskId));

        // A task without a stored ancestor is deleted when its retention ends, which frees its identifier.
        var replacementRequest = CreateRemoteRequest(18);
        var response = await runtime.ScheduleRemoteAsync(taskId, replacementRequest, TestContext.Current.CancellationToken);

        Assert.False(response.IsCompleted);
        Assert.Collection(
            transport.Invocations,
            invocation => Assert.Same(originalRequest, invocation.Request),
            invocation =>
            {
                Assert.Same(replacementRequest, invocation.Request);
                Assert.Equal(taskId, invocation.TaskId);
                Assert.Equal(target, invocation.Target);
            });
        Assert.Equal(
            IDurableTaskRequest.GetFingerprint(replacementRequest, CreateSerializer()),
            storage.Get(taskId).RemoteRequestFingerprint);
        Assert.Null(storage.Get(taskId).Result);
    });

    [Fact]
    public Task SubscribeOrPollAsync_MissingTaskThrowsNotFoundWithoutCreatingState() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, transport) = CreateRuntime();
        var taskId = TaskId.Parse("root/missing");

        var failure = await Assert.ThrowsAsync<DurableTaskNotFoundException>(
            () => runtime.SubscribeOrPollAsync(
                taskId,
                new SubscribeOrPollOptions { PollTimeout = TimeSpan.Zero },
                TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(taskId, failure.TaskId);
        Assert.False(storage.Contains(taskId));
        Assert.Equal(0, manager.WriteCount);
        Assert.Empty(transport.Invocations);
        Assert.Empty(transport.Resumes);
    });

    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    public Task TaskHandlePollAsync_PropagatesCallerCancellation(int pollTimeoutMilliseconds) => ActivationScheduler.RunAsync(async () =>
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
    });

    [Fact]
    public Task DetachFromExecutionCommitsInvocationWithItsMarkerAndReplayDoesNotResend() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, transport) = CreateRuntime();
        var starterId = TaskId.Parse("starter");
        var rootId = TaskId.CreateRoot("detached");
        var detachedRequest = CreateRemoteRequest(1);
        var markerId = starterId.Child($"$detach:{detachedRequest.Context!.TargetId}:detached");
        var writes = new ConcurrentQueue<(bool Marker, int Invocations)>();
        manager.AfterWriteCompleted = _ => writes.Enqueue((storage.Contains(markerId), transport.DetachedInvocations.Count));
        TaskCompletionSource[] detached = [new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously)];
        var attempts = 0;
        var request = CreateRequest(
            1,
            () => DurableTask.Run(async executionToken =>
            {
                var attempt = Interlocked.Increment(ref attempts) - 1;
                await new TestStateManager.TestRemoteDurableTask(detachedRequest).DetachAsync("detached", CancellationToken.None);
                detached[attempt].TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, executionToken);
            }));

        runtime.ScheduleFromInbox(starterId, request);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await detached[0].Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var invocation = Assert.Single(transport.DetachedInvocations);
        Assert.Equal((runtime.GrainId, detachedRequest.Context!.TargetId, rootId), (invocation.Sender, invocation.Target, invocation.TaskId));
        Assert.Empty(transport.Invocations);
        Assert.Equal(default, detachedRequest.Context.CallerId);
        Assert.False(detachedRequest.Context.SupportsDurableCompletion);

        // The invocation and its marker commit in one write; the starter stores no state for the detached task.
        Assert.Contains((true, 1), writes);
        Assert.DoesNotContain((false, 1), writes);
        var marker = storage.Get(markerId);
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, marker.Result!.Status);
        Assert.Null(marker.Request);
        Assert.True(marker.RemoteTarget.IsDefault);
        Assert.False(storage.Contains(rootId));

        // A replacement activation replays the starter, which reaches the marker without sending the invocation again.
        await runtime.StopAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var (replacement, _, _, replacementTransport) = CreateRuntime(storage, manager);
        await replacement.ResumePendingTasksAsync(TestContext.Current.CancellationToken);
        await detached[1].Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Empty(replacementTransport.DetachedInvocations);
        Assert.Empty(replacementTransport.Invocations);
        await replacement.StopAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    });

    [Fact]
    public Task DetachRecordsOneMarkerPerDetachedTask() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, transport) = CreateRuntime();
        var starterId = TaskId.Parse("starter");
        var detachedRequest = CreateRemoteRequest(1);
        var target = detachedRequest.Context!.TargetId;
        var request = CreateRequest(
            1,
            () => DurableTask.Run(async _ =>
            {
                // An unnamed child before the detach doesn't change the marker which the detach reaches.
                await DurableTask.Run(static _ => { });
                await new TestStateManager.TestRemoteDurableTask(detachedRequest).DetachAsync("detached", CancellationToken.None);
                await new TestStateManager.TestRemoteDurableTask(detachedRequest).DetachAsync("detached", CancellationToken.None);
                await new TestStateManager.TestRemoteDurableTask(detachedRequest).DetachAsync("other:root", CancellationToken.None);
            }));

        runtime.ScheduleFromInbox(starterId, request);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => storage.Get(starterId).Result is { IsCompleted: true });

        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, storage.Get(starterId).Result!.Status);
        Assert.Equal(
            [TaskId.CreateRoot("detached"), TaskId.CreateRoot("other:root")],
            transport.DetachedInvocations.Select(static invocation => invocation.TaskId));
        Assert.True(storage.Contains(starterId.Child($"$detach:{target}:detached")));
        Assert.True(storage.Contains(starterId.Child($"$detach:{target}:other\\:root")));
    });

    [Fact]
    public Task DetachIgnoresTheStartersDurableCancellation() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, transport) = CreateRuntime();
        var starterId = TaskId.Parse("starter");
        var detachedRequest = CreateRemoteRequest(1);
        var markerId = starterId.Child($"$detach:{detachedRequest.Context!.TargetId}:cleanup");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = CreateRequest(
            1,
            () => DurableTask.Run(async executionToken =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, executionToken);
                }
                catch (OperationCanceledException) when (DurableTask.Current.IsCancellationRequested)
                {
                    // Cleanup outlives the canceled starter.
                    await new TestStateManager.TestRemoteDurableTask(detachedRequest).DetachAsync("cleanup", CancellationToken.None);
                    throw;
                }
            }));
        runtime.ScheduleFromInbox(starterId, request);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await runtime.SignalCancellationAsync(starterId, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => storage.Get(starterId).Result is { IsCompleted: true });

        Assert.Equal(DurableTaskStatus.Canceled, storage.Get(starterId).Result!.Status);
        var invocation = Assert.Single(transport.DetachedInvocations);
        Assert.Equal(TaskId.CreateRoot("cleanup"), invocation.TaskId);
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, storage.Get(markerId).Result!.Status);
        Assert.Null(storage.Get(markerId).CancellationRequestedAt);

        // Deleting the starter's tree deletes the marker and sends nothing to the detached task.
        await WaitUntilIdleAsync(runtime);
        ((IOutboxMessageSource)runtime).OnRetired(Assert.Single(transport.Completions).MessageId, OutboxMessageRetirement.Delivered);
        Assert.False(storage.Contains(starterId));
        Assert.False(storage.Contains(markerId));
        Assert.Empty(transport.Cancellations);
    });

    [Fact]
    public Task SameGrainDetachStartsLocalRootWhichOutlivesTheStartersTree() => ActivationScheduler.RunAsync(async () =>
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var retention = TimeSpan.FromHours(1);
        var (runtime, storage, manager, transport) = CreateRuntime(retention, timeProvider: clock);
        var starterId = TaskId.Parse("starter");
        var rootId = TaskId.CreateRoot("detached");
        var detachedRequest = CreateRequest(2, () => DurableTask.FromResult(5));
        var markerId = starterId.Child($"$detach:{detachedRequest.Context!.TargetId}:detached");
        var writes = new ConcurrentQueue<(bool Marker, bool Root)>();
        manager.AfterWriteCompleted = _ => writes.Enqueue((storage.Contains(markerId), storage.Contains(rootId)));
        var request = CreateRequest(
            1,
            () => DurableTask.Run(async _ =>
                await new TestStateManager.TestRemoteDurableTask(detachedRequest).DetachAsync("detached", CancellationToken.None)));

        runtime.ScheduleFromInbox(starterId, request);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => storage.Contains(rootId) && storage.Get(rootId).Result is { IsCompleted: true });
        await WaitUntilAsync(() => transport.Completions.Count == 1);
        await WaitUntilIdleAsync(runtime);

        // The root and the marker commit together; the root has no caller and sends its result to no grain.
        Assert.All(writes, write => Assert.Equal(write.Marker, write.Root));
        var root = storage.Get(rootId);
        Assert.Equal(5, root.Result!.GetResult<int>());
        Assert.True(root.CallerId.IsDefault);
        Assert.Empty(root.CompletionDestinations);
        Assert.Equal(1, detachedRequest.CreateTaskCallCount);
        Assert.Empty(transport.Invocations);
        Assert.Empty(transport.DetachedInvocations);
        Assert.Equal(starterId, Assert.Single(transport.Completions).TaskId);

        // Delivering the starter's completion deletes its tree, which holds the marker but not the detached root.
        ((IOutboxMessageSource)runtime).OnRetired(Assert.Single(transport.Completions).MessageId, OutboxMessageRetirement.Delivered);
        Assert.False(storage.Contains(starterId));
        Assert.False(storage.Contains(markerId));
        Assert.Equal(5, (await runtime.SubscribeOrPollAsync(rootId, new SubscribeOrPollOptions(), TestContext.Current.CancellationToken)).GetResult<int>());

        // Like a client-scheduled root, the detached root is retained for the retention period after completion.
        clock.Advance(retention);
        Assert.True(InvokeDeleteExpiredTasks(runtime));
        Assert.False(storage.Contains(rootId));
    });

    [Fact]
    public Task DetachFromOrdinaryMethodCompletesWhenItsWriteCommits() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, transport) = CreateRuntime();
        var rootId = TaskId.CreateRoot("detached");
        var request = CreateRemoteRequest(1);
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.AfterWriteStarted = async () =>
        {
            writeStarted.TrySetResult();
            await releaseWrite.Task;
        };

        var detaching = runtime.DetachAsync(rootId, request, markerId: default, TestContext.Current.CancellationToken).AsTask();
        await writeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(detaching.IsCompleted);
        var invocation = Assert.Single(transport.DetachedInvocations);
        Assert.Equal((request.Context!.TargetId, rootId), (invocation.Target, invocation.TaskId));

        releaseWrite.SetResult();
        await detaching.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(1, manager.WriteCount);
        Assert.Empty(storage.Tasks);
        Assert.Empty(transport.Invocations);
    });

    [Fact]
    public Task RedetachingTheSameRootIsIdempotentAndConflictingReuseFails() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, _, _) = CreateRuntime();
        var rootId = TaskId.CreateRoot("detached");
        var first = CreateRequest(1);
        var again = CreateRequest(1);

        await runtime.DetachAsync(rootId, first, markerId: default, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => storage.Get(rootId).Result is { IsCompleted: true });
        await runtime.DetachAsync(rootId, again, markerId: default, TestContext.Current.CancellationToken);

        Assert.Equal(1, first.CreateTaskCallCount);
        Assert.Equal(0, again.CreateTaskCallCount);
        var exception = await Assert.ThrowsAsync<DurableTaskConflictException>(
            () => runtime.DetachAsync(rootId, CreateRequest(2), markerId: default, TestContext.Current.CancellationToken).AsTask());
        Assert.Contains("different request", exception.Message, StringComparison.Ordinal);
        Assert.Equal(rootId, exception.TaskId);
    });

    [Fact]
    public Task DetachMarkerReusedForAnotherStartFailsBeforeStaging() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, transport) = CreateRuntime();
        var markerId = TaskId.Parse("starter/$detach:target:first");
        await runtime.DetachAsync(TaskId.CreateRoot("first"), CreateRemoteRequest(1), markerId, TestContext.Current.CancellationToken);
        var writes = manager.WriteCount;

        // A replay reaching the marker with the same start commits nothing new.
        await runtime.DetachAsync(TaskId.CreateRoot("first"), CreateRemoteRequest(1), markerId, TestContext.Current.CancellationToken);
        Assert.Single(transport.DetachedInvocations);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.DetachAsync(TaskId.CreateRoot("second"), CreateRemoteRequest(1), markerId, TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("another operation", exception.Message, StringComparison.Ordinal);
        Assert.Single(transport.DetachedInvocations);
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, storage.Get(markerId).Result!.Status);
        Assert.Equal(writes + 1, manager.WriteCount);
    });

    [Fact]
    public Task DetachedInboxInvocationIsRetainedLikeAClientScheduledRoot() => ActivationScheduler.RunAsync(async () =>
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var retention = TimeSpan.FromHours(1);
        var (runtime, storage, manager, transport) = CreateRuntime(retention, timeProvider: clock);
        var sender = GrainId.Create("caller", "one");
        var rootId = TaskId.CreateRoot("detached");
        var request = CreateSerializableRequest(runtime.GrainId);

        Accept(
            CreateHandler(runtime),
            MessagingTestHelpers.CreateMessage(
                MessagingTestHelpers.MessageTypes.Invocation,
                sender,
                runtime.GrainId,
                rootId,
                new DurableTaskInvocationMessage { TaskId = rootId, Request = request, Sender = sender, Detached = true }));
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => storage.Get(rootId).Result is { IsCompleted: true });
        await WaitUntilIdleAsync(runtime);

        var state = storage.Get(rootId);
        Assert.True(state.CallerId.IsDefault);
        Assert.Empty(state.CompletionDestinations);
        Assert.Empty(transport.Completions);
        Assert.Equal(
            DurableTaskStatus.CompletedSuccessfully,
            (await runtime.SubscribeOrPollAsync(rootId, new SubscribeOrPollOptions(), TestContext.Current.CancellationToken)).Status);

        clock.Advance(retention);
        Assert.True(InvokeDeleteExpiredTasks(runtime));
        Assert.False(storage.Contains(rootId));
    });

    [Fact]
    public Task TaskManagerObservesAndCancelsTheGrainsTasksWithoutAGrainCall() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var tasks = (IDurableTaskManager)runtime;
        var taskId = TaskId.CreateRoot("workflow");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.ScheduleFromInbox(taskId, CreateRequest(1, () => DurableTask.Run(async executionToken =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, executionToken);
            return 1;
        })));
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var task = tasks.GetTask<int>(taskId);
        Assert.Equal(taskId, task.Id);
        Assert.Equal(
            DurableTaskStatus.Pending,
            await task.GetStatusAsync(new PollingOptions { PollTimeout = TimeSpan.Zero }, TestContext.Current.CancellationToken));

        await tasks.CancelAsync(taskId, TestContext.Current.CancellationToken);

        var response = await task.GetResponseAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(DurableTaskStatus.Canceled, response.Status);
        Assert.NotNull(storage.Get(taskId).CancellationRequestedAt);
        Assert.Equal(DurableTaskStatus.Canceled, (await tasks.GetTask(taskId).GetResponseAsync(TestContext.Current.CancellationToken)).Status);
        await Assert.ThrowsAsync<DurableTaskNotFoundException>(
            () => tasks.GetTask<int>(TaskId.CreateRoot("missing")).GetResponseAsync(
                new PollingOptions { PollTimeout = TimeSpan.Zero },
                TestContext.Current.CancellationToken));
    });

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

    private static DurableTaskMessageHandler CreateHandler(DurableTaskGrainRuntime runtime) =>
        new(runtime, MessagingTestHelpers.MessageTypes);

    private static void Accept(IInboxMessageSink sink, InboxMessage message)
    {
        Assert.True(sink.TryAccept(message, out var result));
        Assert.Equal(DeliveryStatus.Accepted, result.Status);
    }

    private static async Task HandleAsync(IInboxHandler handler, IInboxHandlerContext context)
    {
        Assert.True(handler.CanHandle(context));
        var handling = handler.HandleAsync(context, TestContext.Current.CancellationToken);

        // Handlers stage their changes synchronously; the inbox owns the batch write.
        Assert.True(handling.IsCompletedSuccessfully);
        await handling;
        context.Received(1).Complete();
        context.DidNotReceive().Fail(Arg.Any<string>());
    }

    private static InboxMessage InvocationMessage(
        GrainId sender,
        GrainId receiver,
        TaskId taskId,
        IDurableTaskRequest request,
        GrainId? messageIdSender = null) =>
        MessagingTestHelpers.CreateMessage(
            MessagingTestHelpers.MessageTypes.Invocation,
            messageIdSender ?? sender,
            receiver,
            taskId,
            new DurableTaskInvocationMessage { TaskId = taskId, Request = request, Sender = sender });

    private static InboxMessage CompletionMessage(
        GrainId sender,
        GrainId receiver,
        TaskId taskId,
        DurableTaskResponse response) =>
        MessagingTestHelpers.CreateMessage(
            MessagingTestHelpers.MessageTypes.Completion,
            sender,
            receiver,
            taskId,
            new DurableTaskCompletionMessage { TaskId = taskId, Response = response, Sender = sender });

    private static InboxMessage CancellationMessage(GrainId sender, GrainId receiver, TaskId taskId) =>
        MessagingTestHelpers.CreateMessage(
            MessagingTestHelpers.MessageTypes.Cancellation,
            sender,
            receiver,
            taskId,
            new DurableTaskCancellationMessage { TaskId = taskId, Sender = sender });
    private static ValueTask<TaskId> InvokeSelectCompletionCoreAsync(
        GrainDurableTaskContext context,
        TaskId decisionId,
        IReadOnlyList<TaskId> candidates,
        CancellationToken cancellationToken) =>
        (ValueTask<TaskId>)typeof(GrainDurableTaskContext)
            .GetMethod("SelectCompletionCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(context, [decisionId, candidates, cancellationToken])!;

    private static TaskId InvokeCreateChildTaskId(GrainDurableTaskContext context, string? name) =>
        (TaskId)typeof(GrainDurableTaskContext)
            .GetMethod("CreateChildTaskId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(context, [name])!;

    private static IInboxHandlerContext ResumeContext(
        DurableTaskGrainRuntime runtime,
        TaskId taskId,
        long generation,
        DateTimeOffset dueTime) =>
        MessagingTestHelpers.CreateHandlerContext(
            MessagingTestHelpers.CreateResumeMessage(runtime.GrainId, taskId, generation, dueTime));

    private static (
        DurableTaskGrainRuntime Runtime,
        TestStorage Storage,
        TestStateManager Manager,
        RecordingDurableTaskMessageTransport Transport) CreateRuntime(
            TimeSpan? resultRetentionPeriod = null,
            bool initialize = true,
            TimeProvider? timeProvider = null,
            TimeSpan? outboxRetryTimeout = null)
    {
        var manager = new TestStateManager();
        var storage = new TestStorage(manager, timeProvider);
        return CreateRuntime(
            storage,
            manager,
            resultRetentionPeriod,
            initialize,
            timeProvider,
            outboxRetryTimeout);
    }

    /// <summary>
    /// Creates a runtime bound to <paramref name="manager"/>. Reusing durable storage and the manager models a
    /// replacement activation: the new runtime replaces the previous one as the manager's journal hook.
    /// </summary>
    private static (
        DurableTaskGrainRuntime Runtime,
        TestStorage Storage,
        TestStateManager Manager,
        RecordingDurableTaskMessageTransport Transport) CreateRuntime(
            TestStorage storage,
            TestStateManager manager,
            TimeSpan? resultRetentionPeriod = null,
            bool initialize = true,
            TimeProvider? timeProvider = null,
            TimeSpan? outboxRetryTimeout = null)
    {
        var context = Substitute.For<IGrainContext>();
        context.GrainId.Returns(GrainId.Create("test", "one"));
        var accessor = Substitute.For<IGrainContextAccessor>();
        accessor.GrainContext.Returns(context);
        var shared = new DurableTaskGrainRuntimeShared(
            accessor,
            timeProvider ?? TimeProvider.System,
            NullLogger<DurableTaskGrainRuntime>.Instance,
            Options.Create(new DurableTaskOptions
            {
                ResultRetentionPeriod = resultRetentionPeriod ?? TimeSpan.FromHours(1),
            }),
            Options.Create(new DurableMessagingOptions
            {
                OutboxRetryTimeout = outboxRetryTimeout ?? TimeSpan.FromDays(1),
            }),
            CreateSerializer());
        var transport = new RecordingDurableTaskMessageTransport();
        var runtime = new DurableTaskGrainRuntime(
            storage,
            storage.LogicalTimes,
            shared,
            [transport]);
        if (initialize)
        {
            runtime.InitializeForActivation();
        }

        manager.RegisterStateMachine(DurableTaskGrainRuntime.StateMachineName, runtime);
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
            bool suppressCancellation,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DurableTaskResponse> ScheduleRemoteAsync(
            TaskId taskId,
            IDurableTaskRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask CancelRemoteAsync(
            TaskId taskId,
            GrainId target,
            string? reason,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask DetachAsync(
            TaskId rootId,
            IDurableTaskRequest request,
            TaskId markerId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<TaskId> SelectCompletionAsync(
            TaskId decisionId,
            IReadOnlyList<TaskId> candidates,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public IScheduledTaskHandle GetScheduledTaskHandle(TaskId taskId) => throw new NotSupportedException();

        public DateTimeOffset? GetCompletedAt(TaskId taskId) => null;

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
            bool suppressCancellation,
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
            string? reason,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask DetachAsync(
            TaskId rootId,
            IDurableTaskRequest request,
            TaskId markerId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public IScheduledTaskHandle GetScheduledTaskHandle(TaskId taskId) =>
            new SchedulerProbeHandle(taskId);

        public DateTimeOffset? GetCompletedAt(TaskId taskId) => null;

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

    private sealed class AwaitExistingChildTask(
        TaskId childId,
        Action<IScheduledTaskHandle>? handleCaptured = null) : DurableTask
    {
        protected override async ValueTask<DurableTaskResponse> RunAsync(DurableTaskContext context)
        {
            var handle = (IScheduledTaskHandle)typeof(DurableTaskContext)
                .GetMethod("GetChildTaskHandle", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(context, [childId])!;
            handleCaptured?.Invoke(handle);
            return await handle.WaitAsync(CancellationToken.None);
        }
    }

    private sealed class ThrowingAbortHandle(
        TaskId taskId,
        TaskCompletionSource waitStarted) : IScheduledTaskHandle
    {
        public TaskId TaskId { get; } = taskId;

        public ValueTask CancelAsync(CancellationToken cancellationToken) => default;

        public ValueTask<DurableTaskResponse> PollAsync(
            PollingOptions options,
            CancellationToken cancellationToken) => new(DurableTaskResponse.Pending);

        public async ValueTask<DurableTaskResponse> WaitAsync(CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(
                static () => throw new InvalidOperationException("Expected abort callback failure."));
            waitStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return DurableTaskResponse.Pending;
        }
    }

    private sealed class TestStorage(TestStateManager manager, TimeProvider? timeProvider = null) : IDurableTaskGrainStorage
    {
        private readonly Dictionary<TaskId, TestTaskState> _states = [];
        private readonly DurableTaskHierarchy _hierarchy = new();
        private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
        private long _lastCompletionSequence;

        public TestDurableDictionary<TaskId, DateTimeOffset> LogicalTimes { get; } = [];

        /// <summary>Gets how many times <see cref="Tasks"/> has been enumerated.</summary>
        public int TasksEnumerationCount { get; private set; }

        public IEnumerable<(TaskId Id, IDurableTaskState State)> Tasks
        {
            get
            {
                TasksEnumerationCount++;
                return _states.Select(entry => (entry.Key, (IDurableTaskState)entry.Value));
            }
        }

        public TestTaskState Get(TaskId id) => _states[id];
        public bool Contains(TaskId id) => _states.ContainsKey(id);
        public TestTaskState GetOrCreate(TaskId id) => (TestTaskState)GetOrCreateTask(id, null);

        public IEnumerable<(TaskId Id, IDurableTaskState State)> GetChildren(TaskId task) =>
            _hierarchy
                .GetNearestStoredDescendants(task, _states.ContainsKey)
                .Select(id => (id, (IDurableTaskState)_states[id]));

        public IDurableTaskState GetOrCreateTask(TaskId taskId, IDurableTaskRequest? request)
        {
            if (!_states.TryGetValue(taskId, out var state))
            {
                state = new TestTaskState
                {
                    Request = request,
                    CreatedAt = _timeProvider.GetUtcNow(),
                };
                _states.Add(taskId, state);
                _hierarchy.Add(taskId);
            }

            return state;
        }

        public void SetRequest(TaskId taskId, IDurableTaskState state, IDurableTaskRequest request) =>
            ((TestTaskState)state).Request = request;

        public void SetRequestFingerprint(TaskId taskId, IDurableTaskState state, string fingerprint) =>
            ((TestTaskState)state).RequestFingerprint = fingerprint;

        public void SetRemoteRequest(TaskId taskId, IDurableTaskState state, GrainId target, string fingerprint)
        {
            ((TestTaskState)state).RemoteTarget = target;
            ((TestTaskState)state).RemoteRequestFingerprint = fingerprint;
        }

        public void SetCallerId(TaskId taskId, IDurableTaskState state, GrainId callerId) =>
            ((TestTaskState)state).CallerId = callerId;

        public void SetResponse(TaskId taskId, IDurableTaskState state, DurableTaskResponse response)
        {
            ((TestTaskState)state).Result = response;
            ((TestTaskState)state).CompletedAt = _timeProvider.GetUtcNow();
            ((TestTaskState)state).CompletionSequence = ++_lastCompletionSequence;
            ((TestTaskState)state).DueTime = null;
            if (((TestTaskState)state).ResumeGeneration > 0)
            {
                ((TestTaskState)state).ResumeGeneration++;
            }
        }

        public void RequestCancellation(TaskId taskId, IDurableTaskState state, string? reason = null)
        {
            if (((TestTaskState)state).CancellationRequestedAt is null)
            {
                ((TestTaskState)state).CancellationRequestedAt = _timeProvider.GetUtcNow();
                ((TestTaskState)state).CancellationReason = reason;
            }
        }

        public void SuppressCancellation(TaskId taskId, IDurableTaskState state) =>
            ((TestTaskState)state).CancellationSuppressed = true;

        public void SetDelay(TaskId taskId, IDurableTaskState state, DateTimeOffset dueTime, long generation)
        {
            ((TestTaskState)state).DueTime = dueTime;
            ((TestTaskState)state).ResumeGeneration = generation;
        }

        public void AddCompletionDestination(TaskId taskId, IDurableTaskState state, GrainId destination) =>
            ((TestTaskState)state).CompletionDestinations.Add(destination);

        public void RemoveCompletionDestination(TaskId taskId, IDurableTaskState state, GrainId destination) =>
            ((TestTaskState)state).CompletionDestinations.Remove(destination);

        public void RetainResult(TaskId taskId, IDurableTaskState state) =>
            ((TestTaskState)state).RetainedSince = _timeProvider.GetUtcNow();

        public bool TryGetTask(TaskId taskId, [NotNullWhen(true)] out IDurableTaskState? state)
        {
            var found = _states.TryGetValue(taskId, out var value);
            state = value;
            return found;
        }

        public bool RemoveTask(TaskId taskId)
        {
            if (!_states.Remove(taskId))
            {
                return false;
            }

            _hierarchy.Remove(taskId, _states.ContainsKey);
            return true;
        }

        public void Clear()
        {
            _states.Clear();
            _hierarchy.Clear();
        }
        public ValueTask WriteAsync(CancellationToken cancellationToken) => manager.WriteStateAsync(cancellationToken);
        public ValueTask ReadAsync(CancellationToken cancellationToken) => default;
    }

    /// <summary>
    /// In-memory task state for <see cref="TestStorage"/>. It holds decoded requests and responses directly, so runtime
    /// tests can use requests which have no Orleans codec.
    /// </summary>
    private sealed class TestTaskState : IDurableTaskState
    {
        public DurableTaskResponse? Result { get; set; }
        public HashSet<GrainId> CompletionDestinations { get; set; } = [];
        public IDurableTaskRequest? Request { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public DateTimeOffset? CancellationRequestedAt { get; set; }
        public string? CancellationReason { get; set; }
        public bool CancellationSuppressed { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? DueTime { get; set; }
        public long ResumeGeneration { get; set; }
        public string? RequestFingerprint { get; set; }
        public DateTimeOffset? RetainedSince { get; set; }
        public GrainId RemoteTarget { get; set; }
        public string? RemoteRequestFingerprint { get; set; }
        public GrainId CallerId { get; set; }
        public long CompletionSequence { get; set; }
        IReadOnlySet<GrainId> IDurableTaskState.CompletionDestinations => CompletionDestinations;
    }

    private sealed class TestDurableDictionary<TKey, TValue> : Dictionary<TKey, TValue>, IDurableDictionary<TKey, TValue>
        where TKey : notnull;

    /// <summary>
    /// Models the journaled state manager's write protocol: preparation, capture, storage acknowledgement, and
    /// completion callbacks. A preparation failure retains staged changes; a storage failure fences the manager.
    /// </summary>
    private sealed class TestStateManager : IJournaledStateManager
    {
        private readonly Dictionary<string, IStateMachine> _stateMachines = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private Exception? _failure;
        public int WriteCount { get; private set; }
        public bool IsFenced => _failure is not null;

        /// <summary>Runs before state preparation; throwing models a preparation failure.</summary>
        public Action? BeforePrepare { get; set; }

        /// <summary>Runs after preparation and before capture; throwing models a storage failure.</summary>
        public Action? BeforeWrite { get; set; }

        /// <summary>Runs after capture and before storage acknowledgement; throwing models a storage failure.</summary>
        public Func<ValueTask>? AfterWriteStarted { get; set; }
        public Action<int>? AfterWriteCompleted { get; set; }
        public ValueTask InitializeAsync(CancellationToken cancellationToken) => default;

        public void RegisterStateMachine(string name, IStateMachine stateMachine) => _stateMachines[name] = stateMachine;

        public bool TryGetStateMachine(string name, [NotNullWhen(true)] out IStateMachine? stateMachine) =>
            _stateMachines.TryGetValue(name, out stateMachine);

        public async ValueTask WriteStateAsync(CancellationToken cancellationToken)
        {
            await _writeGate.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_failure is { } failure)
                {
                    throw new InvalidOperationException("Journaled state operations are fenced because an operation failed.", failure);
                }

                var stateMachines = _stateMachines.Values.ToArray();
                try
                {
                    BeforePrepare?.Invoke();
                    for (var prepared = false; !prepared;)
                    {
                        prepared = true;
                        foreach (var stateMachine in stateMachines)
                        {
                            var preparation = stateMachine.PrepareWriteAsync(cancellationToken);
                            if (!preparation.IsCompletedSuccessfully)
                            {
                                prepared = false;
                                await preparation;
                            }
                        }
                    }
                }
                catch (Exception exception)
                {
                    throw new JournaledStatePreCommitException(exception);
                }

                try
                {
                    BeforeWrite?.Invoke();
                    foreach (var stateMachine in stateMachines)
                    {
                        stateMachine.WritePendingEntries(default);
                    }

                    if (AfterWriteStarted is { } afterWriteStarted)
                    {
                        await afterWriteStarted();
                    }
                }
                catch (Exception exception)
                {
                    _failure = exception;
                    throw;
                }

                WriteCount++;
                foreach (var stateMachine in stateMachines)
                {
                    stateMachine.OnWriteCompleted();
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
            protected override ValueTask<DurableTaskResponse> RunAsync(DurableTaskContext context) =>
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
            protected override ValueTask<DurableTaskResponse> RunAsync(DurableTaskContext context) =>
                throw new NotSupportedException();
        }

        public ValueTask DeleteStateAsync(CancellationToken cancellationToken) => default;
    }
}
