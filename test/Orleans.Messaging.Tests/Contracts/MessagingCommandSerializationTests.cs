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

    public static TheoryData<int> InboxCommands => new(Enumerable.Range(0, CreateInboxCommands().Length));

    [Theory]
    [MemberData(nameof(InboxCommands))]
    public void InboxCommand_RoundTripsThroughJsonAndOrleansSerialization(int index)
    {
        var command = CreateInboxCommands()[index];
        var json = JsonSerializer.Serialize(command, InboxJsonContext.Default.InboxCommand);
        var fromJson = JsonSerializer.Deserialize(json, InboxJsonContext.Default.InboxCommand);
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer<InboxCommand>>();
        var fromBinary = serializer.Deserialize(serializer.SerializeToArray(command));

        Assert.StartsWith("{\"kind\":", json, StringComparison.Ordinal);
        Assert.Equal(command.Value!.GetType(), fromJson.Value!.GetType());
        Assert.Equal(command.Value.GetType(), fromBinary.Value!.GetType());
        Assert.DoesNotContain("\"ProcessedAt\"", json, StringComparison.Ordinal);
        Assert.Equal(json, JsonSerializer.Serialize(fromJson, InboxJsonContext.Default.InboxCommand));
        Assert.Equal(json, JsonSerializer.Serialize(fromBinary, InboxJsonContext.Default.InboxCommand));
    }

    [Fact]
    public void DeliverAt_RoundTripsThroughJsonAndOrleansSerializationAndIsOmittedWhenUnset()
    {
        var deliverAt = new DateTimeOffset(2040, 1, 2, 3, 4, 5, TimeSpan.FromHours(2));
        InboxCommand scheduled = new InboxCommand.Accept(InboxMessage("scheduled", deliverAt));
        InboxCommand immediate = new InboxCommand.Accept(InboxMessage("immediate"));
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer<InboxCommand>>();

        var json = JsonSerializer.Serialize(scheduled, InboxJsonContext.Default.InboxCommand);
        var fromJson = ((InboxCommand.Accept)JsonSerializer.Deserialize(json, InboxJsonContext.Default.InboxCommand).Value!).Message.Envelope.DeliverAt;
        var fromBinary = ((InboxCommand.Accept)serializer.Deserialize(serializer.SerializeToArray(scheduled)).Value!).Message.Envelope.DeliverAt;
        var immediateJson = JsonSerializer.Serialize(immediate, InboxJsonContext.Default.InboxCommand);

        Assert.Contains("\"DeliverAt\":", json, StringComparison.Ordinal);
        Assert.Equal(deliverAt, fromJson);
        Assert.Equal(deliverAt.Offset, fromJson!.Value.Offset);
        Assert.Equal(deliverAt, fromBinary);
        Assert.Equal(deliverAt.Offset, fromBinary!.Value.Offset);
        Assert.DoesNotContain("DeliverAt", immediateJson, StringComparison.Ordinal);
        Assert.Null(((InboxCommand.Accept)JsonSerializer.Deserialize(immediateJson, InboxJsonContext.Default.InboxCommand).Value!).Message.Envelope.DeliverAt);
        Assert.Null(((InboxCommand.Accept)serializer.Deserialize(serializer.SerializeToArray(immediate)).Value!).Message.Envelope.DeliverAt);
    }

    private static InboxMessage InboxMessage(string id, DateTimeOffset? deliverAt = null) => new() { Envelope = Envelope(id, deliverAt), SentAt = Now };

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
            new InboxCommand.Accept(InboxMessage("accept")),
            new InboxCommand.Accept(InboxMessage("scheduled", Now)),
            new InboxCommand.Retry(MessageId.Create("orders", "retry"), new() { AttemptCount = 2, NextAttemptAt = Now }),
            new InboxCommand.Complete(MessageId.Create("orders", "done"), Now, deadLetter, null),
            new InboxCommand.Complete(MessageId.Create("orders", "dead"), Now, deadLetter, rejection),
            new InboxCommand.Complete(MessageId.Create("orders", "done"), Now.AddHours(1), null, null),
            new InboxCommand.Complete(MessageId.Create("orders", "dead"), Now.AddHours(1), null, rejection),
            new InboxCommand.RemoveDeadLetters([MessageId.Create("orders", "dead")]),
            new InboxCommand.AdvanceAdmissionFloor(Now),
            InboxCommand.CleanStop.Instance,
            InboxCommand.Resume.Instance,
            new InboxCommand.Snapshot(
                [new() { Message = InboxMessage("pending"), State = new() { AttemptCount = 1 } }, new() { Message = InboxMessage("scheduled", Now), State = new() }],
                [
                    new() { MessageId = MessageId.Create("orders", "done"), SentAt = Now },
                    new() { MessageId = MessageId.Create("orders", "dead"), SentAt = Now, Rejection = rejection }
                ],
                [deadLetter],
                Now,
                stoppedCleanly: true)
        ];
    }
}
