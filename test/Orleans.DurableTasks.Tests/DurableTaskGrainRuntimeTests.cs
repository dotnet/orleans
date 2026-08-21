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
        var shared = new DurableTaskGrainRuntimeShared(new TestGrainContextAccessor(grainContext), timeProvider, NullLogger<DurableTaskGrainRuntime>.Instance);
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

    #region 1. IDurableTaskServer.ScheduleAsync

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
        Assert.Equal(1, fixture.Transport.CommitCount);
    }

    #endregion

    #region 2. AcceptResponse + ResumePendingTasksAsync (recovery)

    [Fact]
    public async Task AcceptResponse_DuplicateCompletion_DoesNotOverwritePersistedOrInMemoryResult()
    {
        var fixture = CreateFixture();
        var taskId = TaskId.Create("duplicate-completion");
        fixture.Storage.GetOrCreateTask(taskId, request: null);
        var handle = fixture.Runtime.GetScheduledTaskHandle(taskId);

        fixture.Runtime.AcceptResponse(taskId, DurableTaskResponse.FromResult(1));
        fixture.Runtime.AcceptResponse(taskId, DurableTaskResponse.FromResult(2));

        var response = await handle.WaitAsync(CancellationToken.None);
        Assert.Equal(1, response.GetResult<int>());
        Assert.True(fixture.Storage.TryGetTask(taskId, out var state));
        Assert.Equal(1, state.Result!.GetResult<int>());
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
        Assert.Null(stateBeforeRestart.Result);

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

    #endregion

    #region 3. SetResponseAsync fan-out + PruneCompletedTasks retention

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
        Assert.Equal(1, fixture.Transport.CommitCount);

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

    #endregion

    #region 4. SignalCancellationAsync cascading cancellation

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

        // Cancellation was only *requested* (via the execution-context callback path): the underlying tasks used here
        // ignore their internal cancellation token, so none of them have actually completed yet.
        Assert.Null(parentState.Result);
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

    #endregion

    #region 5. ScheduleChildAsync branches

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

    #endregion

    #region 6. ScheduleRemoteAsync / CancelRemoteAsync / ScheduleDelayAsync via the fake transport

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
        Assert.Equal(1, fixture.Transport.CommitCount);
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
        Assert.Equal(1, fixture.Transport.CommitCount);
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

    #endregion

    #region 7. GetTasksAsync / GetRunningTasksAsync / GetScheduledTaskHandle accessors

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

    #endregion

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
