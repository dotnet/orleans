using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Cloning;
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
    public void NullResultRoundTripsWithoutConsumingEnclosingPayload()
    {
        using var services = CreateSerializationServices();
        var serializer = services.GetRequiredService<Serializer>();
        var original = new NullResultContainer
        {
            Response = DurableTaskResponse.FromResult<string?>(null),
            Sentinel = 42,
        };

        var serialized = serializer.SerializeToArray(original);
        var deserialized = serializer.Deserialize<NullResultContainer>(serialized);

        Assert.NotNull(deserialized);
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, deserialized.Response.Status);
        Assert.Null(deserialized.Response.TypedResult);
        Assert.Equal(42, deserialized.Sentinel);
    }

    [GenerateSerializer]
    internal sealed class NullResultContainer
    {
        [Id(0)]
        public required DurableTaskResponse<string?> Response { get; init; }

        [Id(1)]
        public int Sentinel { get; init; }
    }

    [Fact]
    public void CanceledResponseRoundTripsThroughCompletionDelivery()
    {
        var outbox = new RecordingOutbox();
        var transport = MessagingTestHelpers.CreateTransport(outbox);
        var taskId = TaskId.Parse("root/canceled");
        var sender = GrainId.Create("sender", "one");
        var receiver = GrainId.Create("receiver", "one");

        var messageId = transport.GetCompletionMessageId(sender, new(receiver, taskId), taskId);
        var envelope = transport.CreateCompletionMessage(messageId, sender, new(receiver, taskId), taskId, CreateCanceledResponse()).Envelope;

        Assert.Empty(outbox.Messages);
        Assert.Equal(DurableTaskMessageTypes.CompletionSubject, envelope.Subject);
        var message = MessagingTestHelpers.MessageTypes.Completion.Decode(envelope);
        Assert.Equal(taskId, message.TaskId);
        Assert.Equal(sender, message.Sender);
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
}
