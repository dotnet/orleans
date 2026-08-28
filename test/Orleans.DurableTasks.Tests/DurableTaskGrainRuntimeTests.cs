#nullable enable
using System;
using System.Collections.Generic;
using System.Distributed.DurableTasks;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Orleans.Runtime;
using Orleans.Runtime.DurableTasks;
using Orleans.Serialization;
using Xunit;

namespace Orleans.DurableTasks.Tests;

/// <summary>
/// Tests for <see cref="DurableTaskGrainRuntime"/> (and, indirectly, its private nested <c>TaskHandle</c> and
/// <c>CompletedTaskHandle</c> classes, which are only reachable through the outer class's public/internal API).
/// </summary>
[TestCategory("BVT")]
public class DurableTaskGrainRuntimeTests
{
    private sealed class Fixture
    {
        public required DurableTaskGrainRuntime Runtime { get; init; }
        public required VolatileDurableTaskGrainStorage Storage { get; init; }
        public required RecordingDurableTaskMessageTransport? Transport { get; init; }
        public required FakeTimeProvider TimeProvider { get; init; }
        public required GrainId GrainId { get; init; }
        public required DurableTaskGrainRuntimeShared Shared { get; init; }
    }

    private static Fixture CreateFixture(bool withTransport = false, VolatileDurableTaskGrainStorage? storage = null, FakeTimeProvider? timeProvider = null, GrainId grainId = default)
    {
        if (grainId.IsDefault)
        {
            grainId = GrainId.Create("test-grain", Guid.NewGuid().ToString());
        }

        var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        timeProvider ??= new FakeTimeProvider();
        storage ??= new VolatileDurableTaskGrainStorage(
            services.GetRequiredService<DeepCopier<Dictionary<TaskId, DurableTaskState>>>(),
            services.GetRequiredService<DeepCopier<DurableTaskState>>(),
            timeProvider);

        var grainContext = new TestGrainContext(grainId);
        var shared = new DurableTaskGrainRuntimeShared(
            new TestGrainContextAccessor(grainContext),
            timeProvider,
            NullLogger<DurableTaskGrainRuntime>.Instance,
            services.GetRequiredService<Serializer>());
        var transport = withTransport ? new RecordingDurableTaskMessageTransport() : null;
        IEnumerable<IDurableTaskMessageTransport> transports = transport is null ? [] : [transport];
        var runtime = new DurableTaskGrainRuntime(storage, shared, transports);

        return new Fixture
        {
            Runtime = runtime,
            Storage = storage,
            Transport = transport,
            TimeProvider = timeProvider,
            GrainId = grainId,
            Shared = shared,
        };
    }

    private static DurableTaskGrainRuntime CreateSecondRuntime(Fixture original)
    {
        IEnumerable<IDurableTaskMessageTransport> transports = original.Transport is null ? [] : [original.Transport];
        return new DurableTaskGrainRuntime(original.Storage, original.Shared, transports);
    }

    /// <summary>
    /// A bounded cancellation token used in place of <see cref="CancellationToken.None"/> when awaiting a
    /// <see cref="IScheduledTaskHandle.WaitAsync"/> call whose completion is driven by this test's own code
    /// (e.g. a fire-and-forget invocation or a manually-completed <see cref="TaskCompletionSource{TResult}"/>).
    /// This turns a genuine production/test bug that would otherwise hang the test run indefinitely into a fast,
    /// clearly-reported test failure instead.
    /// </summary>
    private static CancellationToken BoundedWait() => new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token;

    [Fact]
    public async Task ScheduleAsync_NewTask_PersistsStateAndReturnsPending()
    {
        var fixture = CreateFixture();
        var taskId = TaskId.Create("task-1");
        var request = new RuntimeTestDurableTaskRequest(() => DurableTask.FromResult(42))
        {
            Context = new DurableTaskRequestContext { CallerId = default, TargetId = fixture.GrainId },
        };

        var response = await ((IDurableTaskServer)fixture.Runtime).ScheduleAsync(taskId, request, CancellationToken.None);

        // No caller was registered (CallerId is default), so the response should be a bare 'Pending', not 'Subscribed'.
        Assert.Same(DurableTaskResponse.Pending, response);
        Assert.True(fixture.Storage.TryGetTask(taskId, out var state));
        Assert.Same(request, state.Request);
        Assert.Empty(state.CompletionDestinations);
        Assert.Equal(DurableTaskKind.Local, state.Kind);

        // Allow the fire-and-forget invocation to complete, then verify the final response.
        var finalResponse = await fixture.Runtime.GetScheduledTaskHandle(taskId).WaitAsync(BoundedWait());
        Assert.True(finalResponse.IsCompleted);
        Assert.Equal(42, finalResponse.GetResult<int>());
        Assert.Equal(1, request.CreateTaskCallCount);
        Assert.Single(request.SetTargetCalls);
    }

    [Fact]
    public async Task ScheduleAsync_NewTask_WithAddressableCaller_ReturnsSubscribed()
    {
        var fixture = CreateFixture();
        var taskId = TaskId.Create("task-1");
        var caller = GrainId.Create("caller", "1");
        var request = new RuntimeTestDurableTaskRequest(() => DurableTask.FromResult(7))
        {
            Context = new DurableTaskRequestContext { CallerId = caller, TargetId = fixture.GrainId },
        };

        var response = await ((IDurableTaskServer)fixture.Runtime).ScheduleAsync(taskId, request, CancellationToken.None);

        Assert.Same(DurableTaskResponse.Subscribed, response);
        Assert.True(fixture.Storage.TryGetTask(taskId, out var state));
        Assert.Contains(caller, state.CompletionDestinations);
    }

    [Fact]
    public async Task ScheduleAsync_SameTaskId_WhileRunning_DoesNotReInvokeRequest_AndRegistersNewCaller()
    {
        // A message transport is required here: the second caller is registered as a completion destination,
        // and completing the task will attempt to notify all completion destinations via the transport.
        var fixture = CreateFixture(withTransport: true);
        var taskId = TaskId.Create("task-1");
        var tcs = new TaskCompletionSource<int>();
        var firstCaller = default(GrainId);
        var firstRequest = new RuntimeTestDurableTaskRequest(() => DurableTask.Run<int>(_ => tcs.Task))
        {
            Context = new DurableTaskRequestContext { CallerId = firstCaller, TargetId = fixture.GrainId },
        };

        var firstResponse = await ((IDurableTaskServer)fixture.Runtime).ScheduleAsync(taskId, firstRequest, CancellationToken.None);
        Assert.Same(DurableTaskResponse.Pending, firstResponse);
        Assert.Equal(1, firstRequest.CreateTaskCallCount);

        // A second, distinct caller polls/subscribes for the same task id while it is still running.
        var secondCaller = GrainId.Create("caller", "2");
        var secondRequest = new RuntimeTestDurableTaskRequest(() => throw new InvalidOperationException("Should not be invoked: the task is already running."))
        {
            Context = new DurableTaskRequestContext { CallerId = secondCaller, TargetId = fixture.GrainId },
        };

        var secondResponse = await ((IDurableTaskServer)fixture.Runtime).ScheduleAsync(taskId, secondRequest, CancellationToken.None);

        Assert.Same(DurableTaskResponse.Subscribed, secondResponse);
        Assert.Equal(0, secondRequest.CreateTaskCallCount);
        Assert.True(fixture.Storage.TryGetTask(taskId, out var state));
        Assert.Contains(secondCaller, state.CompletionDestinations);

        // Complete the underlying task and let the invocation finish.
        tcs.SetResult(99);

        var finalResponse = await fixture.Runtime.GetScheduledTaskHandle(taskId).WaitAsync(BoundedWait());
        Assert.Equal(99, finalResponse.GetResult<int>());

        // The second caller was registered as a completion destination while the task was running; verify it was
        // actually notified (with the correct final response) once the task completed.
        var completion = Assert.Single(fixture.Transport!.Completions, c => c.Target.Equals(secondCaller));
        Assert.Equal(taskId, completion.TaskId);
        Assert.Equal(99, completion.Response.GetResult<int>());
    }

    [Fact]
    public async Task ScheduleAsync_ConcurrentDuplicateWhilePersisting_UsesReservedHandle()
    {
        var storage = new RpcTestDurableTaskGrainStorage { BlockNextWrite = true };
        var grainContext = new TestGrainContext(GrainId.Create("test-grain", "concurrent-schedule"));
        var runtime = RpcTestRuntimeFactory.Create(storage, grainContext);
        var taskId = TaskId.Create("concurrent-schedule");
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRequest = new RuntimeTestDurableTaskRequest(() => DurableTask.Run<int>(_ => completion.Task))
        {
            Context = new DurableTaskRequestContext { TargetId = grainContext.GrainId },
        };
        var duplicateRequest = new RuntimeTestDurableTaskRequest(
            () => throw new InvalidOperationException("A duplicate schedule must not start another invocation."))
        {
            Context = new DurableTaskRequestContext { TargetId = grainContext.GrainId },
        };

        var firstSchedule = ((IDurableTaskServer)runtime).ScheduleAsync(taskId, firstRequest, CancellationToken.None).AsTask();
        await storage.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var duplicateResponse = await ((IDurableTaskServer)runtime).ScheduleAsync(
            taskId,
            duplicateRequest,
            CancellationToken.None);

        storage.AllowWrite.SetResult();
        var firstResponse = await firstSchedule.WaitAsync(TimeSpan.FromSeconds(10));
        completion.SetResult(42);
        var finalResponse = await runtime.GetScheduledTaskHandle(taskId).WaitAsync(BoundedWait());

        Assert.Same(DurableTaskResponse.Pending, duplicateResponse);
        Assert.Same(DurableTaskResponse.Pending, firstResponse);
        Assert.Equal(1, firstRequest.CreateTaskCallCount);
        Assert.Equal(0, duplicateRequest.CreateTaskCallCount);
        Assert.Equal(42, finalResponse.GetResult<int>());
    }

    [Fact]
    public async Task ScheduleAsync_InitialPersistenceFailure_RemovesPendingTaskForRetry()
    {
        var expected = new IOException("Expected write failure.");
        var storage = new RpcTestDurableTaskGrainStorage { NextWriteException = expected };
        var grainContext = new TestGrainContext(GrainId.Create("test-grain", "write-retry"));
        var runtime = RpcTestRuntimeFactory.Create(storage, grainContext);
        var taskId = TaskId.Create("write-retry");
        var request = new RuntimeTestDurableTaskRequest(() => DurableTask.FromResult(42))
        {
            Context = new DurableTaskRequestContext { TargetId = grainContext.GrainId },
        };

        var exception = await Assert.ThrowsAsync<IOException>(
            () => ((IDurableTaskServer)runtime).ScheduleAsync(taskId, request, CancellationToken.None).AsTask());
        Assert.Same(expected, exception);
        Assert.False(storage.TryGetTask(taskId, out _));

        var retryResponse = await ((IDurableTaskServer)runtime).ScheduleAsync(taskId, request, CancellationToken.None);
        var finalResponse = await runtime.GetScheduledTaskHandle(taskId).WaitAsync(BoundedWait());

        Assert.Same(DurableTaskResponse.Pending, retryResponse);
        Assert.Equal(42, finalResponse.GetResult<int>());
        Assert.Equal(1, request.CreateTaskCallCount);
    }

    [Fact]
    public async Task ScheduleAsync_SameTaskId_FromSameCallerWhileRunning_ReturnsPendingWithoutDuplicateSubscription()
    {
        var fixture = CreateFixture(withTransport: true);
        var taskId = TaskId.Create("task-same-caller");
        var caller = GrainId.Create("caller", "same");
        var tcs = new TaskCompletionSource<int>();
        var firstRequest = new RuntimeTestDurableTaskRequest(() => DurableTask.Run<int>(_ => tcs.Task))
        {
            Context = new DurableTaskRequestContext { CallerId = caller, TargetId = fixture.GrainId },
        };

        var firstResponse = await ((IDurableTaskServer)fixture.Runtime).ScheduleAsync(taskId, firstRequest, CancellationToken.None);
        Assert.Same(DurableTaskResponse.Subscribed, firstResponse);

        var secondRequest = new RuntimeTestDurableTaskRequest(() => throw new InvalidOperationException("Should not be invoked: the caller is already subscribed."))
        {
            Context = new DurableTaskRequestContext { CallerId = caller, TargetId = fixture.GrainId },
        };

        var secondResponse = await ((IDurableTaskServer)fixture.Runtime).ScheduleAsync(taskId, secondRequest, CancellationToken.None);

        Assert.Same(DurableTaskResponse.Pending, secondResponse);
        Assert.Equal(0, secondRequest.CreateTaskCallCount);
        Assert.True(fixture.Storage.TryGetTask(taskId, out var state));
        Assert.Single(state.CompletionDestinations);
        Assert.Contains(caller, state.CompletionDestinations);

        tcs.SetResult(5);
        var finalResponse = await fixture.Runtime.GetScheduledTaskHandle(taskId).WaitAsync(BoundedWait());
        Assert.Equal(5, finalResponse.GetResult<int>());

        var completion = Assert.Single(fixture.Transport!.Completions);
        Assert.Equal(caller, completion.Target);
        Assert.Equal(taskId, completion.TaskId);
    }

    [Fact]
    public async Task ScheduleAsync_AfterCompletion_ReturnsCachedResponse_WithoutReInvokingRequest()
    {
        var fixture = CreateFixture();
        var taskId = TaskId.Create("task-1");
        var request = new RuntimeTestDurableTaskRequest(() => DurableTask.FromResult(11))
        {
            Context = new DurableTaskRequestContext { CallerId = default, TargetId = fixture.GrainId },
        };

        await ((IDurableTaskServer)fixture.Runtime).ScheduleAsync(taskId, request, CancellationToken.None);
        await fixture.Runtime.GetScheduledTaskHandle(taskId).WaitAsync(BoundedWait());
        Assert.Equal(1, request.CreateTaskCallCount);

        var thirdRequest = new RuntimeTestDurableTaskRequest(() => throw new InvalidOperationException("Should not be invoked: the task already completed."))
        {
            Context = new DurableTaskRequestContext { CallerId = GrainId.Create("caller", "3"), TargetId = fixture.GrainId },
        };

        var response = await ((IDurableTaskServer)fixture.Runtime).ScheduleAsync(taskId, thirdRequest, CancellationToken.None);

        Assert.True(response.IsCompleted);
        Assert.Equal(11, response.GetResult<int>());
        Assert.Equal(0, thirdRequest.CreateTaskCallCount);
    }

    [Fact]
    public async Task ScheduleAsync_NoContext_ThrowsInvalidOperationException()
    {
        var fixture = CreateFixture();
        var taskId = TaskId.Create("task-1");
        var request = new RuntimeTestDurableTaskRequest { Context = null };

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await ((IDurableTaskServer)fixture.Runtime).ScheduleAsync(taskId, request, CancellationToken.None));
    }

    [Fact]
    public async Task ScheduleAsync_CancellationAlreadyRequested_ReturnsCanceledWithoutInvokingRequest()
    {
        var fixture = CreateFixture(withTransport: true);
        var taskId = TaskId.Create("cancellation-tombstone");
        var caller = GrainId.Create("caller", "canceled");
        var tombstone = fixture.Storage.GetOrCreateTask(taskId, request: null);
        fixture.Storage.SetCancellationTombstone(taskId, tombstone, value: true);
        fixture.Storage.RequestCancellation(taskId, tombstone);

        var request = new RuntimeTestDurableTaskRequest(() => throw new InvalidOperationException("Should not be invoked: the task was already canceled."))
        {
            Context = new DurableTaskRequestContext { CallerId = caller, TargetId = fixture.GrainId },
        };

        var response = await ((IDurableTaskServer)fixture.Runtime).ScheduleAsync(taskId, request, CancellationToken.None);

        Assert.True(response.IsCompleted);
        Assert.IsType<OperationCanceledException>(response.Exception);
        Assert.Equal(0, request.CreateTaskCallCount);
        Assert.True(fixture.Storage.TryGetTask(taskId, out var finalState));
        Assert.Same(request, finalState.Request);
        Assert.NotNull(finalState.CancellationRequestedAt);
        Assert.True(finalState.Result!.IsCompleted);
        Assert.IsType<OperationCanceledException>(finalState.Result.Exception);
        Assert.Empty(finalState.CompletionDestinations);

        var completion = Assert.Single(fixture.Transport!.Completions);
        Assert.Equal(caller, completion.Target);
        Assert.Equal(taskId, completion.TaskId);
        Assert.IsType<OperationCanceledException>(completion.Response.Exception);
        Assert.Equal(0, fixture.Transport.CommitCount);
    }

    [Fact]
    public async Task AcceptResponse_DuplicateCompletion_DoesNotOverwritePersistedOrInMemoryResult()
    {
        var fixture = CreateFixture();
        var taskId = TaskId.Create("duplicate-completion");
        fixture.Storage.GetOrCreateTask(taskId, request: null);
        var handle = fixture.Runtime.GetScheduledTaskHandle(taskId);

        await fixture.Runtime.AcceptResponseAsync(
            taskId,
            fixture.GrainId,
            DurableTaskResponse.FromResult(1),
            CancellationToken.None);
        await fixture.Runtime.AcceptResponseAsync(
            taskId,
            fixture.GrainId,
            DurableTaskResponse.FromResult(2),
            CancellationToken.None);

        var response = await handle.WaitAsync(CancellationToken.None);
        Assert.Equal(1, response.GetResult<int>());
        Assert.True(fixture.Storage.TryGetTask(taskId, out var state));
        Assert.Equal(1, state.Result!.GetResult<int>());
    }

    [Fact]
    public async Task AcceptResponse_HandlerTransactionPublishesHandleOnlyAfterCommit()
    {
        var fixture = CreateFixture(withTransport: true);
        fixture.Transport!.EnlistWrites = true;
        var taskId = TaskId.Create("transactional-response");
        fixture.Storage.GetOrCreateTask(taskId, request: null);
        var handle = fixture.Runtime.GetScheduledTaskHandle(taskId);

        await fixture.Runtime.AcceptResponseAsync(
            taskId,
            fixture.GrainId,
            DurableTaskResponse.FromResult(7),
            CancellationToken.None);

        Assert.False((await handle.PollAsync(
            new PollingOptions { PollTimeout = TimeSpan.Zero },
            CancellationToken.None)).IsCompleted);
        fixture.Transport.CompleteTransaction(committed: true);
        Assert.Equal(7, (await handle.WaitAsync(BoundedWait())).GetResult<int>());
    }

    [Fact]
    public async Task ResumePendingTasksAsync_OnFreshRuntimeInstance_ResumesIncompletePendingTasks()
    {
        var fixture = CreateFixture();
        var taskId = TaskId.Create("resumable");

        // Schedule using an async delegate so the task does not complete before the simulated restart.
        var tcs = new TaskCompletionSource<int>();
        var pendingRequest = new RuntimeTestDurableTaskRequest(() => DurableTask.Run<int>(_ => tcs.Task))
        {
            Context = new DurableTaskRequestContext { CallerId = default, TargetId = fixture.GrainId },
        };
        await ((IDurableTaskServer)fixture.Runtime).ScheduleAsync(taskId, pendingRequest, CancellationToken.None);
        Assert.Equal(1, pendingRequest.CreateTaskCallCount);
        Assert.Single(pendingRequest.SetTargetCalls);

        // Simulate a restart: build a brand-new runtime instance sharing the same storage.
        var restarted = CreateSecondRuntime(fixture);

        await restarted.ResumePendingTasksAsync(CancellationToken.None);

        // The original request object is what got persisted, and it is what gets re-invoked on resume.
        Assert.Equal(2, pendingRequest.CreateTaskCallCount);
        // SetTarget is called once during the original ScheduleAsync (targeting the original grain context) and once
        // more during ResumePendingTasksAsync on the restarted runtime (re-targeting the request to the new grain
        // context/activation), so the request observes two distinct SetTarget calls across its lifetime.
        Assert.Equal(2, pendingRequest.SetTargetCalls.Count);

        tcs.SetResult(55);
        var response = await restarted.GetScheduledTaskHandle(taskId).WaitAsync(BoundedWait());
        Assert.Equal(55, response.GetResult<int>());
    }

    [Fact]
    public async Task ResumePendingTasksAsync_TaskWithCancellationRequested_CompletesAsCanceled_WithoutReInvokingRequest()
    {
        var fixture = CreateFixture();
        var taskId = TaskId.Create("canceled-before-restart");
        var tcs = new TaskCompletionSource<int>();
        var request = new RuntimeTestDurableTaskRequest(() => DurableTask.Run<int>(_ => tcs.Task))
        {
            Context = new DurableTaskRequestContext { CallerId = default, TargetId = fixture.GrainId },
        };
        await ((IDurableTaskServer)fixture.Runtime).ScheduleAsync(taskId, request, CancellationToken.None);

        // Request cancellation while the task is still outstanding, then simulate a restart before it observes cancellation.
        await fixture.Runtime.SignalCancellationAsync(taskId, CancellationToken.None);
        Assert.True(fixture.Storage.TryGetTask(taskId, out var stateBeforeRestart));
        Assert.NotNull(stateBeforeRestart.CancellationRequestedAt);
        Assert.Equal(DurableTaskStatus.Canceled, stateBeforeRestart.Result!.Status);

        var restarted = CreateSecondRuntime(fixture);
        await restarted.ResumePendingTasksAsync(CancellationToken.None);

        // The request must not be re-invoked: a canceled, unstarted (from the new instance's perspective) task is
        // completed directly with an OperationCanceledException instead.
        Assert.Equal(1, request.CreateTaskCallCount);
        Assert.True(fixture.Storage.TryGetTask(taskId, out var finalState));
        Assert.NotNull(finalState.Result);
        Assert.True(finalState.Result!.IsCompleted);
        Assert.IsType<OperationCanceledException>(finalState.Result.Exception);
    }

    [Fact]
    public async Task ResumePendingTasksAsync_AlreadyCompletedTask_IsNotResumed()
    {
        var fixture = CreateFixture();
        var taskId = TaskId.Create("completed");
        var request = new RuntimeTestDurableTaskRequest(() => DurableTask.FromResult(1))
        {
            Context = new DurableTaskRequestContext { CallerId = default, TargetId = fixture.GrainId },
        };
        await ((IDurableTaskServer)fixture.Runtime).ScheduleAsync(taskId, request, CancellationToken.None);
        await fixture.Runtime.GetScheduledTaskHandle(taskId).WaitAsync(BoundedWait());
        Assert.Equal(1, request.CreateTaskCallCount);
        Assert.True(fixture.Storage.TryGetTask(taskId, out var stateBeforeRestart));
        var completedAt = stateBeforeRestart!.CompletedAt;
        Assert.NotNull(completedAt);
        Assert.Equal(1, stateBeforeRestart.Result!.GetResult<int>());

        var restarted = CreateSecondRuntime(fixture);
        await restarted.ResumePendingTasksAsync(CancellationToken.None);

        Assert.Equal(1, request.CreateTaskCallCount);
        Assert.True(fixture.Storage.TryGetTask(taskId, out var stateAfterRestart));
        Assert.Equal(completedAt, stateAfterRestart!.CompletedAt);
        Assert.True(stateAfterRestart.Result!.IsCompleted);
        Assert.Equal(1, stateAfterRestart.Result.GetResult<int>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResumePendingTasksAsync_ReplaysLocalChildRegardlessOfStorageOrder(bool childFirst)
    {
        var fixture = CreateFixture();
        var parentId = TaskId.Create($"local-recovery-{childFirst}");
        var childId = parentId.Child("named:5:child:0");
        var childStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var childCompletion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var childInvocationCount = 0;
        var request = new RuntimeTestDurableTaskRequest(Parent)
        {
            Context = new DurableTaskRequestContext { TargetId = fixture.GrainId },
        };

        if (childFirst)
        {
            AddChild();
            AddParent();
        }
        else
        {
            AddParent();
            AddChild();
        }

        await fixture.Storage.WriteAsync(CancellationToken.None);
        await fixture.Storage.ReadAsync(CancellationToken.None);
        var restarted = CreateSecondRuntime(fixture);

        await restarted.ResumePendingTasksAsync(CancellationToken.None);
        await childStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, Volatile.Read(ref childInvocationCount));
        childCompletion.SetResult(17);
        var response = await restarted.GetScheduledTaskHandle(parentId).WaitAsync(BoundedWait());
        Assert.Equal(17, response.GetResult<int>());
        Assert.Equal(1, request.CreateTaskCallCount);
        Assert.Equal(1, Volatile.Read(ref childInvocationCount));

        void AddParent()
        {
            var state = fixture.Storage.GetOrCreateTask(parentId, request);
            fixture.Storage.SetTaskKind(parentId, state, DurableTaskKind.Local);
        }

        void AddChild()
        {
            var state = fixture.Storage.GetOrCreateTask(childId, request: null);
            fixture.Storage.SetTaskKind(childId, state, DurableTaskKind.Local);
        }

        async DurableTask<int> Parent()
        {
            return await DurableTask.Run<int>(_ =>
            {
                Interlocked.Increment(ref childInvocationCount);
                childStarted.TrySetResult();
                return childCompletion.Task;
            }).WithId("child");
        }
    }

    [Fact]
    public async Task Completion_NotifiesAllRegisteredCompletionDestinations_ThenClearsThem()
    {
        var fixture = CreateFixture(withTransport: true);
        var taskId = TaskId.Create("fan-out");
        var tcs = new TaskCompletionSource<int>();
        var callerA = GrainId.Create("caller", "a");
        var callerB = GrainId.Create("caller", "b");

        var requestA = new RuntimeTestDurableTaskRequest(() => DurableTask.Run<int>(_ => tcs.Task))
        {
            Context = new DurableTaskRequestContext { CallerId = callerA, TargetId = fixture.GrainId },
        };
        var firstResponse = await ((IDurableTaskServer)fixture.Runtime).ScheduleAsync(taskId, requestA, CancellationToken.None);
        Assert.Same(DurableTaskResponse.Subscribed, firstResponse);

        var requestB = new RuntimeTestDurableTaskRequest(() => throw new InvalidOperationException("Should not run"))
        {
            Context = new DurableTaskRequestContext { CallerId = callerB, TargetId = fixture.GrainId },
        };
        var secondResponse = await ((IDurableTaskServer)fixture.Runtime).ScheduleAsync(taskId, requestB, CancellationToken.None);
        Assert.Same(DurableTaskResponse.Subscribed, secondResponse);

        Assert.True(fixture.Storage.TryGetTask(taskId, out var stateBeforeCompletion));
        Assert.Equal(2, stateBeforeCompletion.CompletionDestinations.Count);

        tcs.SetResult(7);
        var finalResponse = await fixture.Runtime.GetScheduledTaskHandle(taskId).WaitAsync(BoundedWait());
        Assert.Equal(7, finalResponse.GetResult<int>());

        Assert.Equal(2, fixture.Transport!.Completions.Count);
        Assert.Contains(fixture.Transport.Completions, c => c.Sender == fixture.GrainId && c.Target == callerA && c.TaskId.Equals(taskId) && c.Response.GetResult<int>() == 7);
        Assert.Contains(fixture.Transport.Completions, c => c.Sender == fixture.GrainId && c.Target == callerB && c.TaskId.Equals(taskId) && c.Response.GetResult<int>() == 7);
        Assert.Equal(0, fixture.Transport.CommitCount);

        Assert.True(fixture.Storage.TryGetTask(taskId, out var stateAfterCompletion));
        Assert.Empty(stateAfterCompletion.CompletionDestinations);
    }

    [Fact]
    public async Task PruneCompletedTasks_TaskWithNoDestinations_IsRetainedUntilCleanupAgeElapses()
    {
        var fixture = CreateFixture();
        var taskId = TaskId.Create("prune-me");
        var request = new RuntimeTestDurableTaskRequest(() => DurableTask.FromResult(1))
        {
            Context = new DurableTaskRequestContext { CallerId = default, TargetId = fixture.GrainId },
        };
        await ((IDurableTaskServer)fixture.Runtime).ScheduleAsync(taskId, request, CancellationToken.None);
        await fixture.Runtime.GetScheduledTaskHandle(taskId).WaitAsync(BoundedWait());

        // Immediately after completion, the default 1-day cleanup age has not elapsed: the task must still be present.
        Assert.True(fixture.Storage.TryGetTask(taskId, out _));

        // Advance time past the default cleanup age (1 day) and complete an unrelated task to trigger another prune pass.
        fixture.TimeProvider.Advance(TimeSpan.FromDays(2));
        var otherTaskId = TaskId.Create("trigger-prune");
        var otherRequest = new RuntimeTestDurableTaskRequest(() => DurableTask.FromResult(2))
        {
            Context = new DurableTaskRequestContext { CallerId = default, TargetId = fixture.GrainId },
        };
        await ((IDurableTaskServer)fixture.Runtime).ScheduleAsync(otherTaskId, otherRequest, CancellationToken.None);
        await fixture.Runtime.GetScheduledTaskHandle(otherTaskId).WaitAsync(BoundedWait());

        Assert.False(fixture.Storage.TryGetTask(taskId, out _));
    }

    [Fact]
    public async Task PruneCompletedTasks_TaskWithUnacknowledgedDestination_IsNeverPruned()
    {
        var fixture = CreateFixture(withTransport: true);
        var taskId = TaskId.Create("has-destination");
        var caller = GrainId.Create("caller", "1");
        var request = new RuntimeTestDurableTaskRequest(() => DurableTask.FromResult(1))
        {
            Context = new DurableTaskRequestContext { CallerId = default, TargetId = fixture.GrainId },
        };

        // Register the completion destination via a second, still-in-flight call before the task completes.
        var tcs = new TaskCompletionSource<int>();
        var firstRequest = new RuntimeTestDurableTaskRequest(() => DurableTask.Run<int>(_ => tcs.Task))
        {
            Context = new DurableTaskRequestContext { CallerId = default, TargetId = fixture.GrainId },
        };
        await ((IDurableTaskServer)fixture.Runtime).ScheduleAsync(taskId, firstRequest, CancellationToken.None);

        var subscribeRequest = new RuntimeTestDurableTaskRequest(() => throw new InvalidOperationException("Should not run"))
        {
            Context = new DurableTaskRequestContext { CallerId = caller, TargetId = fixture.GrainId },
        };
        await ((IDurableTaskServer)fixture.Runtime).ScheduleAsync(taskId, subscribeRequest, CancellationToken.None);

        tcs.SetResult(1);
        await fixture.Runtime.GetScheduledTaskHandle(taskId).WaitAsync(BoundedWait());

        // The completion is sent (and destinations cleared) synchronously as part of completion; simulate a case where
        // that hasn't happened by asserting the immediate post-completion state, then confirm pruning proceeds once
        // cleared, whereas a task manually re-populated with a destination would not be pruned.
        Assert.True(fixture.Storage.TryGetTask(taskId, out var clearedState));
        Assert.Empty(clearedState.CompletionDestinations);

        // Re-add a completion destination directly in storage to simulate a client which has not yet been notified,
        // then verify that advancing time and triggering another prune pass does not remove the task.
        fixture.Storage.AddCompletionDestination(taskId, clearedState, GrainId.Create("late-subscriber", "1"));
        fixture.TimeProvider.Advance(TimeSpan.FromDays(2));

        var otherTaskId = TaskId.Create("trigger-prune-2");
        var otherRequest = new RuntimeTestDurableTaskRequest(() => DurableTask.FromResult(9))
        {
            Context = new DurableTaskRequestContext { CallerId = default, TargetId = fixture.GrainId },
        };
        await ((IDurableTaskServer)fixture.Runtime).ScheduleAsync(otherTaskId, otherRequest, CancellationToken.None);
        await fixture.Runtime.GetScheduledTaskHandle(otherTaskId).WaitAsync(BoundedWait());

        Assert.True(fixture.Storage.TryGetTask(taskId, out _));
    }

    [Fact]
    public async Task PruneCompletedTasks_CompletedChildIsPruned_OnceParentAlsoCompletesAndCleanupAgeElapses()
    {
        var fixture = CreateFixture();
        var parentId = TaskId.Create("parent-prune");
        var childId = parentId.Child("child-prune");

        var parentTcs = new TaskCompletionSource<int>();
        var parentTask = DurableTask.Run<int>(_ => parentTcs.Task);
        await fixture.Runtime.ScheduleChildAsync(parentId, parentTask, CancellationToken.None);

        var childHandle = await fixture.Runtime.ScheduleChildAsync(childId, DurableTask.Run(_ => 3), CancellationToken.None);
        var childResponse = await childHandle.WaitAsync(BoundedWait());
        Assert.Equal(3, childResponse.GetResult<int>());

        // The child completed, but the parent has not: the child must be retained (nothing to trigger a prune pass on it yet).
        Assert.True(fixture.Storage.TryGetTask(childId, out _));

        // Complete the parent, then advance time well past the cleanup age and trigger another completion to prune.
        parentTcs.SetResult(1);
        await fixture.Runtime.GetScheduledTaskHandle(parentId).WaitAsync(BoundedWait());

        fixture.TimeProvider.Advance(TimeSpan.FromDays(2));
        var otherTaskId = TaskId.Create("trigger-prune-3");
        var otherRequest = new RuntimeTestDurableTaskRequest(() => DurableTask.FromResult(0))
        {
            Context = new DurableTaskRequestContext { CallerId = default, TargetId = fixture.GrainId },
        };
        await ((IDurableTaskServer)fixture.Runtime).ScheduleAsync(otherTaskId, otherRequest, CancellationToken.None);
        await fixture.Runtime.GetScheduledTaskHandle(otherTaskId).WaitAsync(BoundedWait());

        Assert.False(fixture.Storage.TryGetTask(parentId, out _));
        Assert.False(fixture.Storage.TryGetTask(childId, out _));
    }

    [Fact]
    public async Task SignalCancellationAsync_CascadesToChildrenAndGrandchildren()
    {
        var fixture = CreateFixture();
        var parentId = TaskId.Create("cascade-parent");
        var childId = parentId.Child("child");
        var grandchildId = childId.Child("grandchild");
        var siblingId = parentId.Child("sibling");

        var parentTcs = new TaskCompletionSource<int>();
        var childTcs = new TaskCompletionSource<int>();
        var grandchildTcs = new TaskCompletionSource<int>();
        var siblingTcs = new TaskCompletionSource<int>();

        await fixture.Runtime.ScheduleChildAsync(parentId, DurableTask.Run<int>(_ => parentTcs.Task), CancellationToken.None);
        await fixture.Runtime.ScheduleChildAsync(childId, DurableTask.Run<int>(_ => childTcs.Task), CancellationToken.None);
        await fixture.Runtime.ScheduleChildAsync(grandchildId, DurableTask.Run<int>(_ => grandchildTcs.Task), CancellationToken.None);
        await fixture.Runtime.ScheduleChildAsync(siblingId, DurableTask.Run<int>(_ => siblingTcs.Task), CancellationToken.None);

        await fixture.Runtime.SignalCancellationAsync(parentId, CancellationToken.None);

        Assert.True(fixture.Storage.TryGetTask(parentId, out var parentState));
        Assert.True(fixture.Storage.TryGetTask(childId, out var childState));
        Assert.True(fixture.Storage.TryGetTask(grandchildId, out var grandchildState));
        Assert.True(fixture.Storage.TryGetTask(siblingId, out var siblingState));

        Assert.NotNull(parentState.CancellationRequestedAt);
        Assert.NotNull(childState.CancellationRequestedAt);
        Assert.NotNull(grandchildState.CancellationRequestedAt);
        Assert.NotNull(siblingState.CancellationRequestedAt);

        // The requested root reaches a terminal canceled result before acknowledgement. Descendants retain their
        // cancellation requests and can finish cleanup independently.
        Assert.Equal(DurableTaskStatus.Canceled, parentState.Result!.Status);
        Assert.Null(childState.Result);
        Assert.Null(grandchildState.Result);
        Assert.Null(siblingState.Result);
    }

    [Fact]
    public async Task SignalCancellationAsync_CalledTwice_IsIdempotent()
    {
        var fixture = CreateFixture();
        var taskId = TaskId.Create("cancel-twice");
        var tcs = new TaskCompletionSource<int>();
        await fixture.Runtime.ScheduleChildAsync(taskId, DurableTask.Run<int>(_ => tcs.Task), CancellationToken.None);

        await fixture.Runtime.SignalCancellationAsync(taskId, CancellationToken.None);
        Assert.True(fixture.Storage.TryGetTask(taskId, out var firstState));
        var firstRequestedAt = firstState.CancellationRequestedAt;
        Assert.NotNull(firstRequestedAt);

        fixture.TimeProvider.Advance(TimeSpan.FromSeconds(1));
        await fixture.Runtime.SignalCancellationAsync(taskId, CancellationToken.None);

        Assert.True(fixture.Storage.TryGetTask(taskId, out var secondState));
        Assert.Equal(firstRequestedAt, secondState.CancellationRequestedAt);
    }

    [Fact]
    public async Task LocalTaskHandle_CancelAsync_CancelsRunningInvocation()
    {
        var fixture = CreateFixture();
        var taskId = TaskId.Create("cancel-local-handle");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = DurableTask.Run(async cancellationToken =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });

        var handle = await fixture.Runtime.ScheduleChildAsync(taskId, task, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await handle.CancelAsync(CancellationToken.None);
        var response = await handle.WaitAsync(BoundedWait());

        Assert.Equal(DurableTaskStatus.Canceled, response.Status);
        Assert.True(fixture.Storage.TryGetTask(taskId, out var state));
        Assert.NotNull(state.CancellationRequestedAt);
    }

    [Fact]
    public async Task TaskHandle_PollAsync_PropagatesCallerCancellation()
    {
        var fixture = CreateFixture();
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = await fixture.Runtime.ScheduleChildAsync(
            TaskId.Create("poll-cancellation"),
            DurableTask.Run<int>(_ => completion.Task),
            CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => handle.PollAsync(
                new PollingOptions { PollTimeout = TimeSpan.FromMinutes(1) },
                cancellation.Token).AsTask());

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        completion.SetResult(1);
        await handle.WaitAsync(BoundedWait());
    }

    [Fact]
    public async Task SignalCancellationAsync_UnknownTaskId_CreatesTombstoneRecordingCancellation()
    {
        var fixture = CreateFixture();
        var taskId = TaskId.Create("never-scheduled");

        await fixture.Runtime.SignalCancellationAsync(taskId, CancellationToken.None);

        Assert.True(fixture.Storage.TryGetTask(taskId, out var state));
        Assert.NotNull(state.CancellationRequestedAt);
        Assert.Null(state.Request);
    }

    [Fact]
    public async Task SignalCancellationAsync_DefaultTaskId_Throws()
    {
        var fixture = CreateFixture();
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await fixture.Runtime.SignalCancellationAsync(default, CancellationToken.None));
    }

    [Fact]
    public async Task SignalCancellationAsync_CompletedTask_DoesNotRecordCancellation()
    {
        var fixture = CreateFixture();
        var taskId = TaskId.Create("already-completed");
        var request = new RuntimeTestDurableTaskRequest(() => DurableTask.FromResult(8))
        {
            Context = new DurableTaskRequestContext { CallerId = default, TargetId = fixture.GrainId },
        };

        await ((IDurableTaskServer)fixture.Runtime).ScheduleAsync(taskId, request, CancellationToken.None);
        await fixture.Runtime.GetScheduledTaskHandle(taskId).WaitAsync(BoundedWait());
        Assert.True(fixture.Storage.TryGetTask(taskId, out var beforeCancellation));
        var completedAt = beforeCancellation.CompletedAt;
        Assert.Null(beforeCancellation.CancellationRequestedAt);

        fixture.TimeProvider.Advance(TimeSpan.FromMinutes(1));
        await fixture.Runtime.SignalCancellationAsync(taskId, CancellationToken.None);

        Assert.True(fixture.Storage.TryGetTask(taskId, out var afterCancellation));
        Assert.Null(afterCancellation.CancellationRequestedAt);
        Assert.Equal(completedAt, afterCancellation.CompletedAt);
        Assert.Equal(8, afterCancellation.Result!.GetResult<int>());
    }

    [Fact]
    public async Task ScheduleChildAsync_SchedulableTask_CompletesImmediately_ReturnsCompletedHandle()
    {
        var fixture = CreateFixture();
        var taskId = TaskId.Create("schedulable-completed");
        var task = new TestSchedulableTask((_, _) => new ValueTask<DurableTaskResponse>(DurableTaskResponse.FromResult(321)));

        var handle = await fixture.Runtime.ScheduleChildAsync(taskId, task, CancellationToken.None);

        Assert.Equal(1, task.ScheduleAsyncCallCount);
        Assert.Equal(taskId, handle.TaskId);

        var polled = await handle.PollAsync(new PollingOptions { PollTimeout = TimeSpan.Zero }, CancellationToken.None);
        Assert.True(polled.IsCompleted);
        Assert.Equal(321, polled.GetResult<int>());

        // Cancelling an already-completed handle must be a safe no-op.
        await handle.CancelAsync(CancellationToken.None);

        Assert.True(fixture.Storage.TryGetTask(taskId, out var state));
        Assert.NotNull(state.Result);
        Assert.Equal(321, state.Result!.GetResult<int>());
        Assert.Equal(DurableTaskKind.Scheduled, state.Kind);
    }

    [Fact]
    public async Task ScheduleChildAsync_SchedulableTask_NotYetCompleted_ReturnsProvidedHandle_AndIsIdempotentOnRepeatedCalls()
    {
        var fixture = CreateFixture();
        var taskId = TaskId.Create("schedulable-pending");
        var providedHandle = new StubScheduledTaskHandle(taskId);
        var task = new TestSchedulableTask(
            (_, _) => new ValueTask<DurableTaskResponse>(DurableTaskResponse.Pending),
            _ => providedHandle);

        var handle = await fixture.Runtime.ScheduleChildAsync(taskId, task, CancellationToken.None);

        Assert.Same(providedHandle, handle);
        Assert.Equal(1, task.ScheduleAsyncCallCount);

        // A second call for the same, still-running task id must not re-invoke ScheduleAsync.
        var secondHandle = await fixture.Runtime.ScheduleChildAsync(taskId, task, CancellationToken.None);
        Assert.Same(providedHandle, secondHandle);
        Assert.Equal(1, task.ScheduleAsyncCallCount);
    }

    [Fact]
    public async Task ScheduleChildAsync_LocalMethodInvocation_RunsAndCompletesWithResult()
    {
        var fixture = CreateFixture();
        var taskId = TaskId.Create("local-method");
        var localTask = DurableTask.Run(_ => 5);

        var handle = await fixture.Runtime.ScheduleChildAsync(taskId, localTask, CancellationToken.None);
        var response = await handle.WaitAsync(BoundedWait());

        Assert.Equal(5, response.GetResult<int>());
        Assert.True(fixture.Storage.TryGetTask(taskId, out var state));
        Assert.Equal(5, state.Result!.GetResult<int>());
    }

    [Fact]
    public async Task ScheduleChildAsync_WhenStorageIsNotJournalIntegrated_WritesAfterSchedulerCommit()
    {
        var storage = new RpcTestDurableTaskGrainStorage();
        var grainContext = new TestGrainContext(GrainId.Create("test-grain", "atomic-scheduler"));
        var runtime = RpcTestRuntimeFactory.Create(storage, grainContext);
        var handle = new StubScheduledTaskHandle(TaskId.Create("atomic-scheduler"));
        var task = new TestSchedulableTask(
            (_, _) => ValueTask.FromResult<DurableTaskResponse>(DurableTaskResponse.Pending),
            _ => handle,
            commitsDurableState: true);

        var result = await runtime.ScheduleChildAsync(handle.TaskId, task, CancellationToken.None);

        Assert.Same(handle, result);
        Assert.Equal(1, storage.WriteAsyncCallCount);
    }

    [Fact]
    public async Task ScheduleChildAsync_ImmediateCompletionPublishesCachedHandleAfterCommit()
    {
        var storage = new RpcTestDurableTaskGrainStorage();
        var transport = new RecordingDurableTaskMessageTransport { EnlistWrites = true };
        var grainContext = new TestGrainContext(GrainId.Create("test-grain", "transactional-child"));
        var runtime = RpcTestRuntimeFactory.Create(storage, grainContext, transport);
        var taskId = TaskId.Create("transactional-child");
        var task = new TestSchedulableTask(
            (_, _) => ValueTask.FromResult<DurableTaskResponse>(DurableTaskResponse.FromResult(9)),
            _ => new StubScheduledTaskHandle(taskId),
            commitsDurableState: false);

        _ = await runtime.ScheduleChildAsync(taskId, task, CancellationToken.None);
        var cached = runtime.GetScheduledTaskHandle(taskId);
        Assert.False((await cached.PollAsync(
            new PollingOptions { PollTimeout = TimeSpan.Zero },
            CancellationToken.None)).IsCompleted);

        transport.CompleteTransaction(committed: true);
        Assert.Equal(9, (await cached.WaitAsync(BoundedWait())).GetResult<int>());
    }

    [Fact]
    public async Task ScheduleChildAsync_DelayPersistsVolatileChildState()
    {
        var fixture = CreateFixture(withTransport: true);
        var parentId = TaskId.Create("volatile-delay");
        var taskId = parentId.Child("delay");
        DurableExecutionContext.SetCurrentContext(new GrainDurableExecutionContext(parentId, fixture.Runtime));
        try
        {
            _ = await fixture.Runtime.ScheduleChildAsync(
                taskId,
                DurableTask.Delay(TimeSpan.FromSeconds(1)),
                CancellationToken.None);
        }
        finally
        {
            DurableExecutionContext.SetCurrentContext(null);
        }
        await fixture.Storage.ReadAsync(CancellationToken.None);

        Assert.True(fixture.Storage.TryGetTask(taskId, out var state));
        Assert.Null(state.Result);
        Assert.Equal(DurableTaskKind.Scheduled, state.Kind);
    }

    [Fact]
    public async Task ResumePendingTasksAsync_RehydratesScheduledDelayWithoutSchedulingDuplicate()
    {
        var fixture = CreateFixture(withTransport: true);
        var parentId = TaskId.Create("scheduled-delay-recovery");
        var taskId = parentId.Child("delay");
        DurableExecutionContext.SetCurrentContext(new GrainDurableExecutionContext(parentId, fixture.Runtime));
        try
        {
            _ = await fixture.Runtime.ScheduleChildAsync(
                taskId,
                DurableTask.Delay(TimeSpan.FromHours(1)),
                CancellationToken.None);
        }
        finally
        {
            DurableExecutionContext.SetCurrentContext(null);
        }

        Assert.Single(fixture.Transport!.ScheduledResumes);
        await fixture.Storage.ReadAsync(CancellationToken.None);
        var restarted = CreateSecondRuntime(fixture);
        await restarted.ResumePendingTasksAsync(CancellationToken.None);

        var recovered = await restarted.ScheduleChildAsync(
            taskId,
            DurableTask.Delay(TimeSpan.FromHours(2)),
            CancellationToken.None);
        var response = await recovered.PollAsync(
            new PollingOptions { PollTimeout = TimeSpan.Zero },
            CancellationToken.None);

        Assert.False(response.IsCompleted);
        Assert.Single(fixture.Transport.ScheduledResumes);
    }

    [Fact]
    public async Task ResumePendingTasksAsync_CompletesCanceledScheduledTaskWithoutReusingSchedulerMessage()
    {
        var fixture = CreateFixture(withTransport: true);
        var taskId = TaskId.Create("canceled-scheduled-recovery");
        var state = fixture.Storage.GetOrCreateTask(taskId, request: null);
        fixture.Storage.SetTaskKind(taskId, state, DurableTaskKind.Scheduled);
        fixture.Storage.RequestCancellation(taskId, state);
        await fixture.Storage.WriteAsync(CancellationToken.None);
        await fixture.Storage.ReadAsync(CancellationToken.None);
        var restarted = CreateSecondRuntime(fixture);

        await restarted.ResumePendingTasksAsync(CancellationToken.None);
        var response = await restarted.GetScheduledTaskHandle(taskId).WaitAsync(BoundedWait());

        Assert.Equal(DurableTaskStatus.Canceled, response.Status);
        Assert.Empty(fixture.Transport!.ScheduledResumes);
    }

    [Fact]
    public async Task ScheduleChildAsync_FailedSchedulingRemovesProvisionalHandle()
    {
        var fixture = CreateFixture(withTransport: true);
        var taskId = TaskId.Create("failed-scheduling");
        var failure = new InvalidOperationException("Expected scheduling failure.");
        var failed = new TestSchedulableTask(
            (_, _) => ValueTask.FromException<DurableTaskResponse>(failure),
            _ => new StubScheduledTaskHandle(taskId),
            commitsDurableState: false);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await fixture.Runtime.ScheduleChildAsync(taskId, failed, CancellationToken.None));
        Assert.Same(failure, exception);

        var retried = false;
        var retry = new TestSchedulableTask(
            (_, _) =>
            {
                retried = true;
                return ValueTask.FromResult<DurableTaskResponse>(DurableTaskResponse.Pending);
            },
            _ => new StubScheduledTaskHandle(taskId),
            commitsDurableState: false);
        var result = await fixture.Runtime.ScheduleChildAsync(taskId, retry, CancellationToken.None);

        Assert.True(retried);
        Assert.Equal(taskId, result.TaskId);
    }

    [Fact]
    public async Task ScheduleRemoteAsync_WithTransport_SendsInvocationAndCommits()
    {
        var fixture = CreateFixture(withTransport: true);
        var taskId = TaskId.Create("remote");
        var target = GrainId.Create("target", "1");
        var request = new RuntimeTestDurableTaskRequest(() => DurableTask.FromResult(1))
        {
            Context = new DurableTaskRequestContext { TargetId = target },
        };

        var response = await fixture.Runtime.ScheduleRemoteAsync(taskId, request, CancellationToken.None);

        Assert.Same(DurableTaskResponse.Pending, response);
        Assert.Equal(fixture.GrainId, request.Context!.CallerId);

        var invocation = Assert.Single(fixture.Transport!.Invocations);
        Assert.Equal(fixture.GrainId, invocation.Sender);
        Assert.Equal(target, invocation.Target);
        Assert.Equal(taskId, invocation.TaskId);
        Assert.Same(request, invocation.Request);
        Assert.True(fixture.Storage.TryGetTask(taskId, out var state));
        Assert.Same(request, state.Request);
        Assert.Equal(target, state.RemoteTarget);
        Assert.Equal(DurableTaskKind.Remote, state.Kind);
        await fixture.Runtime.AcceptResponseAsync(
            taskId,
            target,
            DurableTaskResponse.FromResult(9),
            CancellationToken.None);
        Assert.True(fixture.Storage.TryGetTask(taskId, out state));
        Assert.Equal(9, state.Result!.GetResult<int>());
        Assert.Equal(0, fixture.Transport.CommitCount);
    }

    [Fact]
    public async Task ScheduleRemoteAsync_CommitsThroughStorageTransaction()
    {
        var storage = new RpcTestDurableTaskGrainStorage();
        var transport = new RecordingDurableTaskMessageTransport();
        var grainContext = new TestGrainContext(GrainId.Create("test-grain", "atomic-remote"));
        var runtime = RpcTestRuntimeFactory.Create(storage, grainContext, transport);
        var target = GrainId.Create("target", "atomic-remote");
        var request = new RuntimeTestDurableTaskRequest
        {
            Context = new DurableTaskRequestContext { TargetId = target },
        };

        await runtime.ScheduleRemoteAsync(TaskId.Create("atomic-remote"), request, CancellationToken.None);

        Assert.Equal(1, storage.WriteAsyncCallCount);
        Assert.Equal(0, transport.CommitCount);
    }

    [Fact]
    public async Task ResumePendingTasksAsync_DoesNotExecuteRecoveredOutboundRequestLocally()
    {
        var fixture = CreateFixture(withTransport: true);
        var taskId = TaskId.Create("recovered-outbound");
        var target = GrainId.Create("target", "recovered");
        var request = new RuntimeTestDurableTaskRequest(() => DurableTask.FromResult(1))
        {
            Context = new DurableTaskRequestContext { TargetId = target },
        };
        await fixture.Runtime.ScheduleRemoteAsync(taskId, request, CancellationToken.None);
        await fixture.Storage.ReadAsync(CancellationToken.None);
        Assert.True(fixture.Storage.TryGetTask(taskId, out var recoveredState));
        var recoveredRequest = Assert.IsType<RuntimeTestDurableTaskRequest>(recoveredState.Request);

        var restarted = CreateSecondRuntime(fixture);
        await restarted.ResumePendingTasksAsync(CancellationToken.None);

        Assert.Equal(0, recoveredRequest.CreateTaskCallCount);
        await restarted.GetScheduledTaskHandle(taskId).CancelAsync(CancellationToken.None);
        Assert.Contains(
            fixture.Transport!.Cancellations,
            cancellation => cancellation.Target == target && cancellation.TaskId == taskId);
    }

    [Fact]
    public async Task ScheduleAsync_CompletionNotificationCommitsStateAndOutboxOnce()
    {
        var storage = new RpcTestDurableTaskGrainStorage();
        var transport = new RecordingDurableTaskMessageTransport();
        var grainContext = new TestGrainContext(GrainId.Create("test-grain", "atomic-completion"));
        var runtime = RpcTestRuntimeFactory.Create(storage, grainContext, transport);
        var caller = GrainId.Create("caller", "atomic-completion");
        var taskId = TaskId.Create("atomic-completion");
        var request = new RuntimeTestDurableTaskRequest(() => DurableTask.FromResult(42))
        {
            Context = new DurableTaskRequestContext { CallerId = caller, TargetId = grainContext.GrainId },
        };

        await ((IDurableTaskServer)runtime).ScheduleAsync(taskId, request, CancellationToken.None);
        var response = await runtime.GetScheduledTaskHandle(taskId).WaitAsync(BoundedWait());

        Assert.Equal(42, response.GetResult<int>());
        Assert.Equal(2, storage.WriteAsyncCallCount);
        Assert.Equal(0, transport.CommitCount);
        Assert.Single(transport.Completions);
    }

    [Fact]
    public async Task ScheduleRemoteAsync_PreCanceledToken_ThrowsWithoutSending()
    {
        var fixture = CreateFixture(withTransport: true);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var request = new RuntimeTestDurableTaskRequest
        {
            Context = new DurableTaskRequestContext { TargetId = GrainId.Create("target", "canceled") },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await fixture.Runtime.ScheduleRemoteAsync(TaskId.Create("remote-canceled"), request, cts.Token));

        Assert.Empty(fixture.Transport!.Invocations);
        Assert.Equal(0, fixture.Transport.CommitCount);
    }

    [Fact]
    public async Task ScheduleRemoteAsync_NoTransportConfigured_Throws()
    {
        var fixture = CreateFixture(withTransport: false);
        var request = new RuntimeTestDurableTaskRequest { Context = new DurableTaskRequestContext { TargetId = GrainId.Create("t", "1") } };

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await fixture.Runtime.ScheduleRemoteAsync(TaskId.Create("x"), request, CancellationToken.None));
    }

    [Fact]
    public async Task ScheduleRemoteAsync_NoRequestContext_Throws()
    {
        var fixture = CreateFixture(withTransport: true);
        var request = new RuntimeTestDurableTaskRequest { Context = null };

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await fixture.Runtime.ScheduleRemoteAsync(TaskId.Create("x"), request, CancellationToken.None));
    }

    [Fact]
    public async Task CancelRemoteAsync_WithTransport_SendsCancellationAndCommits()
    {
        var fixture = CreateFixture(withTransport: true);
        var taskId = TaskId.Create("remote-cancel");
        var target = GrainId.Create("target", "2");

        await fixture.Runtime.CancelRemoteAsync(taskId, target, CancellationToken.None);

        var cancellation = Assert.Single(fixture.Transport!.Cancellations);
        Assert.Equal(fixture.GrainId, cancellation.Sender);
        Assert.Equal(target, cancellation.Target);
        Assert.Equal(taskId, cancellation.TaskId);
        Assert.Equal(0, fixture.Transport.CommitCount);
    }

    [Fact]
    public async Task CancelRemoteAsync_NoTransportConfigured_Throws()
    {
        var fixture = CreateFixture(withTransport: false);
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await fixture.Runtime.CancelRemoteAsync(TaskId.Create("x"), GrainId.Create("t", "1"), CancellationToken.None));
    }

    [Fact]
    public async Task ScheduleDelayAsync_WithTransport_SchedulesResumeAndReturnsPending()
    {
        var fixture = CreateFixture(withTransport: true);
        var taskId = TaskId.Create("delay");
        var dueTime = DateTimeOffset.UtcNow.AddMinutes(5);

        var response = await fixture.Runtime.ScheduleDelayAsync(taskId, dueTime, CancellationToken.None);

        Assert.Same(DurableTaskResponse.Pending, response);
        var resume = Assert.Single(fixture.Transport!.ScheduledResumes);
        Assert.Equal(fixture.GrainId, resume.Target);
        Assert.Equal(taskId, resume.TaskId);
        Assert.Equal(dueTime, resume.DueTime);
    }

    [Fact]
    public async Task ScheduleDelayAsync_NoTransportConfigured_Throws()
    {
        var fixture = CreateFixture(withTransport: false);
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await fixture.Runtime.ScheduleDelayAsync(TaskId.Create("x"), DateTimeOffset.UtcNow, CancellationToken.None));
    }

    [Fact]
    public async Task SubscribeOrPollAsync_RunningTaskReturnsPendingUntilCompletion()
    {
        var fixture = CreateFixture();
        var taskId = TaskId.Create("subscribe-or-poll");
        var tcs = new TaskCompletionSource<int>();
        var request = new RuntimeTestDurableTaskRequest(() => DurableTask.Run<int>(_ => tcs.Task))
        {
            Context = new DurableTaskRequestContext { CallerId = default, TargetId = fixture.GrainId },
        };

        await ((IDurableTaskServer)fixture.Runtime).ScheduleAsync(taskId, request, CancellationToken.None);

        var pending = await fixture.Runtime.SubscribeOrPollAsync(
            taskId,
            new SubscribeOrPollOptions { PollTimeout = TimeSpan.Zero },
            CancellationToken.None);

        Assert.Same(DurableTaskResponse.Pending, pending);

        tcs.SetResult(12);
        var completed = await fixture.Runtime.SubscribeOrPollAsync(
            taskId,
            new SubscribeOrPollOptions { PollTimeout = TimeSpan.Zero },
            BoundedWait());

        Assert.True(completed.IsCompleted);
        Assert.Equal(12, completed.GetResult<int>());
    }

    [Fact]
    public async Task GetTasksAsync_ReportsDiagnosticStateForEachTask()
    {
        var fixture = CreateFixture();
        var completedId = TaskId.Create("diag-completed");
        var faultedId = TaskId.Create("diag-faulted");

        var completedRequest = new RuntimeTestDurableTaskRequest(() => DurableTask.FromResult(1), interfaceName: "IFoo", methodName: "Bar")
        {
            Context = new DurableTaskRequestContext { CallerId = default, TargetId = fixture.GrainId },
        };
        await ((IDurableTaskServer)fixture.Runtime).ScheduleAsync(completedId, completedRequest, CancellationToken.None);
        await fixture.Runtime.GetScheduledTaskHandle(completedId).WaitAsync(BoundedWait());

        var faultedRequest = new RuntimeTestDurableTaskRequest(() => DurableTask.Run<int>((Func<CancellationToken, int>)(_ => throw new InvalidOperationException("boom"))))
        {
            Context = new DurableTaskRequestContext { CallerId = default, TargetId = fixture.GrainId },
        };
        await ((IDurableTaskServer)fixture.Runtime).ScheduleAsync(faultedId, faultedRequest, CancellationToken.None);
        await fixture.Runtime.GetScheduledTaskHandle(faultedId).WaitAsync(BoundedWait());

        var states = await ((IDurableTaskGrainExtension)fixture.Runtime).GetTasksAsync(CancellationToken.None).ToListAsync();

        var completed = Assert.Single(states, s => s.TaskId.Equals(completedId));
        Assert.Equal("Completed", completed.State.Status);
        Assert.Equal("IFoo.Bar()", completed.State.Request);
        Assert.Contains("Success", completed.State.Response);

        var faulted = Assert.Single(states, s => s.TaskId.Equals(faultedId));
        Assert.Equal("Faulted", faulted.State.Status);
    }

    [Fact]
    public async Task GetRunningTasksAsync_IncludesInFlightTask_ExcludesCompletedTask()
    {
        var fixture = CreateFixture();
        var taskId = TaskId.Create("in-flight");
        var tcs = new TaskCompletionSource<int>();
        var request = new RuntimeTestDurableTaskRequest(() => DurableTask.Run<int>(_ => tcs.Task))
        {
            Context = new DurableTaskRequestContext { CallerId = default, TargetId = fixture.GrainId },
        };
        await ((IDurableTaskServer)fixture.Runtime).ScheduleAsync(taskId, request, CancellationToken.None);

        var runningWhilePending = await ((IDurableTaskGrainExtension)fixture.Runtime).GetRunningTasksAsync(CancellationToken.None).ToListAsync();
        Assert.Contains(taskId, runningWhilePending);

        tcs.SetResult(1);
        await fixture.Runtime.GetScheduledTaskHandle(taskId).WaitAsync(BoundedWait());

        var runningAfterCompletion = await ((IDurableTaskGrainExtension)fixture.Runtime).GetRunningTasksAsync(CancellationToken.None).ToListAsync();
        Assert.DoesNotContain(taskId, runningAfterCompletion);
    }

    [Fact]
    public void GetScheduledTaskHandle_UnknownTaskId_ThrowsKeyNotFoundException()
    {
        var fixture = CreateFixture();
        var exception = Assert.Throws<KeyNotFoundException>(() => fixture.Runtime.GetScheduledTaskHandle(TaskId.Create("missing")));
        Assert.Contains("missing", exception.Message);
    }

    [Fact]
    public async Task GetScheduledTaskHandle_RunningLocalTask_PollReturnsPendingUntilComplete()
    {
        var fixture = CreateFixture();
        var taskId = TaskId.Create("poll-me");
        var tcs = new TaskCompletionSource<int>();
        var request = new RuntimeTestDurableTaskRequest(() => DurableTask.Run<int>(_ => tcs.Task))
        {
            Context = new DurableTaskRequestContext { CallerId = default, TargetId = fixture.GrainId },
        };
        await ((IDurableTaskServer)fixture.Runtime).ScheduleAsync(taskId, request, CancellationToken.None);

        var handle = fixture.Runtime.GetScheduledTaskHandle(taskId);
        var polled = await handle.PollAsync(new PollingOptions { PollTimeout = TimeSpan.Zero }, CancellationToken.None);
        Assert.False(polled.IsCompleted);

        tcs.SetResult(4);
        var completed = await handle.WaitAsync(BoundedWait());
        Assert.Equal(4, completed.GetResult<int>());
    }

    [Fact]
    public async Task TaskHandle_PollAsync_UsesInjectedTimeProviderForTimeout()
    {
        var fixture = CreateFixture();
        var taskId = TaskId.Create("poll-timeout");
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new RuntimeTestDurableTaskRequest(() => DurableTask.Run<int>(_ => completion.Task))
        {
            Context = new DurableTaskRequestContext { CallerId = default, TargetId = fixture.GrainId },
        };
        await ((IDurableTaskServer)fixture.Runtime).ScheduleAsync(taskId, request, CancellationToken.None);
        var handle = fixture.Runtime.GetScheduledTaskHandle(taskId);

        var poll = handle.PollAsync(
            new PollingOptions { PollTimeout = TimeSpan.FromMinutes(1) },
            CancellationToken.None).AsTask();
        Assert.False(poll.IsCompleted);

        fixture.TimeProvider.Advance(TimeSpan.FromMinutes(1));

        Assert.Same(DurableTaskResponse.Pending, await poll.WaitAsync(BoundedWait()));
        completion.SetResult(4);
        Assert.Equal(4, (await handle.WaitAsync(BoundedWait())).GetResult<int>());
    }

    [Fact]
    public async Task GetScheduledTaskHandle_RehydratesCompletedTaskFromStorage_WhenNoLocalHandleExists()
    {
        var fixture = CreateFixture();
        var taskId = TaskId.Create("rehydrate-completed");

        // Populate storage directly, bypassing the runtime, to simulate state left behind by a previous instance
        // for a task which has no in-memory handle in *this* instance.
        var state = fixture.Storage.GetOrCreateTask(taskId, null);
        fixture.Storage.SetResponse(taskId, state, DurableTaskResponse.FromResult("done"));

        var handle = fixture.Runtime.GetScheduledTaskHandle(taskId);
        var response = await handle.PollAsync(new PollingOptions { PollTimeout = TimeSpan.Zero }, CancellationToken.None);

        Assert.True(response.IsCompleted);
        Assert.Equal("done", response.GetResult<string>());
    }

    [Fact]
    public async Task GetScheduledTaskHandle_RehydratesIncompleteTaskFromStorage_AsNotRunning()
    {
        var fixture = CreateFixture();
        var taskId = TaskId.Create("rehydrate-pending");

        // A task exists in storage (e.g. it was created via SignalCancellationAsync's tombstone path or GetOrCreateTask)
        // but has no response yet and no in-memory handle.
        fixture.Storage.GetOrCreateTask(taskId, null);

        var handle = fixture.Runtime.GetScheduledTaskHandle(taskId);
        var response = await handle.PollAsync(new PollingOptions { PollTimeout = TimeSpan.Zero }, CancellationToken.None);

        Assert.False(response.IsCompleted);
    }

    [Fact]
    public async Task CancelRemoteAsync_AfterPersistedIntentIsNotDelivered_RestartResendsUntilAcknowledged()
    {
        var fixture = CreateFixture(withTransport: true);
        var taskId = TaskId.Create("recoverable-cancellation");
        var target = GrainId.Create("remote-target", "1");
        fixture.Storage.GetOrCreateTask(taskId, request: null);

        await fixture.Runtime.CancelRemoteAsync(taskId, target, CancellationToken.None);
        await fixture.Storage.WriteAsync(CancellationToken.None);
        Assert.True(fixture.Storage.TryGetTask(taskId, out var persisted));
        Assert.NotNull(persisted.CancellationRequestedAt);
        Assert.Equal(target, Assert.IsType<DurableTaskState>(persisted).PendingCancellationDestination);

        // The journal commit succeeded, but delivery did not occur before the activation was lost.
        fixture.Transport!.Cancellations.Clear();
        await fixture.Storage.ReadAsync(CancellationToken.None);
        var restarted = CreateSecondRuntime(fixture);

        await restarted.ResumePendingTasksAsync(CancellationToken.None);

        var resent = Assert.Single(fixture.Transport.Cancellations);
        Assert.Equal((fixture.GrainId, target, taskId), resent);

        await restarted.AcceptCancellationAcknowledgementAsync(
            taskId,
            target,
            DurableTaskResponse.FromException(new OperationCanceledException()),
            CancellationToken.None);
        fixture.Transport.Cancellations.Clear();
        await CreateSecondRuntime(fixture).ResumePendingTasksAsync(CancellationToken.None);

        Assert.Empty(fixture.Transport.Cancellations);
        Assert.True(fixture.Storage.TryGetTask(taskId, out var acknowledged));
        Assert.True(acknowledged.Result!.IsCompleted);
        Assert.Equal(DurableTaskStatus.Canceled, acknowledged.Result.Status);
        Assert.True(Assert.IsType<DurableTaskState>(acknowledged).PendingCancellationDestination.IsDefault);
    }

    [Fact]
    public async Task CancellationAcknowledgement_UsesReceiverTerminalResponse()
    {
        var fixture = CreateFixture(withTransport: true);
        var taskId = TaskId.Create("completed-before-cancellation-ack");
        var target = GrainId.Create("remote-target", "completed");
        fixture.Storage.GetOrCreateTask(taskId, request: null);
        await fixture.Runtime.CancelRemoteAsync(taskId, target, CancellationToken.None);
        var authoritative = DurableTaskResponse.FromResult(73);

        await fixture.Runtime.AcceptCancellationAcknowledgementAsync(
            taskId,
            target,
            authoritative,
            CancellationToken.None);
        await fixture.Runtime.AcceptResponseAsync(taskId, target, authoritative, CancellationToken.None);

        Assert.True(fixture.Storage.TryGetTask(taskId, out var state));
        Assert.Equal(73, state.Result!.GetResult<int>());
        Assert.True(Assert.IsType<DurableTaskState>(state).PendingCancellationDestination.IsDefault);
    }

    [Fact]
    public async Task AcceptResponse_UnknownTask_RejectsCompletion()
    {
        var fixture = CreateFixture();
        var taskId = TaskId.Create("unknown-completion");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Runtime.AcceptResponseAsync(
                taskId,
                fixture.GrainId,
                DurableTaskResponse.Completed,
                CancellationToken.None).AsTask());

        Assert.Contains("unknown durable task", exception.Message, StringComparison.Ordinal);
        Assert.False(fixture.Storage.TryGetTask(taskId, out _));
    }

    [Fact]
    public async Task AcceptResponse_WrongRemoteSender_RejectsCompletion()
    {
        var fixture = CreateFixture();
        var taskId = TaskId.Create("wrong-completion-sender");
        var expectedSender = GrainId.Create("remote-target", "expected");
        var state = fixture.Storage.GetOrCreateTask(taskId, request: null);
        fixture.Storage.SetRemoteTarget(taskId, state, expectedSender);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Runtime.AcceptResponseAsync(
                taskId,
                GrainId.Create("remote-target", "other"),
                DurableTaskResponse.Completed,
                CancellationToken.None).AsTask());

        Assert.Contains(expectedSender.ToString(), exception.Message, StringComparison.Ordinal);
        Assert.Null(state.Result);
    }

    [Fact]
    public async Task RecoveredRemoteChild_RetainsCancellationTarget()
    {
        var fixture = CreateFixture(withTransport: true);
        var taskId = TaskId.Create("remote-child-recovery");
        var target = GrainId.Create("remote-target", "recovered");
        var state = fixture.Storage.GetOrCreateTask(taskId, request: null);
        fixture.Storage.SetRemoteTarget(taskId, state, target);
        await fixture.Storage.WriteAsync(CancellationToken.None);
        await fixture.Storage.ReadAsync(CancellationToken.None);
        var restarted = CreateSecondRuntime(fixture);

        await restarted.GetScheduledTaskHandle(taskId).CancelAsync(CancellationToken.None);

        var cancellation = Assert.Single(fixture.Transport!.Cancellations);
        Assert.Equal((fixture.GrainId, target, taskId), cancellation);
    }

    [Fact]
    public async Task CustomStorage_RetainsPendingCancellationDestinationForRecovery()
    {
        var storage = new RpcTestDurableTaskGrainStorage();
        var grainContext = new TestGrainContext(GrainId.Create("custom-storage", "grain"));
        var transport = new RpcTestMessageTransport();
        var runtime = RpcTestRuntimeFactory.Create(storage, grainContext, transport);
        var taskId = TaskId.Create("custom-storage-cancellation");
        var target = GrainId.Create("remote-target", "custom");
        storage.GetOrCreateTask(taskId, request: null);

        await runtime.CancelRemoteAsync(taskId, target, CancellationToken.None);
        Assert.Equal(1, storage.WriteAsyncCallCount);
        transport.Cancellations.Clear();
        var restarted = RpcTestRuntimeFactory.Create(storage, grainContext, transport);
        await restarted.ResumePendingTasksAsync(CancellationToken.None);

        Assert.Equal((grainContext.GrainId, target, taskId), Assert.Single(transport.Cancellations));
        Assert.Equal(2, storage.WriteAsyncCallCount);
    }

    [Fact]
    public async Task ScheduleAsync_TaskIdReuseWhileRunning_AcceptsEquivalentAndRejectsConflictBeforeMutation()
    {
        var fixture = CreateFixture(withTransport: true);
        var taskId = TaskId.Create("running-collision");
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = new RuntimeTestDurableTaskRequest(() => DurableTask.Run<int>(_ => completion.Task))
        {
            Context = new DurableTaskRequestContext { TargetId = fixture.GrainId },
        };
        await ((IDurableTaskServer)fixture.Runtime).ScheduleAsync(taskId, original, CancellationToken.None);

        var observer = GrainId.Create("observer", "equivalent");
        var equivalent = new RuntimeTestDurableTaskRequest(
            () => throw new InvalidOperationException("An equivalent retry must not invoke a second request."))
        {
            Context = new DurableTaskRequestContext { CallerId = observer, TargetId = fixture.GrainId },
        };
        var retryResponse = await ((IDurableTaskServer)fixture.Runtime).ScheduleAsync(taskId, equivalent, CancellationToken.None);
        Assert.Same(DurableTaskResponse.Subscribed, retryResponse);

        var conflictObserver = GrainId.Create("observer", "conflict");
        var conflict = new RuntimeTestDurableTaskRequest(methodName: "DifferentMethod")
        {
            Context = new DurableTaskRequestContext { CallerId = conflictObserver, TargetId = fixture.GrainId },
        };
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await ((IDurableTaskServer)fixture.Runtime).ScheduleAsync(taskId, conflict, CancellationToken.None));

        Assert.Contains(taskId.ToString(), exception.Message);
        Assert.True(fixture.Storage.TryGetTask(taskId, out var state));
        Assert.Same(original, state.Request);
        Assert.Contains(observer, state.CompletionDestinations);
        Assert.DoesNotContain(conflictObserver, state.CompletionDestinations);
        completion.SetResult(1);
        await fixture.Runtime.GetScheduledTaskHandle(taskId).WaitAsync(BoundedWait());
    }

    [Fact]
    public async Task ScheduleAsync_CompletedTaskIdReuse_ReturnsEquivalentResultAndRejectsConflict()
    {
        var fixture = CreateFixture();
        var taskId = TaskId.Create("completed-collision");
        var original = new RuntimeTestDurableTaskRequest(() => DurableTask.FromResult(17))
        {
            Context = new DurableTaskRequestContext { TargetId = fixture.GrainId },
        };
        await ((IDurableTaskServer)fixture.Runtime).ScheduleAsync(taskId, original, CancellationToken.None);
        await fixture.Runtime.GetScheduledTaskHandle(taskId).WaitAsync(BoundedWait());

        var equivalent = new RuntimeTestDurableTaskRequest
        {
            Context = new DurableTaskRequestContext { TargetId = fixture.GrainId },
        };
        var response = await ((IDurableTaskServer)fixture.Runtime).ScheduleAsync(taskId, equivalent, CancellationToken.None);
        Assert.Equal(17, response.GetResult<int>());

        var conflict = new RuntimeTestDurableTaskRequest(interfaceName: "IConflictingInterface")
        {
            Context = new DurableTaskRequestContext { TargetId = fixture.GrainId },
        };
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await ((IDurableTaskServer)fixture.Runtime).ScheduleAsync(taskId, conflict, CancellationToken.None));
        Assert.Equal(0, equivalent.CreateTaskCallCount);
        Assert.Equal(0, conflict.CreateTaskCallCount);
    }

    [Fact]
    public async Task ScheduleAsync_RequestlessTaskIdReuse_IsRejected()
    {
        var fixture = CreateFixture();
        var taskId = TaskId.Create("requestless-collision");
        fixture.Storage.GetOrCreateTask(taskId, request: null);
        var request = new RuntimeTestDurableTaskRequest
        {
            Context = new DurableTaskRequestContext { TargetId = fixture.GrainId },
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await ((IDurableTaskServer)fixture.Runtime).ScheduleAsync(
                taskId,
                request,
                CancellationToken.None));

        Assert.Contains("local, delay, or outbound", exception.Message);
        Assert.Equal(0, request.CreateTaskCallCount);
    }

    [Fact]
    public async Task ScheduleAsync_CanceledRequestlessNonTombstone_IsRejected()
    {
        var fixture = CreateFixture();
        var taskId = TaskId.Create("canceled-requestless-collision");
        var state = fixture.Storage.GetOrCreateTask(taskId, request: null);
        fixture.Storage.RequestCancellation(taskId, state);
        var request = new RuntimeTestDurableTaskRequest
        {
            Context = new DurableTaskRequestContext { TargetId = fixture.GrainId },
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await ((IDurableTaskServer)fixture.Runtime).ScheduleAsync(
                taskId,
                request,
                CancellationToken.None));
    }

    [Fact]
    public async Task ScheduleAsync_RecoveredTaskIdReuse_UsesSerializedArrayAndRecordEquivalenceAndRejectsConflict()
    {
        var fixture = CreateFixture();
        var taskId = TaskId.Create("recovered-collision");
        var persisted = new RuntimeTestDurableTaskRequest(
            () => DurableTask.FromResult(29),
            arguments:
            [
                new[] { 1, 2, 3 },
                new RuntimeTestComplexArgument { Name = "persisted", Values = [4, 5] },
            ])
        {
            Context = new DurableTaskRequestContext { TargetId = fixture.GrainId },
        };
        fixture.Storage.GetOrCreateTask(taskId, persisted);
        await fixture.Storage.WriteAsync(CancellationToken.None);
        await fixture.Storage.ReadAsync(CancellationToken.None);
        var restarted = CreateSecondRuntime(fixture);

        var equivalent = new RuntimeTestDurableTaskRequest(
            arguments:
            [
                new[] { 1, 2, 3 },
                new RuntimeTestComplexArgument { Name = "persisted", Values = [4, 5] },
            ])
        {
            Context = new DurableTaskRequestContext { TargetId = fixture.GrainId },
        };
        await ((IDurableTaskServer)restarted).ScheduleAsync(taskId, equivalent, CancellationToken.None);
        var response = await restarted.GetScheduledTaskHandle(taskId).WaitAsync(BoundedWait());

        Assert.Equal(29, response.GetResult<int>());
        Assert.Equal(1, persisted.CreateTaskCallCount);
        Assert.Equal(0, equivalent.CreateTaskCallCount);

        var conflict = new RuntimeTestDurableTaskRequest(
            arguments:
            [
                new[] { 1, 2, 3 },
                new RuntimeTestComplexArgument { Name = "conflict", Values = [4, 5] },
            ])
        {
            Context = new DurableTaskRequestContext { TargetId = fixture.GrainId },
        };
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await ((IDurableTaskServer)restarted).ScheduleAsync(taskId, conflict, CancellationToken.None));
        Assert.Equal(0, conflict.CreateTaskCallCount);
    }

    [Fact]
    public async Task ResumePendingTasksAsync_RestoresApplicationContextExcludesReservedEntriesAndRestoresAmbientContext()
    {
        var fixture = CreateFixture();
        var taskId = TaskId.Create("request-context-recovery");
        RequestContext.Clear();
        try
        {
            var scheduledReentrancyId = Guid.NewGuid();
            RequestContext.Set("tenant", "scheduled-tenant");
            RequestContext.ReentrancyId = scheduledReentrancyId;
            RequestContext.Set("Ping", true);
            RequestContext.Set("Orleans.DurableJobs.TurnIsolation", "scheduled-owner");
            var capturedValues = DurableTaskRequestContext.CaptureRequestContext(fixture.Shared.Serializer)!;
            Assert.DoesNotContain("#CCR", capturedValues.Keys);
            Assert.DoesNotContain("Ping", capturedValues.Keys);
            Assert.DoesNotContain("Orleans.DurableJobs.TurnIsolation", capturedValues.Keys);

            // Defensively verify that framework values from older/corrupt persisted state are not replayed either.
            var values = new Dictionary<string, byte[]>(capturedValues, StringComparer.Ordinal)
            {
                ["#CCR"] = fixture.Shared.Serializer.SerializeToArray<object>(scheduledReentrancyId),
                ["Ping"] = fixture.Shared.Serializer.SerializeToArray<object>(true),
                ["Orleans.DurableJobs.TurnIsolation"] = fixture.Shared.Serializer.SerializeToArray<object>("scheduled-owner"),
            };
            RequestContext.Clear();
            string? observedTenant = null;
            var observedReentrancyId = Guid.Empty;
            object? observedPing = null;
            object? observedTurnIsolation = null;
            var request = new RuntimeTestDurableTaskRequest(() => DurableTask.Run(_ =>
            {
                observedTenant = RequestContext.Get("tenant") as string;
                observedReentrancyId = RequestContext.ReentrancyId;
                observedPing = RequestContext.Get("Ping");
                observedTurnIsolation = RequestContext.Get("Orleans.DurableJobs.TurnIsolation");
                RequestContext.Set("tenant", "invocation-mutated");
                return 41;
            }))
            {
                Context = new DurableTaskRequestContext { TargetId = fixture.GrainId, Values = values },
            };
            fixture.Storage.GetOrCreateTask(taskId, request);
            await fixture.Storage.WriteAsync(CancellationToken.None);
            await fixture.Storage.ReadAsync(CancellationToken.None);
            var ambientReentrancyId = Guid.NewGuid();
            RequestContext.Set("tenant", "ambient-tenant");
            RequestContext.ReentrancyId = ambientReentrancyId;
            RequestContext.Set("Ping", "ambient-ping");
            RequestContext.Set("Orleans.DurableJobs.TurnIsolation", "ambient-owner");

            var restarted = CreateSecondRuntime(fixture);
            await restarted.ResumePendingTasksAsync(CancellationToken.None);
            var response = await restarted.GetScheduledTaskHandle(taskId).WaitAsync(BoundedWait());

            Assert.Equal(41, response.GetResult<int>());
            Assert.Equal("scheduled-tenant", observedTenant);
            Assert.Equal(Guid.Empty, observedReentrancyId);
            Assert.Null(observedPing);
            Assert.Equal("ambient-owner", observedTurnIsolation);
            Assert.Equal("ambient-tenant", RequestContext.Get("tenant"));
            Assert.Equal(ambientReentrancyId, RequestContext.ReentrancyId);
            Assert.Equal("ambient-ping", RequestContext.Get("Ping"));
            Assert.Equal("ambient-owner", RequestContext.Get("Orleans.DurableJobs.TurnIsolation"));
        }
        finally
        {
            RequestContext.Clear();
        }
    }

    [Fact]
    public async Task ScheduleAsync_InitialExecutionUsesPersistedApplicationContextWithoutReservedMarkers()
    {
        var fixture = CreateFixture();
        var taskId = TaskId.Create("initial-request-context");
        string? observedTenant = null;
        var observedReentrancyId = Guid.NewGuid();
        var request = new RuntimeTestDurableTaskRequest(() => DurableTask.Run(_ =>
        {
            observedTenant = RequestContext.Get("tenant") as string;
            observedReentrancyId = RequestContext.ReentrancyId;
        }))
        {
            Context = new DurableTaskRequestContext
            {
                TargetId = fixture.GrainId,
                Values = new Dictionary<string, byte[]>(StringComparer.Ordinal)
                {
                    ["tenant"] = fixture.Shared.Serializer.SerializeToArray<object>("persisted-tenant"),
                    ["#CCR"] = fixture.Shared.Serializer.SerializeToArray<object>(Guid.NewGuid()),
                },
            },
        };
        RequestContext.Clear();
        try
        {
            RequestContext.Set("tenant", "ambient-tenant");
            RequestContext.ReentrancyId = Guid.NewGuid();

            await ((IDurableTaskServer)fixture.Runtime).ScheduleAsync(taskId, request, CancellationToken.None);
            _ = await fixture.Runtime.GetScheduledTaskHandle(taskId).WaitAsync(BoundedWait());

            Assert.Equal("persisted-tenant", observedTenant);
            Assert.Equal(Guid.Empty, observedReentrancyId);
        }
        finally
        {
            RequestContext.Clear();
        }
    }

    [Fact]
    public async Task ScheduleChildAsync_ReplacesRehydratedLocalHandle()
    {
        var fixture = CreateFixture();
        var taskId = TaskId.Create("root").Child("local-child");
        fixture.Storage.GetOrCreateTask(taskId, request: null);
        await fixture.Storage.WriteAsync(CancellationToken.None);
        var rehydrated = fixture.Runtime.GetScheduledTaskHandle(taskId);

        var scheduled = await fixture.Runtime.ScheduleChildAsync(
            taskId,
            DurableTask.Run<int>(static _ => 42),
            CancellationToken.None);
        var response = await scheduled.WaitAsync(BoundedWait());

        Assert.NotSame(rehydrated, scheduled);
        Assert.Equal(42, response.GetResult<int>());
    }

    [Fact]
    public void CaptureRequestContext_ExceedingEntryLimitFailsWithoutDroppingValues()
    {
        var fixture = CreateFixture();
        RequestContext.Clear();
        try
        {
            for (var i = 0; i <= DurableTaskRequestContext.MaxEntryCount; i++)
            {
                RequestContext.Set($"key-{i}", i);
            }

            var exception = Assert.Throws<InvalidOperationException>(
                () => DurableTaskRequestContext.CaptureRequestContext(fixture.Shared.Serializer));
            Assert.Contains(DurableTaskRequestContext.MaxEntryCount.ToString(), exception.Message);
        }
        finally
        {
            RequestContext.Clear();
        }
    }

    [Fact]
    public async Task ScheduleAsync_OversizedReservedContextFailsBeforeStorageMutation()
    {
        var fixture = CreateFixture();
        var request = new RuntimeTestDurableTaskRequest
        {
            Context = new DurableTaskRequestContext
            {
                TargetId = fixture.GrainId,
                Values = new Dictionary<string, byte[]>(StringComparer.Ordinal)
                {
                    ["#CCR"] = new byte[DurableTaskRequestContext.MaxSerializedValueLength + 1],
                },
            },
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await ((IDurableTaskServer)fixture.Runtime).ScheduleAsync(
                TaskId.Create("oversized-reserved-context"),
                request,
                CancellationToken.None));

        Assert.Contains(DurableTaskRequestContext.MaxSerializedValueLength.ToString(), exception.Message);
        Assert.Empty(fixture.Storage.Tasks);
    }

    [Fact]
    public async Task StopAsync_StopsAdmissionHandsOffPendingRequestAndPreventsOldActivationFromOutlivingStop()
    {
        var fixture = CreateFixture();
        var taskId = TaskId.Create("deactivation-handoff");
        var firstAttemptStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var request = new RuntimeTestDurableTaskRequest(() =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                return DurableTask.Run(async cancellationToken =>
                {
                    firstAttemptStarted.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                });
            }

            return DurableTask.FromResult(53);
        })
        {
            Context = new DurableTaskRequestContext { TargetId = fixture.GrainId },
        };
        await ((IDurableTaskServer)fixture.Runtime).ScheduleAsync(taskId, request, CancellationToken.None);
        await firstAttemptStarted.Task.WaitAsync(BoundedWait());

        await fixture.Runtime.StopAsync(BoundedWait());

        Assert.True(fixture.Storage.TryGetTask(taskId, out var handedOff));
        Assert.Null(handedOff.Result);
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await ((IDurableTaskServer)fixture.Runtime).ScheduleAsync(
                TaskId.Create("rejected-after-stop"),
                new RuntimeTestDurableTaskRequest { Context = new DurableTaskRequestContext { TargetId = fixture.GrainId } },
                CancellationToken.None));

        var restarted = CreateSecondRuntime(fixture);
        await restarted.ResumePendingTasksAsync(CancellationToken.None);
        var response = await restarted.GetScheduledTaskHandle(taskId).WaitAsync(BoundedWait());

        Assert.Equal(53, response.GetResult<int>());
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task StopAsync_NonCooperativeWorkBlocksTeardownUntilExecutionIsTerminal()
    {
        var fixture = CreateFixture();
        fixture.Shared.DeactivationDrainTimeout = TimeSpan.FromMinutes(1);
        var taskId = TaskId.Create("non-cooperative-stop");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new RuntimeTestDurableTaskRequest(() => DurableTask.Run(async _ =>
        {
            started.TrySetResult();
            await release.Task;
        }))
        {
            Context = new DurableTaskRequestContext { TargetId = fixture.GrainId },
        };
        await ((IDurableTaskServer)fixture.Runtime).ScheduleAsync(taskId, request, CancellationToken.None);
        await started.Task.WaitAsync(BoundedWait());

        var timedStop = fixture.Runtime.StopAsync(CancellationToken.None);
        fixture.TimeProvider.Advance(fixture.Shared.DeactivationDrainTimeout);
        await Task.Yield();
        Assert.False(timedStop.IsCompleted);

        var runningAfterTimeout = await ((IDurableTaskGrainExtension)fixture.Runtime)
            .GetRunningTasksAsync()
            .ToListAsync();
        Assert.Contains(taskId, runningAfterTimeout);
        Assert.True(fixture.Storage.TryGetTask(taskId, out var pendingAfterTimeout));
        Assert.Null(pendingAfterTimeout.Result);

        release.SetResult();
        var response = await fixture.Runtime.GetScheduledTaskHandle(taskId).WaitAsync(BoundedWait());
        Assert.True(response.IsCompleted);

        await timedStop.WaitAsync(BoundedWait());
        var runningAfterTerminalStop = await ((IDurableTaskGrainExtension)fixture.Runtime)
            .GetRunningTasksAsync()
            .ToListAsync();
        Assert.Empty(runningAfterTerminalStop);
    }

    private sealed class StubScheduledTaskHandle(TaskId taskId) : IScheduledTaskHandle
    {
        public TaskId TaskId { get; } = taskId;
        public ValueTask<DurableTaskResponse> WaitAsync(CancellationToken cancellationToken) => new(DurableTaskResponse.Pending);
        public ValueTask<DurableTaskResponse> PollAsync(PollingOptions options, CancellationToken cancellationToken) => new(DurableTaskResponse.Pending);
        public ValueTask CancelAsync(CancellationToken cancellationToken) => default;
    }
}

internal static class AsyncEnumerableTestExtensions
{
    public static async Task<List<T>> ToListAsync<T>(this IAsyncEnumerable<T> source)
    {
        var results = new List<T>();
        await foreach (var item in source)
        {
            results.Add(item);
        }

        return results;
    }
}
