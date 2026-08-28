#nullable enable
using System;
using System.Collections.Generic;
using System.Distributed.DurableTasks;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.Runtime;
using Orleans.Runtime.DurableTasks;
using Orleans.Serialization;
using Xunit;

namespace Orleans.DurableTasks.Tests;

[TestCategory("BVT")]
public class VolatileDurableTaskGrainStorageTests
{
    private static (VolatileDurableTaskGrainStorage Storage, FakeTimeProvider Time) CreateStorage()
    {
        var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var storageCopier = services.GetRequiredService<DeepCopier<Dictionary<TaskId, DurableTaskState>>>();
        var stateCopier = services.GetRequiredService<DeepCopier<DurableTaskState>>();
        var time = new FakeTimeProvider(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
        return (new VolatileDurableTaskGrainStorage(storageCopier, stateCopier, time), time);
    }

    [Fact]
    public async Task WriteAsync_CommitsConfiguredDurableMessageTransport()
    {
        var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var transport = new RecordingDurableTaskMessageTransport();
        var storage = new VolatileDurableTaskGrainStorage(
            services.GetRequiredService<DeepCopier<Dictionary<TaskId, DurableTaskState>>>(),
            services.GetRequiredService<DeepCopier<DurableTaskState>>(),
            TimeProvider.System,
            transport);

        await storage.WriteAsync(CancellationToken.None);

        Assert.Equal(1, transport.CommitCount);
    }

    [Fact]
    public async Task WriteAsync_EnlistedSnapshotCommitsOrRollsBackWithHandlerTransaction()
    {
        var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var transport = new RecordingDurableTaskMessageTransport();
        var storage = new VolatileDurableTaskGrainStorage(
            services.GetRequiredService<DeepCopier<Dictionary<TaskId, DurableTaskState>>>(),
            services.GetRequiredService<DeepCopier<DurableTaskState>>(),
            TimeProvider.System,
            transport);
        var taskId = TaskId.Create("enlisted");
        var state = storage.GetOrCreateTask(taskId, request: null);
        storage.SetResponse(taskId, state, DurableTaskResponse.FromResult(1));
        await storage.WriteAsync(CancellationToken.None);

        transport.EnlistWrites = true;
        Assert.True(storage.TryGetTask(taskId, out state));
        storage.SetResponse(taskId, state, DurableTaskResponse.FromResult(2));
        await storage.WriteAsync(CancellationToken.None);
        transport.CompleteTransaction(committed: false);
        Assert.True(storage.TryGetTask(taskId, out var rolledBack));
        Assert.Equal(1, rolledBack.Result!.GetResult<int>());

        storage.SetResponse(taskId, rolledBack, DurableTaskResponse.FromResult(3));
        await storage.WriteAsync(CancellationToken.None);
        transport.CompleteTransaction(committed: true);
        await storage.ReadAsync(CancellationToken.None);
        Assert.True(storage.TryGetTask(taskId, out var committed));
        Assert.Equal(3, committed.Result!.GetResult<int>());
    }

    [Fact]
    public async Task WriteAsync_FailedMessageCommitDoesNotPublishPreparedSnapshot()
    {
        var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var transport = new RecordingDurableTaskMessageTransport();
        var storage = new VolatileDurableTaskGrainStorage(
            services.GetRequiredService<DeepCopier<Dictionary<TaskId, DurableTaskState>>>(),
            services.GetRequiredService<DeepCopier<DurableTaskState>>(),
            TimeProvider.System,
            transport);
        var taskId = TaskId.Create("failed-commit");
        var state = storage.GetOrCreateTask(taskId, request: null);
        storage.SetResponse(taskId, state, DurableTaskResponse.FromResult(1));
        await storage.WriteAsync(CancellationToken.None);
        Assert.True(storage.TryGetTask(taskId, out state));
        storage.SetResponse(taskId, state, DurableTaskResponse.FromResult(2));
        transport.NextCommitException = new IOException("Expected commit failure.");

        await Assert.ThrowsAsync<IOException>(
            () => storage.WriteAsync(CancellationToken.None).AsTask());
        await storage.ReadAsync(CancellationToken.None);

        Assert.True(storage.TryGetTask(taskId, out var recovered));
        Assert.Equal(1, recovered.Result!.GetResult<int>());
    }

    [Fact]
    public async Task ArmedNextCommitEnlistment_IsNotRolledBackByScopeDisposal()
    {
        var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var transport = new RecordingDurableTaskMessageTransport();
        var storage = new VolatileDurableTaskGrainStorage(
            services.GetRequiredService<DeepCopier<Dictionary<TaskId, DurableTaskState>>>(),
            services.GetRequiredService<DeepCopier<DurableTaskState>>(),
            TimeProvider.System,
            transport);
        var taskId = TaskId.Create("armed-enlistment");
        var state = storage.GetOrCreateTask(taskId, request: null);
        storage.SetResponse(taskId, state, DurableTaskResponse.FromResult(1));
        await storage.WriteAsync(CancellationToken.None);
        Assert.True(storage.TryGetTask(taskId, out state));
        storage.SetResponse(taskId, state, DurableTaskResponse.FromResult(2));

        using (storage.EnlistWithNextMessageCommit())
        {
            await storage.WriteAsync(CancellationToken.None);
        }

        Assert.True(storage.TryGetTask(taskId, out var stillWorking));
        Assert.Equal(2, stillWorking.Result!.GetResult<int>());
        await transport.ScheduleResumeAsync(
            GrainId.Create("test", "target"),
            taskId,
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        await storage.ReadAsync(CancellationToken.None);
        Assert.True(storage.TryGetTask(taskId, out var committed));
        Assert.Equal(2, committed.Result!.GetResult<int>());
    }

    [Fact]
    public async Task NextCommitEnlistment_CapturesMutationsAtCommitTime()
    {
        var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var transport = new RecordingDurableTaskMessageTransport();
        var storage = new VolatileDurableTaskGrainStorage(
            services.GetRequiredService<DeepCopier<Dictionary<TaskId, DurableTaskState>>>(),
            services.GetRequiredService<DeepCopier<DurableTaskState>>(),
            TimeProvider.System,
            transport);
        var taskId = TaskId.Create("commit-time-snapshot");
        var state = storage.GetOrCreateTask(taskId, request: null);
        storage.SetResponse(taskId, state, DurableTaskResponse.FromResult(1));
        await storage.WriteAsync(CancellationToken.None);

        using (storage.EnlistWithNextMessageCommit())
        {
            Assert.True(storage.TryGetTask(taskId, out state));
            storage.SetResponse(taskId, state, DurableTaskResponse.FromResult(2));
            await storage.WriteAsync(CancellationToken.None);
        }

        await transport.ScheduleResumeAsync(
            GrainId.Create("test", "target"),
            taskId,
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        await storage.ReadAsync(CancellationToken.None);
        Assert.True(storage.TryGetTask(taskId, out var committed));
        Assert.Equal(2, committed.Result!.GetResult<int>());
    }

    [Fact]
    public void AddOrUpdateTask_TryGetTask_RoundTrip_DeepCopyIsolation()
    {
        var (storage, time) = CreateStorage();
        var taskId = TaskId.Create("task-1");
        var destination = GrainId.Create("test-grain-type", "destination-1");

        var state = new DurableTaskState { CreatedAt = time.GetUtcNow() };
        state.CompletionDestinations.Add(destination);

        storage.AddOrUpdateTask(taskId, state);

        Assert.True(storage.TryGetTask(taskId, out var firstRead));
        var firstReadState = Assert.IsType<DurableTaskState>(firstRead);
        Assert.Equal(new[] { destination }, firstReadState.CompletionDestinations.ToArray());

        // Mutate the returned copy; the internal storage must not observe this mutation.
        var extraDestination = GrainId.Create("test-grain-type", "destination-2");
        firstReadState.CompletionDestinations.Add(extraDestination);

        Assert.True(storage.TryGetTask(taskId, out var secondRead));
        var secondReadState = Assert.IsType<DurableTaskState>(secondRead);
        Assert.DoesNotContain(extraDestination, secondReadState.CompletionDestinations);
        Assert.Single(secondReadState.CompletionDestinations);

        // Re-adding the mutated copy propagates the change.
        storage.AddOrUpdateTask(taskId, firstReadState);
        Assert.True(storage.TryGetTask(taskId, out var thirdRead));
        var thirdReadState = Assert.IsType<DurableTaskState>(thirdRead);
        Assert.Equal(2, thirdReadState.CompletionDestinations.Count);
        Assert.Contains(extraDestination, thirdReadState.CompletionDestinations);
    }

    [Fact]
    public void TryGetTask_MissingTask_ReturnsFalseAndNullState()
    {
        var (storage, _) = CreateStorage();
        Assert.False(storage.TryGetTask(TaskId.Create("does-not-exist"), out var state));
        Assert.Null(state);
    }

    [Fact]
    public void GetOrCreateTask_NewTask_SetsCreatedAtFromTimeProviderAndNullRequest()
    {
        var (storage, time) = CreateStorage();
        var taskId = TaskId.Create("new-task");

        var result = storage.GetOrCreateTask(taskId, request: null);

        Assert.Equal(time.GetUtcNow(), result.CreatedAt);
        Assert.Null(result.Request);
        Assert.Null(result.CompletedAt);
        Assert.Null(result.CancellationRequestedAt);
        Assert.Empty(result.CompletionDestinations);

        // GetOrCreateTask persists the newly-created state immediately (see the comment on
        // VolatileDurableTaskGrainStorage.GetOrCreateTask for why this is required), so it is visible to a
        // subsequent TryGetTask even without any further Set* mutation.
        Assert.True(storage.TryGetTask(taskId, out var persisted));
        Assert.Equal(time.GetUtcNow(), persisted!.CreatedAt);
    }

    [Fact]
    public void GetOrCreateTask_ExistingTask_ReturnsStoredStateIgnoringRequestArgument()
    {
        var (storage, time) = CreateStorage();
        var taskId = TaskId.Create("existing-task");
        var createdAt = time.GetUtcNow();

        storage.AddOrUpdateTask(taskId, new DurableTaskState { CreatedAt = createdAt });

        // Advance time so that, if GetOrCreateTask incorrectly treated this as "new", CreatedAt would differ.
        time.Advance(TimeSpan.FromHours(1));

        var request = Substitute.For<IDurableTaskRequest>();
        request.Context.Returns(new DurableTaskRequestContext { TargetId = GrainId.Create("grain-type", "target") });

        var result = storage.GetOrCreateTask(taskId, request);

        Assert.Equal(createdAt, result.CreatedAt);
        Assert.NotEqual(time.GetUtcNow(), result.CreatedAt);
    }

    [Fact]
    public void GetOrCreateTask_NewTask_RequestWithNullContext_ThrowsInvalidOperationException()
    {
        var (storage, _) = CreateStorage();
        var taskId = TaskId.Create("bad-request-task");

        var request = Substitute.For<IDurableTaskRequest>();
        request.Context.Returns((DurableTaskRequestContext?)null);

        var exception = Assert.Throws<InvalidOperationException>(() => storage.GetOrCreateTask(taskId, request));
        Assert.Contains("context", exception.Message, StringComparison.OrdinalIgnoreCase);

        // The failed creation attempt must not have persisted anything.
        Assert.False(storage.TryGetTask(taskId, out _));
    }

    [Fact]
    public void GetOrCreateTask_NewTask_RequestWithContext_DoesNotThrow()
    {
        var (storage, time) = CreateStorage();
        var taskId = TaskId.Create("good-request-task");

        // Use a genuinely serializable IDurableTaskRequest (not an NSubstitute proxy): GetOrCreateTask now
        // persists the new state immediately via AddOrUpdateTask, which deep-copies the DurableTaskState
        // (including its Request), and there is no registered copier for a dynamically-generated proxy type.
        var request = new TestDurableTaskRequest { Context = new DurableTaskRequestContext { TargetId = GrainId.Create("grain-type", "target") } };

        var result = storage.GetOrCreateTask(taskId, request);

        Assert.Same(request, result.Request);
        Assert.Equal(time.GetUtcNow(), result.CreatedAt);

        Assert.True(storage.TryGetTask(taskId, out var persisted));
        var persistedState = Assert.IsType<DurableTaskState>(persisted);
        var persistedRequest = Assert.IsType<TestDurableTaskRequest>(persistedState.Request);
        Assert.Equal(request.Context!.TargetId, persistedRequest.Context!.TargetId);
    }

    [Fact]
    public void SetRequest_UpdatesStoredRequest()
    {
        var (storage, time) = CreateStorage();
        var taskId = TaskId.Create("set-request-task");
        storage.AddOrUpdateTask(taskId, new DurableTaskState { CreatedAt = time.GetUtcNow() });
        Assert.True(storage.TryGetTask(taskId, out var state));

        var request = new TestDurableTaskRequest { Context = new DurableTaskRequestContext { TargetId = GrainId.Create("grain-type", "target") } };
        storage.SetRequest(taskId, state!, request);

        Assert.True(storage.TryGetTask(taskId, out var updated));
        var updatedState = Assert.IsType<DurableTaskState>(updated);
        Assert.NotNull(updatedState.Request);
        Assert.IsType<TestDurableTaskRequest>(updatedState.Request);
        Assert.Equal(request.Context!.TargetId, updatedState.Request!.Context!.TargetId);
    }

    [Fact]
    public void SetResponse_SetsResultAndCompletedAtFromTimeProvider()
    {
        var (storage, time) = CreateStorage();
        var taskId = TaskId.Create("set-response-task");
        storage.AddOrUpdateTask(taskId, new DurableTaskState { CreatedAt = time.GetUtcNow() });
        Assert.True(storage.TryGetTask(taskId, out var state));
        Assert.Null(state!.CompletedAt);

        time.Advance(TimeSpan.FromMinutes(5));
        storage.SetResponse(taskId, state, DurableTaskResponse.Completed);

        Assert.True(storage.TryGetTask(taskId, out var updated));
        var updatedState = Assert.IsType<DurableTaskState>(updated);
        Assert.Equal(time.GetUtcNow(), updatedState.CompletedAt);
        Assert.Same(DurableTaskResponse.Completed, updatedState.Result);
    }

    [Fact]
    public void AddCompletionDestination_And_ClearCompletionDestinations()
    {
        var (storage, time) = CreateStorage();
        var taskId = TaskId.Create("completion-destinations-task");
        storage.AddOrUpdateTask(taskId, new DurableTaskState { CreatedAt = time.GetUtcNow() });
        Assert.True(storage.TryGetTask(taskId, out var state));

        var destination1 = GrainId.Create("grain-type", "d1");
        var destination2 = GrainId.Create("grain-type", "d2");
        storage.AddCompletionDestination(taskId, state!, destination1);

        Assert.True(storage.TryGetTask(taskId, out var afterFirstAdd));
        Assert.Equal(new[] { destination1 }, Assert.IsType<DurableTaskState>(afterFirstAdd).CompletionDestinations.ToArray());

        storage.AddCompletionDestination(taskId, afterFirstAdd!, destination2);
        Assert.True(storage.TryGetTask(taskId, out var afterSecondAdd));
        var afterSecondAddState = Assert.IsType<DurableTaskState>(afterSecondAdd);
        Assert.Equal(2, afterSecondAddState.CompletionDestinations.Count);
        Assert.Contains(destination1, afterSecondAddState.CompletionDestinations);
        Assert.Contains(destination2, afterSecondAddState.CompletionDestinations);

        storage.ClearCompletionDestinations(taskId, afterSecondAddState);
        Assert.True(storage.TryGetTask(taskId, out var afterClear));
        Assert.Empty(Assert.IsType<DurableTaskState>(afterClear).CompletionDestinations);
    }

    [Fact]
    public void RequestCancellation_SetsCancellationRequestedAtFromTimeProvider()
    {
        var (storage, time) = CreateStorage();
        var taskId = TaskId.Create("cancellation-task");
        storage.AddOrUpdateTask(taskId, new DurableTaskState { CreatedAt = time.GetUtcNow() });
        Assert.True(storage.TryGetTask(taskId, out var state));
        Assert.Null(state!.CancellationRequestedAt);

        time.Advance(TimeSpan.FromSeconds(30));
        storage.RequestCancellation(taskId, state);

        Assert.True(storage.TryGetTask(taskId, out var updated));
        Assert.Equal(time.GetUtcNow(), Assert.IsType<DurableTaskState>(updated).CancellationRequestedAt);
    }

    [Fact]
    public void RemoveTask_RemovesExistingTaskAndReturnsFalseForMissingTask()
    {
        var (storage, time) = CreateStorage();
        var taskId = TaskId.Create("remove-task");
        storage.AddOrUpdateTask(taskId, new DurableTaskState { CreatedAt = time.GetUtcNow() });

        Assert.True(storage.RemoveTask(taskId));
        Assert.False(storage.TryGetTask(taskId, out _));
        Assert.False(storage.RemoveTask(taskId));
    }

    [Fact]
    public void Clear_RemovesAllTasksFromWorkingCopy()
    {
        var (storage, time) = CreateStorage();
        storage.AddOrUpdateTask(TaskId.Create("task-a"), new DurableTaskState { CreatedAt = time.GetUtcNow() });
        storage.AddOrUpdateTask(TaskId.Create("task-b"), new DurableTaskState { CreatedAt = time.GetUtcNow() });
        Assert.Equal(2, storage.Tasks.Count());

        storage.Clear();

        Assert.Empty(storage.Tasks);
    }

    [Fact]
    public void GetChildren_ReturnsOnlyDirectChildrenOfParent()
    {
        var (storage, time) = CreateStorage();
        var parent = TaskId.Create("parent-task");
        var child1 = parent.Child("child-1");
        var child2 = parent.Child("child-2");
        var grandchild = child1.Child("grandchild");
        var unrelated = TaskId.Create("unrelated-task");

        foreach (var id in new[] { parent, child1, child2, grandchild, unrelated })
        {
            storage.AddOrUpdateTask(id, new DurableTaskState { CreatedAt = time.GetUtcNow() });
        }

        var children = storage.GetChildren(parent).Select(entry => entry.Id).ToArray();

        Assert.Equal(2, children.Length);
        Assert.Contains(child1, children);
        Assert.Contains(child2, children);
        Assert.DoesNotContain(grandchild, children);
        Assert.DoesNotContain(unrelated, children);
        Assert.DoesNotContain(parent, children);
    }

    [Fact]
    public async System.Threading.Tasks.Task WriteAsync_Then_Clear_Then_ReadAsync_RestoresPersistedSnapshot()
    {
        var (storage, time) = CreateStorage();
        var taskId = TaskId.Create("persisted-task");
        storage.AddOrUpdateTask(taskId, new DurableTaskState { CreatedAt = time.GetUtcNow() });

        await storage.WriteAsync(default);

        storage.Clear();
        Assert.False(storage.TryGetTask(taskId, out _));

        await storage.ReadAsync(default);

        Assert.True(storage.TryGetTask(taskId, out _));
    }

    [Fact]
    public async System.Threading.Tasks.Task ReadAsync_DoesNotSeeChangesMadeAfterLastWriteAsync()
    {
        var (storage, time) = CreateStorage();
        var persistedTaskId = TaskId.Create("persisted-only-task");
        storage.AddOrUpdateTask(persistedTaskId, new DurableTaskState { CreatedAt = time.GetUtcNow() });
        await storage.WriteAsync(default);

        // A task added after the last WriteAsync should not survive a ReadAsync.
        var unpersistedTaskId = TaskId.Create("unpersisted-task");
        storage.AddOrUpdateTask(unpersistedTaskId, new DurableTaskState { CreatedAt = time.GetUtcNow() });

        await storage.ReadAsync(default);

        Assert.True(storage.TryGetTask(persistedTaskId, out _));
        Assert.False(storage.TryGetTask(unpersistedTaskId, out _));
    }

    private sealed class NotDurableTaskState : IDurableTaskState
    {
        public DurableTaskResponse? Result => null;
        public IReadOnlySet<GrainId> CompletionDestinations => new HashSet<GrainId>();
        public IDurableTaskRequest? Request => null;
        public DateTimeOffset? CompletedAt => null;
        public DateTimeOffset? CancellationRequestedAt => null;
        public DateTimeOffset CreatedAt => default;
        public GrainId RemoteTarget => default;
        public bool IsCancellationTombstone { get; set; }
        public GrainId PendingCancellationDestination { get; set; }
    }

    [Fact]
    public void SetRequest_WithForeignIDurableTaskStateImplementation_ThrowsArgumentException()
    {
        var (storage, _) = CreateStorage();
        var taskId = TaskId.Create("foreign-state-task");
        var foreignState = new NotDurableTaskState();
        var request = Substitute.For<IDurableTaskRequest>();

        var exception = Assert.Throws<ArgumentException>(() => storage.SetRequest(taskId, foreignState, request));
        Assert.Equal("state", exception.ParamName);
    }

    [Fact]
    public void SetResponse_WithForeignIDurableTaskStateImplementation_ThrowsArgumentException()
    {
        var (storage, _) = CreateStorage();
        var taskId = TaskId.Create("foreign-state-task-2");
        var foreignState = new NotDurableTaskState();

        var exception = Assert.Throws<ArgumentException>(() => storage.SetResponse(taskId, foreignState, DurableTaskResponse.Completed));
        Assert.Equal("state", exception.ParamName);
    }
}
