#nullable enable
using System;
using System.Distributed.DurableTasks;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;
using Xunit;

namespace Orleans.DurableTasks.Tests;

/// <summary>
/// Unit tests for <see cref="DurableTaskGrainParticipant"/>.
/// </summary>
public class DurableTaskGrainParticipantTests
{
    private static (DurableTaskGrainParticipant Participant, TestGrainContext GrainContext, RpcTestDurableTaskGrainStorage Storage) CreateParticipant()
    {
        var grainId = GrainId.Create("rpc-participant-grain", Guid.NewGuid().ToString("N"));
        var grainContext = new TestGrainContext(grainId);
        var storage = new RpcTestDurableTaskGrainStorage();
        var runtime = RpcTestRuntimeFactory.Create(storage, grainContext);
        var participant = new DurableTaskGrainParticipant(runtime, grainContext);
        return (participant, grainContext, storage);
    }

    [Fact]
    public void Initialize_SubscribesLifecycleObserverWithExactObserverNameAndActivateStage()
    {
        var (participant, grainContext, _) = CreateParticipant();
        var lifecycle = (TestGrainLifecycle)grainContext.ObservableLifecycle;
        Assert.Empty(lifecycle.Subscriptions);

        participant.Initialize();

        var subscription = Assert.Single(lifecycle.Subscriptions);
        Assert.Equal(nameof(DurableTaskGrainParticipant), subscription.ObserverName);
        Assert.Equal(GrainLifecycleStage.Activate, subscription.Stage);
    }

    [Fact]
    public void Initialize_CalledTwice_RecordsTwoIndependentSubscriptions()
    {
        // Pins down that Initialize() does not attempt to be idempotent/de-duplicate; it is the lifecycle's
        // responsibility (not the participant's) to avoid duplicate subscriptions.
        var (participant, grainContext, _) = CreateParticipant();
        var lifecycle = (TestGrainLifecycle)grainContext.ObservableLifecycle;

        participant.Initialize();
        participant.Initialize();

        Assert.Equal(2, lifecycle.Subscriptions.Count);
        Assert.All(lifecycle.Subscriptions, s =>
        {
            Assert.Equal(nameof(DurableTaskGrainParticipant), s.ObserverName);
            Assert.Equal(GrainLifecycleStage.Activate, s.Stage);
        });
    }

    [Fact]
    public async Task OnStart_DelegatesToRuntimeResumePendingTasksAsync_ResumingAnIncompleteTask()
    {
        var (participant, _, storage) = CreateParticipant();
        var taskId = TaskId.Create("resumable-workflow/step-1");
        var request = new RpcTestDurableTaskRequest { ResultValue = 123 };
        storage.GetOrCreateTask(taskId, request);

        // Sanity-check the precondition: the task is not yet completed before OnStart runs.
        Assert.True(storage.TryGetTask(taskId, out var preState));
        Assert.Null(preState!.Result);
        Assert.Equal(0, request.CreateTaskCallCount);

        await participant.OnStart(CancellationToken.None);

        Assert.True(storage.TryGetTask(taskId, out var postState));
        Assert.NotNull(postState!.Result);
        Assert.True(postState.Result!.IsCompleted);
        Assert.Equal(123, postState.Result.GetResult<int>());
        Assert.Equal(1, request.CreateTaskCallCount);
    }

    [Fact]
    public async Task OnStart_WithNoPendingTasks_CompletesWithoutError()
    {
        var (participant, _, storage) = CreateParticipant();

        await participant.OnStart(CancellationToken.None);

        Assert.Empty(storage.Tasks);
    }

    [Fact]
    public async Task OnStart_SkipsAlreadyCompletedTasks()
    {
        var (participant, _, storage) = CreateParticipant();
        var taskId = TaskId.Create("resumable-workflow/step-2");
        var request = new RpcTestDurableTaskRequest { ResultValue = 55 };
        var state = storage.GetOrCreateTask(taskId, request);
        storage.SetResponse(taskId, state, DurableTaskResponse.FromResult(55));
        Assert.True(storage.TryGetTask(taskId, out var preState));
        Assert.True(preState!.Result!.IsCompleted);
        Assert.Equal(55, preState.Result.GetResult<int>());

        await participant.OnStart(CancellationToken.None);

        // CreateTask must not be invoked again for an already-completed task.
        Assert.Equal(0, request.CreateTaskCallCount);
        Assert.True(storage.TryGetTask(taskId, out var postState));
        Assert.True(postState!.Result!.IsCompleted);
        Assert.Equal(55, postState.Result.GetResult<int>());
    }

    [Fact]
    public void OnStop_ReturnsTheCompletedTaskSingletonWithNoSideEffects()
    {
        var (participant, _, storage) = CreateParticipant();

        var task = participant.OnStop(CancellationToken.None);

        Assert.Same(Task.CompletedTask, task);
        Assert.True(task.IsCompletedSuccessfully);
        Assert.Empty(storage.Tasks);
        Assert.Equal(0, storage.WriteAsyncCallCount);
    }
}
