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
        var fromJson = JsonSerializer.Deserialize(json, InboxJsonContext.Default.InboxCommand)!;
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer<InboxCommand>>();
        var fromBinary = serializer.Deserialize(serializer.SerializeToArray(command));

        Assert.Equal(command.Kind, fromJson.Kind);
        Assert.Equal(json, JsonSerializer.Serialize(fromJson, InboxJsonContext.Default.InboxCommand));
        Assert.Equal(json, JsonSerializer.Serialize(fromBinary!, InboxJsonContext.Default.InboxCommand));
    }

    private static InboxMessage InboxMessage(string id) => new() { Envelope = Envelope(id) };

    private static Envelope Envelope(string id) => new()
    {
        MessageId = HierarchicalKey.Create("orders", id),
        Payload = [1, 2, 3],
        Subject = "orders.v1",
        Headers = new(StringComparer.Ordinal) { ["trace"] = [4, 5] }
    };

    private static InboxCommand[] CreateInboxCommands()
    {
        var deadLetter = new InboxDeadLetter { Message = InboxMessage("dead"), DeadLetteredAt = Now, Reason = "failed", AttemptCount = 3 };
        return
        [
            new() { Kind = InboxCommandKind.Accept, Message = InboxMessage("accept"), State = new() },
            new() { Kind = InboxCommandKind.Retry, MessageId = HierarchicalKey.Create("orders", "retry"), State = new() { AttemptCount = 2, NextAttemptAt = Now, LastError = "boom" } },
            new() { Kind = InboxCommandKind.Complete, MessageId = HierarchicalKey.Create("orders", "done"), ProcessedAt = Now, DeadLetter = deadLetter },
            new() { Kind = InboxCommandKind.RemoveDeadLetters, RemovedKeys = [HierarchicalKey.Create("orders", "dead")] },
            new()
            {
                Kind = InboxCommandKind.Snapshot,
                Snapshot = new()
                {
                    Messages = [new() { Message = InboxMessage("pending"), State = new() { AttemptCount = 1 } }],
                    Processed = [new() { MessageId = HierarchicalKey.Create("orders", "done"), ProcessedAt = Now }],
                    DeadLetters = [deadLetter]
                }
            }
        ];
    }
}
