using System.Distributed.DurableTasks;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Orleans.CodeGeneration;
using Orleans.DurableTasks;
using Orleans.Journaling.DurableTasks;
using Orleans.Runtime;
using Orleans.Runtime.DurableTasks;
using Orleans.Serialization;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.Serializers;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Tests for <see cref="Orleans.Journaling.DurableTasks.DurableTaskGrainStorage"/>, the journaled
/// implementation of <see cref="IDurableTaskGrainStorage"/>. These tests focus specifically on the
/// behavioral deltas versus <see cref="VolatileDurableTaskGrainStorage"/>: default-<see cref="TaskId"/>
/// validation, idempotent cancellation requests, and legacy-observer migration on every read path.
/// </summary>
[TestCategory("BVT")]
public class DurableTaskGrainStorageTests : JournalingTestBase
{
    private async Task<(DurableTaskGrainStorage Storage, IJournaledStateManager Manager, IDurableDictionary<TaskId, DurableTaskState> Dictionary)> CreateSubject(TimeProvider? timeProvider = null)
    {
        var sut = CreateTestSystem(provider: timeProvider);
        var keyCodec = CodecProvider.GetCodec<TaskId>();
        var valueCodec = CodecProvider.GetCodec<DurableTaskState>();
        var dictionary = new DurableDictionary<TaskId, DurableTaskState>(
            "$tasks",
            sut.Manager,
            new OrleansBinaryDurableDictionaryCommandCodec<TaskId, DurableTaskState>(keyCodec, valueCodec, SessionPool));
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);

        var storage = new DurableTaskGrainStorage(
            dictionary,
            sut.Manager,
            timeProvider ?? TimeProvider.System,
            ServiceProvider.GetRequiredService<DeepCopier<DurableTaskState>>());
        return (storage, sut.Manager, dictionary);
    }

    private static TaskId CreateTaskId(string value = "root-task") => TaskId.Parse(value, provider: null);

    [Fact]
    public async Task SetRemoteTarget_PersistsAcrossRecovery()
    {
        var (storage, manager, _) = await CreateSubject();
        var taskId = CreateTaskId("remote-child");
        var target = GrainId.Create("remote", "target");
        var state = storage.GetOrCreateTask(taskId, request: null);

        storage.SetRemoteTarget(taskId, state, target);
        await manager.WriteStateAsync(CancellationToken.None);
        await manager.RevertPendingChangesAsync(CancellationToken.None);

        Assert.True(storage.TryGetTask(taskId, out var recovered));
        Assert.Equal(target, recovered.RemoteTarget);
    }

    // ---------------------------------------------------------------------
    // default(TaskId) validation - genuine delta vs. VolatileDurableTaskGrainStorage
    // ---------------------------------------------------------------------

    [Fact]
    public async Task GetOrCreateTask_DefaultTaskId_ThrowsArgumentOutOfRangeException()
    {
        var (storage, _, _) = await CreateSubject();

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => storage.GetOrCreateTask(default, request: null));
        Assert.Equal("taskId", ex.ParamName);
    }

    [Fact]
    public async Task TryGetTask_DefaultTaskId_ThrowsArgumentOutOfRangeException()
    {
        var (storage, _, _) = await CreateSubject();

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => storage.TryGetTask(default, out _));
        Assert.Equal("taskId", ex.ParamName);
    }

    [Fact]
    public async Task RemoveTask_DefaultTaskId_ThrowsArgumentOutOfRangeException()
    {
        var (storage, _, _) = await CreateSubject();

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => storage.RemoveTask(default));
        Assert.Equal("taskId", ex.ParamName);
    }

    [Fact]
    public async Task VolatileDurableTaskGrainStorage_DefaultTaskId_DoesNotThrow_UnlikeJournaledStorage()
    {
        // This is a dedicated comparison test: VolatileDurableTaskGrainStorage performs no default-TaskId
        // validation at all, whereas DurableTaskGrainStorage guards every entry point with
        // ArgumentOutOfRangeException.ThrowIfEqual(taskId, default). This is a genuine behavioral difference
        // between the two IDurableTaskGrainStorage implementations.
        var volatileStorage = CreateVolatileStorage();

        // GetOrCreateTask with default(TaskId) does not throw - it creates/returns a task keyed by default.
        var state = volatileStorage.GetOrCreateTask(default, request: null);
        Assert.NotNull(state);

        // TryGetTask with default(TaskId) does not throw either. Note: VolatileDurableTaskGrainStorage
        // deep-copies on every read (see TryGetTask's CopyState call), so the returned instance is never
        // reference-equal to a prior GetOrCreateTask result - assert value equivalence instead of identity.
        var found = volatileStorage.TryGetTask(default, out var foundState);
        Assert.True(found);
        Assert.NotSame(state, foundState);
        Assert.Equal(state.CreatedAt, foundState!.CreatedAt);

        // RemoveTask with default(TaskId) does not throw.
        var removed = volatileStorage.RemoveTask(default);
        Assert.True(removed);

        // Whereas the journaled storage throws for all three of the above operations (asserted in the
        // dedicated tests above), demonstrating the genuine behavioral delta between implementations.
        var (journaledStorage, _, _) = await CreateSubject();
        Assert.Throws<ArgumentOutOfRangeException>(() => journaledStorage.GetOrCreateTask(default, request: null));
    }

    [Fact]
    public async Task GetOrCreateTask_NewTaskId_CreatesTaskWithCreatedAtFromTimeProvider()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2024-01-01T00:00:00Z"));
        var (storage, _, _) = await CreateSubject(timeProvider);
        var taskId = CreateTaskId();

        var state = storage.GetOrCreateTask(taskId, request: null);

        Assert.Equal(timeProvider.GetUtcNow(), state.CreatedAt);
        Assert.Null(state.Request);
        Assert.Null(state.Result);
        Assert.False(state.CompletedAt.HasValue);
    }

    [Fact]
    public async Task GetOrCreateTask_ExistingTaskId_ReturnsPersistedStateRatherThanCreatingNew()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2024-01-01T00:00:00Z"));
        var (storage, _, dictionary) = await CreateSubject(timeProvider);
        var taskId = CreateTaskId();

        var first = storage.GetOrCreateTask(taskId, request: null);
        timeProvider.Advance(TimeSpan.FromHours(1));
        var second = storage.GetOrCreateTask(taskId, request: null);

        // Second call must not overwrite CreatedAt with the advanced clock value: it is the same
        // persisted entry, not a freshly created one.
        Assert.Equal(first.CreatedAt, second.CreatedAt);
        Assert.NotEqual(timeProvider.GetUtcNow(), second.CreatedAt);
        Assert.Single(dictionary);
    }

    [Fact]
    public async Task GetOrCreateTask_RequestWithoutContext_ThrowsAndDoesNotPersistTask()
    {
        var (storage, _, dictionary) = await CreateSubject();
        var taskId = CreateTaskId();

        var exception = Assert.Throws<InvalidOperationException>(() => storage.GetOrCreateTask(taskId, new DurableTaskStorageTestRequest()));

        Assert.Contains("must not be null", exception.Message, StringComparison.Ordinal);
        Assert.Empty(dictionary);
        Assert.False(storage.TryGetTask(taskId, out _));
    }

    [Fact]
    public async Task TasksAndGetChildren_ExposePersistedTasksAndFilterByParentTaskId()
    {
        var (storage, _, _) = await CreateSubject();
        var parent = CreateTaskId("parent");
        var childOne = parent.Child("child-one");
        var childTwo = parent.Child("child-two");
        var unrelated = CreateTaskId("unrelated");

        storage.GetOrCreateTask(parent, request: null);
        storage.GetOrCreateTask(childOne, request: null);
        storage.GetOrCreateTask(childTwo, request: null);
        storage.GetOrCreateTask(unrelated, request: null);

        var tasks = storage.Tasks.Select(task => task.Id).ToHashSet();
        Assert.True(tasks.SetEquals([parent, childOne, childTwo, unrelated]));

        var children = storage.GetChildren(parent).Select(task => task.Id).ToHashSet();
        Assert.True(children.SetEquals([childOne, childTwo]));
    }

    [Fact]
    public async Task SetRequest_StoresProvidedRequestInstance()
    {
        var (storage, _, dictionary) = await CreateSubject();
        var taskId = CreateTaskId();
        var state = storage.GetOrCreateTask(taskId, request: null);
        var request = new DurableTaskStorageTestRequest
        {
            Context = new DurableTaskRequestContext
            {
                CallerId = GrainId.Create("caller", "1"),
                TargetId = GrainId.Create("target", "1"),
            }
        };

        storage.SetRequest(taskId, state, request);

        Assert.Same(request, dictionary[taskId].Request);
    }

    [Fact]
    public async Task SetResponse_StoresResponseAndCompletionTimestampFromTimeProvider()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2024-01-01T00:00:00Z"));
        var (storage, _, dictionary) = await CreateSubject(timeProvider);
        var taskId = CreateTaskId();
        var state = storage.GetOrCreateTask(taskId, request: null);
        var response = DurableTaskResponse.FromResult(42);

        timeProvider.Advance(TimeSpan.FromMinutes(5));
        storage.SetResponse(taskId, state, response);

        var persisted = dictionary[taskId];
        Assert.Same(response, persisted.Result);
        Assert.Equal(timeProvider.GetUtcNow(), persisted.CompletedAt);
    }

    [Fact]
    public async Task SetResponse_ThrowsWhenStateDoesNotBelongToProvidedTaskId()
    {
        var (storage, _, dictionary) = await CreateSubject();
        var taskId = CreateTaskId("task-one");
        var otherTaskId = CreateTaskId("task-two");
        var state = storage.GetOrCreateTask(taskId, request: null);

        var exception = Assert.Throws<ArgumentException>(() => storage.SetResponse(otherTaskId, state, DurableTaskResponse.FromResult(123)));

        Assert.Equal("state", exception.ParamName);
        Assert.False(dictionary.ContainsKey(otherTaskId));
    }

    // ---------------------------------------------------------------------
    // RequestCancellation idempotency - genuine delta vs. VolatileDurableTaskGrainStorage
    // ---------------------------------------------------------------------

    [Fact]
    public async Task RequestCancellation_SetsCancellationRequestedAt_WhenNotAlreadyCancelledOrCompleted()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2024-01-01T00:00:00Z"));
        var (storage, _, _) = await CreateSubject(timeProvider);
        var taskId = CreateTaskId();
        var state = storage.GetOrCreateTask(taskId, request: null);

        storage.RequestCancellation(taskId, state);

        Assert.True(storage.TryGetTask(taskId, out var updated));
        Assert.Equal(timeProvider.GetUtcNow(), updated.CancellationRequestedAt);
    }

    [Fact]
    public async Task RequestCancellation_IsNoOp_WhenAlreadyCancelled()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2024-01-01T00:00:00Z"));
        var (storage, _, _) = await CreateSubject(timeProvider);
        var taskId = CreateTaskId();
        var state = storage.GetOrCreateTask(taskId, request: null);

        storage.RequestCancellation(taskId, state);
        Assert.True(storage.TryGetTask(taskId, out var canceled));
        var firstCancellationTimestamp = canceled.CancellationRequestedAt;
        Assert.NotNull(firstCancellationTimestamp);

        timeProvider.Advance(TimeSpan.FromHours(1));
        storage.RequestCancellation(taskId, canceled);

        // The second call must be a no-op: the original cancellation timestamp is preserved,
        // not overwritten with the advanced clock value.
        Assert.True(storage.TryGetTask(taskId, out var afterRetry));
        Assert.Equal(firstCancellationTimestamp, afterRetry.CancellationRequestedAt);
        Assert.NotEqual(timeProvider.GetUtcNow(), afterRetry.CancellationRequestedAt);
    }

    [Fact]
    public async Task RequestCancellation_IsNoOp_WhenAlreadyCompleted()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2024-01-01T00:00:00Z"));
        var (storage, _, _) = await CreateSubject(timeProvider);
        var taskId = CreateTaskId();
        var state = storage.GetOrCreateTask(taskId, request: null);
        storage.SetResponse(taskId, state, DurableTaskResponse.FromResult(42));
        Assert.True(storage.TryGetTask(taskId, out var completed));
        Assert.True(completed.CompletedAt.HasValue);

        timeProvider.Advance(TimeSpan.FromHours(1));
        storage.RequestCancellation(taskId, completed);

        Assert.True(storage.TryGetTask(taskId, out var afterCancellation));
        Assert.Null(afterCancellation.CancellationRequestedAt);
    }

    [Fact]
    public void RequestCancellation_Volatile_AlwaysOverwrites_UnlikeJournaledStorage()
    {
        // This is a dedicated comparison test: VolatileDurableTaskGrainStorage's RequestCancellation has no
        // idempotency guard at all - it unconditionally overwrites CancellationRequestedAt every time it is
        // called, even if the task is already cancelled or completed. This differs from the journaled
        // DurableTaskGrainStorage, which no-ops in both of those cases (asserted above).
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2024-01-01T00:00:00Z"));
        var volatileStorage = CreateVolatileStorage(timeProvider);
        var taskId = CreateTaskId();
        var state = volatileStorage.GetOrCreateTask(taskId, request: null);

        volatileStorage.RequestCancellation(taskId, state);
        var firstCancellationTimestamp = state.CancellationRequestedAt;
        Assert.NotNull(firstCancellationTimestamp);

        timeProvider.Advance(TimeSpan.FromHours(1));
        volatileStorage.RequestCancellation(taskId, state);

        Assert.NotEqual(firstCancellationTimestamp, state.CancellationRequestedAt);
        Assert.Equal(timeProvider.GetUtcNow(), state.CancellationRequestedAt);
    }

    // ---------------------------------------------------------------------
    // Legacy-observer migration - runs on every read path (GetOrCreateTask/TryGetTask/GetState)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task GetOrCreateTask_MigratesLegacyObserversToCompletionDestinations()
    {
        var (storage, _, dictionary) = await CreateSubject();
        var taskId = CreateTaskId();
        var observerGrainId = GrainId.Create("legacy-observer-type", "legacy-observer-key");
        var observerReference = CreateObserverGrainReference(observerGrainId);

        // Seed the dictionary directly with a state containing a legacy observer, bypassing the storage's
        // own creation path (which never populates LegacyObservers - only old, pre-migration snapshots would).
        // ApplySet (rather than Add) is used deliberately: it inserts directly into the in-memory backing
        // store without round-tripping through the journal codec, since GrainReference-typed observers require
        // DI wiring (IGrainReferenceRuntime/IGrainFactory/GrainReferenceCodecProvider) that this minimal
        // journaling test harness does not provide - the migration logic under test operates purely on the
        // in-memory DurableTaskState instance regardless of how it entered the dictionary.
        var seeded = new DurableTaskState { CreatedAt = TimeProvider.System.GetUtcNow() };
        seeded.LegacyObservers.Add(observerReference);
        ((IDurableDictionaryCommandHandler<TaskId, DurableTaskState>)dictionary).ApplySet(taskId, seeded);

        var state = storage.GetOrCreateTask(taskId, request: null);

        Assert.NotSame(seeded, state);
        Assert.Single(state.CompletionDestinations);
        Assert.Contains(observerGrainId, ((IDurableTaskState)state).CompletionDestinations);
        Assert.Empty(((DurableTaskState)state).LegacyObservers);
    }

    [Fact]
    public async Task TryGetTask_MigratesLegacyObserversToCompletionDestinations()
    {
        var (storage, _, dictionary) = await CreateSubject();
        var taskId = CreateTaskId();
        var observerGrainId = GrainId.Create("legacy-observer-type", "legacy-observer-key-2");
        var observerReference = CreateObserverGrainReference(observerGrainId);

        var seeded = new DurableTaskState { CreatedAt = TimeProvider.System.GetUtcNow() };
        seeded.LegacyObservers.Add(observerReference);
        ((IDurableDictionaryCommandHandler<TaskId, DurableTaskState>)dictionary).ApplySet(taskId, seeded);

        var found = storage.TryGetTask(taskId, out var state);

        Assert.True(found);
        Assert.Contains(observerGrainId, state!.CompletionDestinations);
        Assert.Empty(((DurableTaskState)state).LegacyObservers);
    }

    [Fact]
    public async Task AddCompletionDestination_MigratesLegacyObserversViaPrivateGetStateHelper()
    {
        // SetRequest/SetResponse/RequestCancellation/AddCompletionDestination/ClearCompletionDestinations all
        // route through the private GetState helper, which also runs MigrateLegacyObservers - so migration is
        // not limited to the read-only accessors (GetOrCreateTask/TryGetTask).
        var (storage, _, dictionary) = await CreateSubject();
        var taskId = CreateTaskId();
        var legacyObserverGrainId = GrainId.Create("legacy-observer-type", "legacy-observer-key-3");
        var observerReference = CreateObserverGrainReference(legacyObserverGrainId);
        var newDestination = GrainId.Create("new-destination-type", "new-destination-key");

        var seeded = new DurableTaskState { CreatedAt = TimeProvider.System.GetUtcNow() };
        seeded.LegacyObservers.Add(observerReference);
        ((IDurableDictionaryCommandHandler<TaskId, DurableTaskState>)dictionary).ApplySet(taskId, seeded);

        storage.AddCompletionDestination(taskId, seeded, newDestination);

        var persisted = dictionary[taskId];
        Assert.Contains(legacyObserverGrainId, persisted.CompletionDestinations);
        Assert.Contains(newDestination, persisted.CompletionDestinations);
        Assert.Equal(2, persisted.CompletionDestinations.Count);
        Assert.Empty(persisted.LegacyObservers);
    }

    [Fact]
    public async Task ClearCompletionDestinations_RemovesAllPersistedDestinations()
    {
        var (storage, _, dictionary) = await CreateSubject();
        var taskId = CreateTaskId();
        var state = storage.GetOrCreateTask(taskId, request: null);
        storage.AddCompletionDestination(taskId, state, GrainId.Create("destination", "one"));
        storage.AddCompletionDestination(taskId, state, GrainId.Create("destination", "two"));

        storage.ClearCompletionDestinations(taskId, state);

        Assert.Empty(dictionary[taskId].CompletionDestinations);
    }

    [Fact]
    public async Task MigrateLegacyObservers_WithNoLegacyObservers_LeavesCompletionDestinationsUnchanged()
    {
        var (storage, _, dictionary) = await CreateSubject();
        var taskId = CreateTaskId();
        var existingDestination = GrainId.Create("pre-existing-destination-type", "pre-existing-destination-key");

        var seeded = new DurableTaskState { CreatedAt = TimeProvider.System.GetUtcNow() };
        seeded.CompletionDestinations.Add(existingDestination);
        dictionary.Add(taskId, seeded);

        var state = storage.GetOrCreateTask(taskId, request: null);

        Assert.Single(state.CompletionDestinations);
        Assert.Contains(existingDestination, state.CompletionDestinations);
    }

    // ---------------------------------------------------------------------
    // WriteAsync/ReadAsync delegation to IJournaledStateManager
    // ---------------------------------------------------------------------

    [Fact]
    public async Task WriteAsync_DelegatesToJournaledStateManager_WriteStateAsync()
    {
        var sut = CreateTestSystem();
        var keyCodec = CodecProvider.GetCodec<TaskId>();
        var valueCodec = CodecProvider.GetCodec<DurableTaskState>();
        var dictionary = new DurableDictionary<TaskId, DurableTaskState>(
            "$tasks",
            sut.Manager,
            new OrleansBinaryDurableDictionaryCommandCodec<TaskId, DurableTaskState>(keyCodec, valueCodec, SessionPool));
        var spy = new SpyJournaledStateManager(sut.Manager);
        var storage = new DurableTaskGrainStorage(
            dictionary,
            spy,
            TimeProvider.System,
            ServiceProvider.GetRequiredService<DeepCopier<DurableTaskState>>());

        // The manager's internal work loop is only started (lazily) by InitializeAsync/lifecycle OnStart - it
        // must run before WriteStateAsync's enqueued work item can ever be dequeued and completed, otherwise
        // the await below would block forever waiting for a loop that was never started.
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);

        await storage.WriteAsync(CancellationToken.None);

        Assert.Equal(1, spy.WriteStateAsyncCallCount);
        Assert.Equal(0, spy.InitializeAsyncCallCount);
    }

    [Fact]
    public async Task ReadAsync_DelegatesToJournaledStateManager_InitializeAsync()
    {
        var sut = CreateTestSystem();
        var keyCodec = CodecProvider.GetCodec<TaskId>();
        var valueCodec = CodecProvider.GetCodec<DurableTaskState>();
        var dictionary = new DurableDictionary<TaskId, DurableTaskState>(
            "$tasks",
            sut.Manager,
            new OrleansBinaryDurableDictionaryCommandCodec<TaskId, DurableTaskState>(keyCodec, valueCodec, SessionPool));
        var spy = new SpyJournaledStateManager(sut.Manager);
        var storage = new DurableTaskGrainStorage(
            dictionary,
            spy,
            TimeProvider.System,
            ServiceProvider.GetRequiredService<DeepCopier<DurableTaskState>>());

        await storage.ReadAsync(CancellationToken.None);

        Assert.Equal(1, spy.InitializeAsyncCallCount);
        Assert.Equal(0, spy.WriteStateAsyncCallCount);
    }

    // ---------------------------------------------------------------------
    // Clear()
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Clear_RemovesAllItemsFromUnderlyingDictionary()
    {
        var (storage, _, dictionary) = await CreateSubject();
        storage.GetOrCreateTask(CreateTaskId("task-one"), request: null);
        storage.GetOrCreateTask(CreateTaskId("task-two"), request: null);
        Assert.Equal(2, dictionary.Count);

        storage.Clear();

        Assert.Empty(dictionary);
        Assert.Empty(storage.Tasks);
    }

    [Fact]
    public async Task Clear_ThenGetOrCreateTask_CreatesFreshEntry()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2024-01-01T00:00:00Z"));
        var (storage, _, dictionary) = await CreateSubject(timeProvider);
        var taskId = CreateTaskId();
        var original = storage.GetOrCreateTask(taskId, request: null);
        storage.SetResponse(taskId, original, DurableTaskResponse.FromResult(1));

        storage.Clear();
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        var recreated = storage.GetOrCreateTask(taskId, request: null);

        Assert.NotSame(original, recreated);
        Assert.Null(recreated.Result);
        Assert.Equal(timeProvider.GetUtcNow(), recreated.CreatedAt);
        Assert.Single(dictionary);
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private VolatileDurableTaskGrainStorage CreateVolatileStorage(TimeProvider? timeProvider = null)
    {
        var storageCopier = ServiceProvider.GetRequiredService<DeepCopier<Dictionary<TaskId, DurableTaskState>>>();
        var stateCopier = ServiceProvider.GetRequiredService<DeepCopier<DurableTaskState>>();
        return new VolatileDurableTaskGrainStorage(storageCopier, stateCopier, timeProvider ?? TimeProvider.System);
    }

    private IDurableTaskObserver CreateObserverGrainReference(GrainId grainId)
    {
        var codecProvider = ServiceProvider.GetRequiredService<Orleans.Serialization.Serializers.CodecProvider>();
        var copyContextPool = ServiceProvider.GetRequiredService<CopyContextPool>();
        var shared = new GrainReferenceShared(
            grainId.Type,
            GrainInterfaceType.Create("durable-task-observer-tests"),
            interfaceVersion: 0,
            new FakeGrainReferenceRuntime(),
            InvokeMethodOptions.None,
            codecProvider,
            copyContextPool,
            ServiceProvider);
        return new FakeObserverGrainReference(shared, grainId.Key);
    }

    private sealed class FakeObserverGrainReference(GrainReferenceShared shared, IdSpan key) : GrainReference(shared, key), IDurableTaskObserver
    {
        public ValueTask OnResponseAsync(TaskId taskId, DurableTaskResponse response, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    private sealed class FakeGrainReferenceRuntime : IGrainReferenceRuntime
    {
        public object Cast(IAddressable grain, Type interfaceType) => grain;
        public void InvokeMethod(GrainReference reference, IInvokable request, InvokeMethodOptions options) => throw new NotImplementedException();
        public ValueTask InvokeMethodAsync(GrainReference reference, IInvokable request, InvokeMethodOptions options) => throw new NotImplementedException();
        public ValueTask<T?> InvokeMethodAsync<T>(GrainReference reference, IInvokable request, InvokeMethodOptions options) => throw new NotImplementedException();
    }

    private sealed class SpyJournaledStateManager(IJournaledStateManager inner) : IJournaledStateManager
    {
        public int InitializeAsyncCallCount { get; private set; }
        public int WriteStateAsyncCallCount { get; private set; }

        public ValueTask InitializeAsync(CancellationToken cancellationToken)
        {
            InitializeAsyncCallCount++;
            return inner.InitializeAsync(cancellationToken);
        }

        public void RegisterState(string name, IJournaledState state) => inner.RegisterState(name, state);

        public bool TryGetState(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IJournaledState? state) => inner.TryGetState(name, out state);

        public ValueTask WriteStateAsync(CancellationToken cancellationToken)
        {
            WriteStateAsyncCallCount++;
            return inner.WriteStateAsync(cancellationToken);
        }

        public ValueTask RevertPendingChangesAsync(CancellationToken cancellationToken) =>
            inner.RevertPendingChangesAsync(cancellationToken);

        public ValueTask DeleteStateAsync(CancellationToken cancellationToken) => inner.DeleteStateAsync(cancellationToken);
    }

}

[GenerateSerializer]
internal sealed class DurableTaskStorageTestRequest : IDurableTaskRequest
{
    [Id(0)]
    public DurableTaskRequestContext? Context { get; set; }

    [Id(1)]
    public InvokeMethodOptions Options { get; private set; }

    public void AddInvokeMethodOptions(InvokeMethodOptions options) => Options |= options;

    public DurableTask CreateTask() => DurableTask.Run(static _ => { });

    public void Dispose()
    {
    }

    public string GetActivityName() => $"{GetInterfaceName()}.{GetMethodName()}";

    public object? GetArgument(int index) => throw new ArgumentOutOfRangeException(nameof(index));

    public int GetArgumentCount() => 0;

    public CancellationToken GetCancellationToken() => CancellationToken.None;

    public TimeSpan? GetDefaultResponseTimeout() => null;

    public string GetInterfaceName() => typeof(IDurableTaskRequest).FullName!;

    public Type GetInterfaceType() => typeof(IDurableTaskRequest);

    public MethodInfo GetMethod() => typeof(DurableTaskStorageTestRequest).GetMethod(nameof(CreateTask))!;

    public string GetMethodName() => nameof(CreateTask);

    public object? GetTarget() => null;

    public bool IsCancellable => false;

    public ValueTask<Response> Invoke() => throw new NotImplementedException();

    public void SetArgument(int index, object value) => throw new ArgumentOutOfRangeException(nameof(index));

    public void SetTarget(ITargetHolder holder) => throw new NotImplementedException();

    public bool TryCancel() => false;
}
