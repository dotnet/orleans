using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks.Runtime;
using Orleans.DurableTasks.Storage;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class DurableTaskStorageTests
{
    [Fact]
    public void CanceledResponseRoundTripsThroughOrleansSerialization()
    {
        using var services = CreateSerializationServices();
        var storage = CreateStorage();
        var taskId = TaskId.Parse("root/canceled");
        var exception = new OperationCanceledException(
            "Durable cancellation requested.",
            new InvalidOperationException("Cancellation source."));
        exception.Data["task"] = taskId.ToString();
        var state = storage.GetOrCreateTask(taskId, request: null);
        storage.SetResponse(taskId, state, new CanceledDurableTaskResponse(exception));

        var recovered = RoundTrip(services, Assert.IsType<DurableTaskState>(state));

        var response = Assert.IsType<CanceledDurableTaskResponse>(recovered.Result);
        Assert.Equal(DurableTaskStatus.Canceled, response.Status);
        Assert.Equal("Durable cancellation requested.", response.Exception.Message);
        Assert.Equal(
            "Cancellation source.",
            Assert.IsType<InvalidOperationException>(response.Exception.InnerException).Message);
        Assert.Equal(taskId.ToString(), response.Exception.Data["task"]);
    }

    [Fact]
    public void MutationRejectsStateOwnedByDifferentTaskId()
    {
        var storage = CreateStorage();
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
    public void SetRequestRejectsMissingContext()
    {
        var storage = CreateStorage();
        var taskId = TaskId.Parse("root/missing-context");
        var state = storage.GetOrCreateTask(taskId, request: null);
        var request = Substitute.For<IDurableTaskRequest>();

        var exception = Assert.Throws<InvalidOperationException>(
            () => storage.SetRequest(taskId, state, request));

        Assert.Equal("The request context must not be null.", exception.Message);
        Assert.Null(state.Request);
    }

    [Fact]
    public void RemoteIdentityTombstoneRoundTripsThroughOrleansSerialization()
    {
        using var services = CreateSerializationServices();
        var storage = CreateStorage();
        var taskId = TaskId.Parse("root/remote/tombstone");
        var remoteTarget = GrainId.Create("remote-target", "tombstone");
        var state = storage.GetOrCreateTask(taskId, request: null);
        storage.SetRemoteRequest(taskId, state, remoteTarget, "remote-fingerprint");
        storage.CreateTombstone(taskId, state);

        var recovered = RoundTrip(services, Assert.IsType<DurableTaskState>(state));

        Assert.NotNull(recovered.TombstonedAt);
        Assert.Equal(remoteTarget, recovered.RemoteTarget);
        Assert.Equal("remote-fingerprint", recovered.RemoteRequestFingerprint);
        Assert.Null(recovered.RequestFingerprint);
        Assert.Null(recovered.Request);
        Assert.Null(recovered.Result);
    }

    [Fact]
    public void FullTerminalStateRoundTripsThroughOrleansSerialization()
    {
        using var services = CreateSerializationServices();
        var createdAt = new DateTimeOffset(2041, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var clock = new FakeTimeProvider(createdAt);
        var storage = CreateStorage(clock);
        var taskId = TaskId.Parse("root/remote/terminal");
        var terminalResult = TaskId.Parse("root/result/42");
        var remoteTarget = GrainId.Create("remote-target", "alpha");
        var caller = GrainId.Create("caller", "beta");
        var completionDestination = GrainId.Create("completion-destination", "gamma");
        var state = storage.GetOrCreateTask(taskId, request: null);
        storage.SetRequestFingerprint(taskId, state, "request-fingerprint-v1");
        storage.SetRemoteRequest(taskId, state, remoteTarget, "remote-fingerprint-v2");
        storage.SetCallerId(taskId, state, caller);
        storage.AddCompletionDestination(taskId, state, completionDestination);
        clock.Advance(TimeSpan.FromMinutes(3));
        storage.SetResponse(taskId, state, DurableTaskResponse.FromResult(terminalResult));

        var recovered = RoundTrip(services, Assert.IsType<DurableTaskState>(state));

        Assert.Null(recovered.Request);
        Assert.Equal(createdAt, recovered.CreatedAt);
        Assert.Equal(createdAt.AddMinutes(3), recovered.CompletedAt);
        Assert.Null(recovered.CancellationRequestedAt);
        Assert.Null(recovered.DueTime);
        Assert.Equal(0, recovered.ResumeGeneration);
        Assert.Equal("request-fingerprint-v1", recovered.RequestFingerprint);
        Assert.Null(recovered.TombstonedAt);
        Assert.Equal(remoteTarget, recovered.RemoteTarget);
        Assert.Equal("remote-fingerprint-v2", recovered.RemoteRequestFingerprint);
        Assert.Equal(caller, recovered.CallerId);
        Assert.Empty(recovered.LegacyObservers);
        Assert.Equal(completionDestination, Assert.Single(recovered.CompletionDestinations));
        var response = Assert.IsType<DurableTaskResponse<TaskId>>(recovered.Result);
        Assert.Equal(terminalResult, response.GetResult<TaskId>());
    }

    private static DurableTaskGrainStorage CreateStorage(TimeProvider? timeProvider = null) =>
        new(
            new TestDurableDictionary<TaskId, DurableTaskState>(),
            new TestStateManager(),
            timeProvider ?? TimeProvider.System);

    private static ServiceProvider CreateSerializationServices()
    {
        var services = new ServiceCollection();
        services.AddSerializer(builder => builder.AddAssembly(typeof(DurableTaskStorageTests).Assembly));
        return services.BuildServiceProvider();
    }

    private static DurableTaskState RoundTrip(ServiceProvider services, DurableTaskState state)
    {
        var serializer = services.GetRequiredService<Serializer>();
        return serializer.Deserialize<DurableTaskState>(serializer.SerializeToArray(state))
            ?? throw new InvalidOperationException("The durable task state deserialized as null.");
    }

    private sealed class TestDurableDictionary<TKey, TValue> : Dictionary<TKey, TValue>, IDurableDictionary<TKey, TValue>
        where TKey : notnull;

    private sealed class TestStateManager : IJournaledStateManager
    {
        public ValueTask InitializeAsync(CancellationToken cancellationToken) => default;
        public void RegisterState(string name, IJournaledState state) { }
        public void RegisterObserver(IJournaledStateObserver observer) { }
        public bool TryGetState(string name, [NotNullWhen(true)] out IJournaledState? state)
        {
            state = null;
            return false;
        }

        public ValueTask WriteStateAsync(CancellationToken cancellationToken) => default;
        public ValueTask RevertPendingChangesAsync(CancellationToken cancellationToken) => default;
        public ValueTask DeleteStateAsync(CancellationToken cancellationToken) => default;
    }
}
