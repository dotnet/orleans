using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Session;

namespace Orleans.Messaging.Tests.Support;

internal static class OutboxTestMessages
{
    public static OutboxMessage Create(HierarchicalKey messageId, GrainId receiver, ReadOnlyMemory<byte> payload,
        GrainId? sender, string? subject, Serializer<GrainId> senderSerializer) => new()
        {
            ReceiverId = receiver,
            Envelope = new Envelope
            {
                MessageId = messageId,
                Payload = payload.ToArray(),
                Subject = subject,
                Headers = sender is { } senderId
                    ? new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["sender"] = senderSerializer.SerializeToArray(senderId) }
                    : null
            }
        };

    public static OutboxMessage CreateApplication(IServiceProvider services, HierarchicalKey messageId,
        GrainId sender, GrainId receiver, string subject, object? body) => Create(messageId, receiver,
            TestApplicationProtocol.Encode(services.GetRequiredService<SerializerSessionPool>(), new TestApplicationMessage(subject, body)),
            sender, subject, services.GetRequiredService<Serializer<GrainId>>());

    public static string? Subject(Envelope envelope) => envelope.Subject;
}
