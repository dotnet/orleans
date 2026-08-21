using System.Diagnostics.CodeAnalysis;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
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
        var request = CreateRequest(1);
        request.Context!.TargetId = GrainId.Create("target", "other");
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

        request.Context.TargetId = receiver;
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
        var (runtime, storage, _, _) = CreateRuntime();
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

        var request = CreateRequest(1);
        request.Context!.TargetId = receiver;
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
    public async Task PreCanceledInboxInvocationStagesOneCompletion()
    {
        var (runtime, _, _, transport) = CreateRuntime();
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
        var request = CreateRequest(1);
        request.Context!.TargetId = receiver;
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
        var request = CreateRequest(7);
        var target = request.Context!.TargetId;

        await runtime.ScheduleChildAsync(childId, new TestStateManager.TestRemoteDurableTask(request), default);
        var child = storage.Get(childId);
        var fingerprint = child.RemoteRequestFingerprint;
        Assert.Equal(target, child.RemoteTarget);
        Assert.NotNull(fingerprint);

        var (recovered, _, _, recoveredTransport) = CreateRuntime(storage, manager);
        await recovered.SignalCancellationAsync(rootId, default);

        var cancellation = Assert.Single(recoveredTransport.Cancellations);
        Assert.Equal(childId, cancellation.TaskId);
        Assert.Equal(target, cancellation.Target);
        Assert.Equal(fingerprint, storage.Get(childId).RemoteRequestFingerprint);
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
                new TestStateManager.TestRemoteDurableTask(CreateRequest(2)),
                default).AsTask());
        Assert.Contains("different request", remoteException.Message, StringComparison.Ordinal);

        var remoteTaskId = TaskId.Parse("remote-identity");
        await runtime.ScheduleChildAsync(
            remoteTaskId,
            new TestStateManager.TestRemoteDurableTask(CreateRequest(3)),
            default);
        var topLevelException = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.ScheduleFromInboxAsync(remoteTaskId, CreateRequest(4), default).AsTask());
        Assert.Contains("remote child request", topLevelException.Message, StringComparison.Ordinal);
        Assert.NotNull(storage.Get(remoteTaskId).RemoteRequestFingerprint);
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
    public async Task CancelingLocalChildSignalsActiveDurableContext()
    {
        var (runtime, storage, _, _) = CreateRuntime();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedCancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
                    observedCancellation.TrySetResult();
                    throw;
                }
            }),
            default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await handle.CancelAsync(default);

        await observedCancellation.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DurableTaskStatus.Canceled, storage.Get(taskId).Result!.Status);
    }

    [Fact]
    public async Task DurableCancellationPropagatesRemoteEffectsWithIndependentToken()
    {
        var (runtime, _, _, transport) = CreateRuntime();
        var remoteRequest = CreateRequest(2);
        var rootRequest = CreateRequest(
            1,
            () => new TestStateManager.TestRemoteDurableTask(remoteRequest));
        var rootId = TaskId.Parse("root");
        await ((IDurableTaskServer)runtime).ScheduleAsync(rootId, rootRequest);
        await WaitUntilAsync(() => transport.Invocations.Count > 0);

        await runtime.SignalCancellationAsync(rootId, default);

        Assert.Contains(
            transport.Cancellations,
            cancellation => cancellation.Target == remoteRequest.Context!.TargetId);
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
            () => context.SelectCompletionAsync(
                TaskId.Parse("other/decision"),
                [TaskId.Parse("root/child")],
                default).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => context.SelectCompletionAsync(
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

        var generated = context.CreateChildTaskId(null);
        var explicitNumeric = context.CreateChildTaskId("0");

        Assert.NotEqual(generated, explicitNumeric);
        Assert.StartsWith("root/$child-", generated.ToString(), StringComparison.Ordinal);
        Assert.Equal(TaskId.Parse("root/0"), explicitNumeric);
        Assert.Throws<ArgumentException>(() => context.CreateChildTaskId("$reserved"));
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
    public async Task CleanupRecursivelyPrunesDescendantsBeforeTombstoningParent()
    {
        var (runtime, storage, _, _) = CreateRuntime(TimeSpan.Zero);
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

        await runtime.AcknowledgeCompletionAsync(rootId, waiter, default);

        Assert.NotNull(storage.Get(rootId).TombstonedAt);
        Assert.NotNull(storage.Get(childId).TombstonedAt);
        Assert.False(storage.Contains(grandchildId));
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
                TargetId = GrainId.Create("target", "one"),
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

    private static DurableEnvelope CreateEnvelope<T>(
        GrainId sender,
        GrainId target,
        string route,
        T body,
        GrainId? replyTo = null)
    {
        var services = new ServiceCollection();
        services.AddSerializer(builder => builder.AddAssembly(typeof(DurableTaskRuntimeInvariantTests).Assembly));
        using var serviceProvider = services.BuildServiceProvider();
        var builder = new DurableEnvelopeBuilder(
                serviceProvider.GetRequiredService<SerializerSessionPool>(),
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
            TimeSpan? resultRetentionPeriod = null)
    {
        var manager = new TestStateManager();
        var storage = new TestStorage(manager);
        return CreateRuntime(storage, manager, resultRetentionPeriod);
    }

    private static (
        DurableTaskGrainRuntime Runtime,
        TestStorage Storage,
        TestStateManager Manager,
        RecordingDurableTaskMessageTransport Transport) CreateRuntime(
            TestStorage storage,
            TestStateManager manager,
            TimeSpan? resultRetentionPeriod = null)
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
            foreach (var observer in _observers)
            {
                await observer.OnWritePreparingAsync(cancellationToken);
                observer.OnWriteStarted();
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
            public DurableTaskRequestContext? Context => request.Context;
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
            public ValueTask<DurableTaskResponse> ScheduleAsync(TaskId taskId, CancellationToken cancellationToken) =>
                new(DurableTaskResponse.Pending);
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
