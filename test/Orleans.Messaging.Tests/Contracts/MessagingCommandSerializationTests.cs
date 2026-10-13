using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Serialization;
using Xunit;

namespace Orleans.Messaging.Tests.Contracts;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class MessagingCommandSerializationTests
{
    private static readonly DateTimeOffset Now = new(2040, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private static readonly GrainId Receiver = GrainId.Create("receiver", "1");

    public static TheoryData<int> InboxCommands => new(Enumerable.Range(0, CreateInboxCommands().Length));
    public static TheoryData<int> OutboxCommands => new(Enumerable.Range(0, CreateOutboxCommands().Length));

    [Theory]
    [MemberData(nameof(InboxCommands))]
    public void InboxCommand_RoundTripsThroughJsonAndOrleansSerialization(int index)
    {
        var command = CreateInboxCommands()[index];
        var json = JsonSerializer.Serialize(command, InboxJsonContext.Default.InboxCommand);
        var fromJson = JsonSerializer.Deserialize(json, InboxJsonContext.Default.InboxCommand)!;
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer<InboxCommand>>();
        var fromBinary = serializer.Deserialize(serializer.SerializeToArray(command));

        Assert.Equal(command.Kind, fromJson.Kind);
        Assert.Equal(json, JsonSerializer.Serialize(fromJson, InboxJsonContext.Default.InboxCommand));
        Assert.Equal(json, JsonSerializer.Serialize(fromBinary!, InboxJsonContext.Default.InboxCommand));
    }

    [Theory]
    [MemberData(nameof(OutboxCommands))]
    public void OutboxCommand_RoundTripsThroughJsonAndOrleansSerialization(int index)
    {
        var command = CreateOutboxCommands()[index];
        var json = JsonSerializer.Serialize(command, OutboxJsonContext.Default.OutboxCommand);
        var fromJson = JsonSerializer.Deserialize(json, OutboxJsonContext.Default.OutboxCommand)!;
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer<OutboxCommand>>();
        var fromBinary = serializer.Deserialize(serializer.SerializeToArray(command));

        Assert.Equal(command.Kind, fromJson.Kind);
        Assert.Equal(json, JsonSerializer.Serialize(fromJson, OutboxJsonContext.Default.OutboxCommand));
        Assert.Equal(json, JsonSerializer.Serialize(fromBinary!, OutboxJsonContext.Default.OutboxCommand));
    }

    [Fact]
    public void DeliverAt_RoundTripsThroughJsonAndOrleansSerializationAndIsOmittedWhenUnset()
    {
        var deliverAt = new DateTimeOffset(2040, 1, 2, 3, 4, 5, TimeSpan.FromHours(2));
        var scheduled = new InboxCommand { Kind = InboxCommandKind.Accept, Message = InboxMessage("scheduled", deliverAt) };
        var immediate = new InboxCommand { Kind = InboxCommandKind.Accept, Message = InboxMessage("immediate") };
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer<InboxCommand>>();

        var json = JsonSerializer.Serialize(scheduled, InboxJsonContext.Default.InboxCommand);
        var fromJson = JsonSerializer.Deserialize(json, InboxJsonContext.Default.InboxCommand)!.Message!.Value.Envelope.DeliverAt;
        var fromBinary = serializer.Deserialize(serializer.SerializeToArray(scheduled))!.Message!.Value.Envelope.DeliverAt;
        var immediateJson = JsonSerializer.Serialize(immediate, InboxJsonContext.Default.InboxCommand);

        Assert.Contains("\"DeliverAt\":", json, StringComparison.Ordinal);
        Assert.Equal(deliverAt, fromJson);
        Assert.Equal(deliverAt.Offset, fromJson!.Value.Offset);
        Assert.Equal(deliverAt, fromBinary);
        Assert.Equal(deliverAt.Offset, fromBinary!.Value.Offset);
        Assert.DoesNotContain("DeliverAt", immediateJson, StringComparison.Ordinal);
        Assert.Null(JsonSerializer.Deserialize(immediateJson, InboxJsonContext.Default.InboxCommand)!.Message!.Value.Envelope.DeliverAt);
        Assert.Null(serializer.Deserialize(serializer.SerializeToArray(immediate))!.Message!.Value.Envelope.DeliverAt);
        Assert.DoesNotContain("DeliverAt",
            JsonSerializer.Serialize(new OutboxCommand { Kind = OutboxCommandKind.Enqueue, Message = OutboxMessage("immediate"), State = new() { EnqueuedAt = Now } },
                OutboxJsonContext.Default.OutboxCommand),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReferenceEnqueue_CarriesNoMessageInJsonAndOrleansSerialization()
    {
        var command = new OutboxCommand { Kind = OutboxCommandKind.Enqueue, Reference = Reference("reference"), State = new() { EnqueuedAt = Now } };
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer<OutboxCommand>>();

        var json = JsonSerializer.Serialize(command, OutboxJsonContext.Default.OutboxCommand);
        var fromJson = JsonSerializer.Deserialize(json, OutboxJsonContext.Default.OutboxCommand)!;
        var fromBinary = serializer.Deserialize(serializer.SerializeToArray(command))!;

        Assert.DoesNotContain("Payload", json, StringComparison.Ordinal);
        foreach (var recovered in new[] { fromJson, fromBinary })
        {
            Assert.Null(recovered.Message);
            Assert.Equal(HierarchicalKey.Create("orders", "reference"), recovered.Reference!.Value.MessageId);
        }
    }

    private static InboxMessage InboxMessage(string id, DateTimeOffset? deliverAt = null) => new() { Envelope = Envelope(id, deliverAt) };

    private static OutboxMessage OutboxMessage(string id, DateTimeOffset? deliverAt = null) => new() { Envelope = Envelope(id, deliverAt), ReceiverId = Receiver };

    private static OutboxMessageReference Reference(string id) => new() { MessageId = HierarchicalKey.Create("orders", id) };

    private static Envelope Envelope(string id, DateTimeOffset? deliverAt = null) => new()
    {
        MessageId = MessageId.Create("orders", id),
        Payload = [1, 2, 3],
        Subject = "orders.v1",
        Headers = new(StringComparer.Ordinal) { ["trace"] = [4, 5] },
        DeliverAt = deliverAt
    };

    private static InboxCommand[] CreateInboxCommands()
    {
        var deadLetter = new InboxDeadLetter { Message = InboxMessage("dead"), DeadLetteredAt = Now, Reason = "failed", AttemptCount = 3 };
        var rejection = new InboxRejection { Reason = "failed" };
        return
        [
            new() { Kind = InboxCommandKind.Accept, Message = InboxMessage("accept"), State = new() },
            new() { Kind = InboxCommandKind.Accept, Message = InboxMessage("scheduled", Now), State = new() },
            new() { Kind = InboxCommandKind.Retry, MessageId = MessageId.Create("orders", "retry"), State = new() { AttemptCount = 2, NextAttemptAt = Now, LastError = "boom" } },
            new() { Kind = InboxCommandKind.Complete, MessageId = MessageId.Create("orders", "done"), ProcessedAt = Now, DeadLetter = deadLetter },
            new() { Kind = InboxCommandKind.Complete, MessageId = MessageId.Create("orders", "dead"), ProcessedAt = Now, DeadLetter = deadLetter, Rejection = rejection },
            new() { Kind = InboxCommandKind.RemoveDeadLetters, RemovedKeys = [MessageId.Create("orders", "dead")] },
            new()
            {
                Kind = InboxCommandKind.Snapshot,
                Snapshot = new()
                {
                    Messages = [new() { Message = InboxMessage("pending"), State = new() { AttemptCount = 1 } }, new() { Message = InboxMessage("scheduled", Now), State = new() }],
                    Processed =
                    [
                        new() { MessageId = MessageId.Create("orders", "done"), ProcessedAt = Now },
                        new() { MessageId = MessageId.Create("orders", "dead"), ProcessedAt = Now, Rejection = rejection }
                    ],
                    DeadLetters = [deadLetter]
                }
            }
        ];
    }

    private static OutboxCommand[] CreateOutboxCommands()
    {
        var deadLetter = new OutboxDeadLetter { Message = OutboxMessage("dead"), DeadLetteredAt = Now, Reason = "rejected", AttemptCount = 3 };
        return
        [
            new() { Kind = OutboxCommandKind.Enqueue, Message = OutboxMessage("enqueue"), State = new() { EnqueuedAt = Now } },
            new() { Kind = OutboxCommandKind.Enqueue, Message = OutboxMessage("scheduled", Now), State = new() { EnqueuedAt = Now } },
            new() { Kind = OutboxCommandKind.Enqueue, Reference = Reference("reference"), State = new() { EnqueuedAt = Now } },
            new() { Kind = OutboxCommandKind.Delivery, MessageId = HierarchicalKey.Create("orders", "retry"), State = new() { AttemptCount = 2, NextAttemptAt = Now, LastError = "boom", EnqueuedAt = Now } },
            new() { Kind = OutboxCommandKind.Delivery, MessageId = HierarchicalKey.Create("orders", "dead"), DeadLetter = deadLetter },
            new() { Kind = OutboxCommandKind.RemoveDeadLetters, RemovedDeadLetters = [HierarchicalKey.Create("orders", "dead")] },
            new()
            {
                Kind = OutboxCommandKind.Snapshot,
                Snapshot = new()
                {
                    Messages =
                    [
                        new() { Message = OutboxMessage("pending", Now), State = new() { AttemptCount = 1, EnqueuedAt = Now } },
                        new() { Reference = Reference("pending-reference"), State = new() { AttemptCount = 2, EnqueuedAt = Now } }
                    ],
                    DeadLetters = [deadLetter]
                }
            }
        ];
    }
}
