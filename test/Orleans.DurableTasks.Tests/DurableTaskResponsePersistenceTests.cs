using System.Distributed.DurableTasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableJobs;
using Orleans.DurableMessaging;
using Orleans.Runtime;
using Orleans.Runtime.DurableTasks;
using Orleans.Serialization;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Session;
using Xunit;

namespace Orleans.DurableTasks.Tests;

[TestCategory("BVT")]
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
        await storage.WriteAsync(default);
        storage.Clear();
        await storage.ReadAsync(default);

        Assert.True(storage.TryGetTask(taskId, out var recoveredState));
        AssertCancellation(Assert.IsType<CanceledDurableTaskResponse>(recoveredState.Result));
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

        public Task<bool> TryCancelDurableJobAsync(DurableJob job, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
