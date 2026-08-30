using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableJobs;
using Orleans.DurableMessaging;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Session;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class DurableTaskResponsePersistenceTests
{
    [Fact]
    public void CanceledResponseCodecAndCopierPreserveOperationCanceledException()
    {
        using var services = CreateSerializationServices();
        var original = CreateCanceledResponse();
        var serializer = services.GetRequiredService<Serializer>();

        var serialized = serializer.SerializeToArray<DurableTaskResponse>(original);
        var deserialized = Assert.IsType<CanceledDurableTaskResponse>(
            serializer.Deserialize<DurableTaskResponse>(serialized));
        var copied = Assert.IsType<CanceledDurableTaskResponse>(
            services.GetRequiredService<DeepCopier<CanceledDurableTaskResponse>>().Copy(original));

        AssertCancellation(deserialized);
        AssertCancellation(copied);
        Assert.NotSame(original, copied);
    }

    [Fact]
    public async Task CanceledResponseRoundTripsThroughVolatileStorage()
    {
        using var services = CreateSerializationServices();
        var storage = new VolatileDurableTaskGrainStorage(
            services.GetRequiredService<DeepCopier<Dictionary<TaskId, DurableTaskState>>>(),
            services.GetRequiredService<DeepCopier<DurableTaskState>>(),
            TimeProvider.System);
        var taskId = TaskId.Parse("root/canceled");
        var state = storage.GetOrCreateTask(taskId, request: null);

        storage.SetResponse(taskId, state, CreateCanceledResponse());
        await storage.WriteAsync(TestContext.Current.CancellationToken);
        storage.Clear();
        await storage.ReadAsync(TestContext.Current.CancellationToken);

        Assert.True(storage.TryGetTask(taskId, out var recoveredState));
        AssertCancellation(Assert.IsType<CanceledDurableTaskResponse>(recoveredState.Result));
    }

    [Fact]
    public async Task RemoteIdentityRoundTripsThroughVolatileStorage()
    {
        using var services = CreateSerializationServices();
        var storage = new VolatileDurableTaskGrainStorage(
            services.GetRequiredService<DeepCopier<Dictionary<TaskId, DurableTaskState>>>(),
            services.GetRequiredService<DeepCopier<DurableTaskState>>(),
            TimeProvider.System);
        var taskId = TaskId.Parse("root/remote");
        var target = GrainId.Create("target", "one");
        var caller = GrainId.Create("caller", "one");
        var state = storage.GetOrCreateTask(taskId, request: null);

        storage.SetRemoteRequest(taskId, state, target, "fingerprint");
        storage.SetCallerId(taskId, state, caller);
        await storage.WriteAsync(TestContext.Current.CancellationToken);
        storage.Clear();
        await storage.ReadAsync(TestContext.Current.CancellationToken);

        Assert.True(storage.TryGetTask(taskId, out var recoveredState));
        Assert.Equal(target, recoveredState.RemoteTarget);
        Assert.Equal("fingerprint", recoveredState.RemoteRequestFingerprint);
        Assert.Equal(caller, recoveredState.CallerId);
    }

    [Fact]
    public void VolatileCancellationPreservesNewerCompletedState()
    {
        using var services = CreateSerializationServices();
        var storage = new VolatileDurableTaskGrainStorage(
            services.GetRequiredService<DeepCopier<Dictionary<TaskId, DurableTaskState>>>(),
            services.GetRequiredService<DeepCopier<DurableTaskState>>(),
            TimeProvider.System);
        var taskId = TaskId.Parse("root/completed");
        var staleState = storage.GetOrCreateTask(taskId, request: null);
        Assert.True(storage.TryGetTask(taskId, out var completionState));
        storage.SetResponse(taskId, completionState, DurableTaskResponse.FromResult(42));

        storage.RequestCancellation(taskId, staleState);

        Assert.True(storage.TryGetTask(taskId, out var current));
        Assert.Equal(42, current.Result!.GetResult<int>());
        Assert.Null(current.CancellationRequestedAt);
    }

    [Fact]
    public void VolatileCancellationKeepsStoredStateOwnedByTask()
    {
        using var services = CreateSerializationServices();
        var storage = new VolatileDurableTaskGrainStorage(
            services.GetRequiredService<DeepCopier<Dictionary<TaskId, DurableTaskState>>>(),
            services.GetRequiredService<DeepCopier<DurableTaskState>>(),
            TimeProvider.System);
        var taskId = TaskId.Parse("root/cancel-owned");
        var state = storage.GetOrCreateTask(taskId, request: null);

        storage.RequestCancellation(taskId, state);
        var storedState = Assert.Single(storage.Tasks).State;
        storage.SetResponse(taskId, storedState, DurableTaskResponse.Completed);

        Assert.True(storage.TryGetTask(taskId, out var completed));
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, completed.Result!.Status);
    }

    [Fact]
    public void VolatileStorageRejectsStateFromAnotherStorageInstance()
    {
        using var services = CreateSerializationServices();
        var storageCopier = services.GetRequiredService<DeepCopier<Dictionary<TaskId, DurableTaskState>>>();
        var stateCopier = services.GetRequiredService<DeepCopier<DurableTaskState>>();
        var first = new VolatileDurableTaskGrainStorage(storageCopier, stateCopier, TimeProvider.System);
        var second = new VolatileDurableTaskGrainStorage(storageCopier, stateCopier, TimeProvider.System);
        var taskId = TaskId.Parse("root/owned");
        var state = first.GetOrCreateTask(taskId, request: null);
        second.GetOrCreateTask(taskId, request: null);

        var exception = Assert.Throws<ArgumentException>(
            () => second.SetResponse(taskId, state, DurableTaskResponse.Completed));

        Assert.Equal("state", exception.ParamName);
        Assert.True(second.TryGetTask(taskId, out var secondState));
        Assert.Null(secondState.Result);
    }

    [Fact]
    public void VolatileStorageRejectsStateForDifferentTaskId()
    {
        using var services = CreateSerializationServices();
        var storage = new VolatileDurableTaskGrainStorage(
            services.GetRequiredService<DeepCopier<Dictionary<TaskId, DurableTaskState>>>(),
            services.GetRequiredService<DeepCopier<DurableTaskState>>(),
            TimeProvider.System);
        var ownerTaskId = TaskId.Parse("root/owned");
        var otherTaskId = TaskId.Parse("root/other");
        var state = storage.GetOrCreateTask(ownerTaskId, request: null);
        storage.GetOrCreateTask(otherTaskId, request: null);

        var exception = Assert.Throws<ArgumentException>(
            () => storage.CreateTombstone(otherTaskId, state));

        Assert.Equal("state", exception.ParamName);
        Assert.True(storage.TryGetTask(otherTaskId, out var otherState));
        Assert.Null(otherState.TombstonedAt);
        Assert.True(storage.TryGetTask(ownerTaskId, out var ownerState));
        Assert.Null(ownerState.TombstonedAt);
    }

    [Fact]
    public void CanceledResponseRoundTripsThroughCompletionDelivery()
    {
        using var services = CreateSerializationServices();
        var outbox = new RecordingOutbox();
        var transport = new DurableTaskMessageTransport(
            outbox,
            new RecordingJobManager(),
            services.GetRequiredService<SerializerSessionPool>());
        var taskId = TaskId.Parse("root/canceled");

        transport.SendCompletion(
            GrainId.Create("sender", "one"),
            GrainId.Create("receiver", "one"),
            taskId,
            CreateCanceledResponse());

        var envelope = Assert.Single(outbox.Messages);
        Assert.Equal(DurableTaskMessageTransport.CompletionRoute, envelope.RouteKey);
        Assert.Equal(taskId, envelope.CorrelationKey.ToTaskId());
        Assert.True(envelope.Data.TryGetBody<DurableTaskCompletionMessage>(out var message));
        Assert.NotNull(message);
        Assert.Equal(taskId, message.TaskId);
        AssertCancellation(Assert.IsType<CanceledDurableTaskResponse>(message.Response));
    }

    private static CanceledDurableTaskResponse CreateCanceledResponse()
    {
        var exception = new OperationCanceledException(
            "Durable cancellation requested.",
            new InvalidOperationException("Cancellation source."));
        exception.Data["task"] = "root/canceled";
        return new CanceledDurableTaskResponse(exception);
    }

    private static void AssertCancellation(CanceledDurableTaskResponse response)
    {
        Assert.Equal(DurableTaskStatus.Canceled, response.Status);
        Assert.Equal("Durable cancellation requested.", response.Exception.Message);
        Assert.Equal("Cancellation source.", Assert.IsType<InvalidOperationException>(response.Exception.InnerException).Message);
        Assert.Equal("root/canceled", response.Exception.Data["task"]);
    }

    private static ServiceProvider CreateSerializationServices()
    {
        var services = new ServiceCollection();
        services.AddSerializer(
            builder =>
            {
                builder.AddAssembly(typeof(DurableTaskMessageTransport).Assembly);
                builder.AddAssembly(typeof(DurableTaskResponsePersistenceTests).Assembly);
            });
        return services.BuildServiceProvider();
    }

    private sealed class RecordingOutbox : IDurableOutbox
    {
        private readonly List<DurableEnvelope> _messages = [];
        public IEnumerable<DurableEnvelope> Messages => _messages;
        public int Count => _messages.Count;
        public void Send(DurableEnvelope envelope) => _messages.Add(envelope);
        public bool TryGetMessage(Guid messageId, out DurableEnvelope envelope)
        {
            foreach (var candidate in _messages)
            {
                if (candidate.MessageId == messageId)
                {
                    envelope = candidate;
                    return true;
                }
            }

            envelope = default;
            return false;
        }
    }

    private sealed class RecordingJobManager : ILocalDurableJobManager
    {
        public Task<DurableJob> ScheduleJobAsync(ScheduleJobRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> CancelAsync(DurableJob job, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
