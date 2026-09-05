using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks.Runtime;
using Orleans.DurableTasks.Storage;
using Orleans.DurableTasks;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Journaling")]
public sealed class DurableTaskStorageTests : JournalingTestBase
{
    [Fact]
    public async Task CanceledResponseRoundTripsThroughJournaledStorage()
    {
        var initial = CreateTestSystem();
        var storage = CreateTaskStorage(initial.Manager);
        await initial.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        var taskId = TaskId.Parse("root/canceled");
        var exception = new OperationCanceledException(
            "Durable cancellation requested.",
            new InvalidOperationException("Cancellation source."));
        exception.Data["task"] = "root/canceled";
        var state = storage.GetOrCreateTask(taskId, request: null);
        storage.SetResponse(taskId, state, new CanceledDurableTaskResponse(exception));
        await storage.WriteAsync(TestContext.Current.CancellationToken);

        var recovered = CreateTestSystem(storage: initial.Storage);
        var recoveredStorage = CreateTaskStorage(recovered.Manager);
        await recovered.Lifecycle.OnStart(TestContext.Current.CancellationToken);

        Assert.True(recoveredStorage.TryGetTask(taskId, out var recoveredState));
        var response = Assert.IsType<CanceledDurableTaskResponse>(recoveredState.Result);
        Assert.Equal(DurableTaskStatus.Canceled, response.Status);
        Assert.Equal("Durable cancellation requested.", response.Exception.Message);
        Assert.Equal(
            "Cancellation source.",
            Assert.IsType<InvalidOperationException>(response.Exception.InnerException).Message);
        Assert.Equal("root/canceled", response.Exception.Data["task"]);
    }

    [Fact]
    public async Task MutationRejectsStateOwnedByDifferentTaskId()
    {
        var sut = CreateTestSystem();
        var storage = CreateTaskStorage(sut.Manager);
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        var firstId = TaskId.Parse("root/first");
        var secondId = TaskId.Parse("root/second");
        var firstState = storage.GetOrCreateTask(firstId, request: null);
        var secondState = storage.GetOrCreateTask(secondId, request: null);

        var exception = Assert.Throws<ArgumentException>(
            () => storage.SetResponse(secondId, firstState, DurableTaskResponse.Completed));

        Assert.Equal("state", exception.ParamName);
        Assert.Null(secondState.Result);
        Assert.True(storage.TryGetTask(firstId, out var retainedFirst));
        Assert.Same(firstState, retainedFirst);
        Assert.True(storage.TryGetTask(secondId, out var retainedSecond));
        Assert.Same(secondState, retainedSecond);
    }

    [Fact]
    public async Task SetRequestRejectsMissingContext()
    {
        var sut = CreateTestSystem();
        var storage = CreateTaskStorage(sut.Manager);
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        var taskId = TaskId.Parse("root/missing-context");
        var state = storage.GetOrCreateTask(taskId, request: null);
        var request = Substitute.For<IDurableTaskRequest>();

        var exception = Assert.Throws<InvalidOperationException>(
            () => storage.SetRequest(taskId, state, request));

        Assert.Equal("The request context must not be null.", exception.Message);
        Assert.Null(state.Request);
    }

    private DurableTaskGrainStorage CreateTaskStorage(IJournaledStateManager manager)
    {
        var tasks = new DurableDictionary<TaskId, DurableTaskState>(
            "$tasks",
            manager,
            new OrleansBinaryDurableDictionaryCommandCodec<TaskId, DurableTaskState>(
                CodecProvider.GetCodec<TaskId>(),
                CodecProvider.GetCodec<DurableTaskState>(),
                SessionPool));
        return new DurableTaskGrainStorage(tasks, manager, TimeProvider.System);
    }

    [Fact]
    public async Task OrleansBinaryFormat_RoundTripsRemoteIdentityTombstone()
    {
        var taskId = TaskId.Parse("root/remote/tombstone");
        var remoteTarget = GrainId.Create("remote-target", "tombstone");
        var initial = CreateTestSystem();
        var storage = CreateTaskStorage(initial.Manager);
        await initial.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        var state = storage.GetOrCreateTask(taskId, request: null);
        storage.SetRemoteRequest(taskId, state, remoteTarget, "remote-fingerprint");
        storage.CreateTombstone(taskId, state);
        await storage.WriteAsync(TestContext.Current.CancellationToken);

        var recovered = CreateTestSystem(storage: initial.Storage);
        var recoveredStorage = CreateTaskStorage(recovered.Manager);
        await recovered.Lifecycle.OnStart(TestContext.Current.CancellationToken);

        Assert.True(recoveredStorage.TryGetTask(taskId, out var recoveredState));
        Assert.NotNull(recoveredState.TombstonedAt);
        Assert.Equal(remoteTarget, recoveredState.RemoteTarget);
        Assert.Equal("remote-fingerprint", recoveredState.RemoteRequestFingerprint);
        Assert.Null(recoveredState.RequestFingerprint);
        Assert.Null(recoveredState.Request);
        Assert.Null(recoveredState.Result);
    }

    [Fact]
    public async Task OrleansBinaryFormat_RoundTripsDurableTaskStateWithRemoteIdentityAndTerminalResult()
    {
        var createdAt = new DateTimeOffset(2041, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var clock = new FakeTimeProvider(createdAt);
        var taskId = TaskId.Parse("root/remote/terminal");
        var terminalResult = TaskId.Parse("root/result/42");
        var remoteTarget = GrainId.Create("remote-target", "alpha");
        var caller = GrainId.Create("caller", "beta");
        var completionDestination = GrainId.Create("completion-destination", "gamma");
        var initial = CreateTestSystem();
        var storage = CreateTaskStorage(initial.Manager, clock);
        await initial.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        var state = storage.GetOrCreateTask(taskId, request: null);
        storage.SetRequestFingerprint(taskId, state, "request-fingerprint-v1");
        storage.SetRemoteRequest(taskId, state, remoteTarget, "remote-fingerprint-v2");
        storage.SetCallerId(taskId, state, caller);
        storage.AddCompletionDestination(taskId, state, completionDestination);
        clock.Advance(TimeSpan.FromMinutes(3));
        storage.SetResponse(taskId, state, DurableTaskResponse.FromResult(terminalResult));
        await storage.WriteAsync(TestContext.Current.CancellationToken);

        var recovered = CreateTestSystem(storage: initial.Storage);
        var recoveredStorage = CreateTaskStorage(recovered.Manager, clock);
        await recovered.Lifecycle.OnStart(TestContext.Current.CancellationToken);

        var recoveredEntry = Assert.Single(recoveredStorage.Tasks);
        Assert.Equal(taskId, recoveredEntry.Id);
        var recoveredState = Assert.IsType<DurableTaskState>(recoveredEntry.State);
        Assert.Null(recoveredState.Request);
        Assert.Equal(createdAt, recoveredState.CreatedAt);
        Assert.Equal(createdAt.AddMinutes(3), recoveredState.CompletedAt);
        Assert.Null(recoveredState.CancellationRequestedAt);
        Assert.Null(recoveredState.DueTime);
        Assert.Equal(0, recoveredState.ResumeGeneration);
        Assert.Equal("request-fingerprint-v1", recoveredState.RequestFingerprint);
        Assert.Null(recoveredState.TombstonedAt);
        Assert.Equal(remoteTarget, recoveredState.RemoteTarget);
        Assert.Equal("remote-fingerprint-v2", recoveredState.RemoteRequestFingerprint);
        Assert.Equal(caller, recoveredState.CallerId);
        Assert.Empty(recoveredState.LegacyObservers);
        Assert.Equal(completionDestination, Assert.Single(recoveredState.CompletionDestinations));
        var response = Assert.IsType<DurableTaskResponse<TaskId>>(recoveredState.Result);
        Assert.Equal(DurableTaskResponseKind.CompletedSuccessfully, response.ResponseKind);
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, response.Status);
        Assert.Equal(terminalResult, response.TypedResult);
        Assert.Equal(terminalResult, response.GetResult<TaskId>());
    }

    private DurableTaskGrainStorage CreateTaskStorage(
        IJournaledStateManager manager,
        TimeProvider timeProvider)
    {
        var tasks = new DurableDictionary<TaskId, DurableTaskState>(
            "$tasks",
            manager,
            new OrleansBinaryDurableDictionaryCommandCodec<TaskId, DurableTaskState>(
                CodecProvider.GetCodec<TaskId>(),
                CodecProvider.GetCodec<DurableTaskState>(),
                SessionPool));
        return new DurableTaskGrainStorage(tasks, manager, timeProvider);
    }
}
