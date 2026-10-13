using System.Buffers;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Session;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Messaging.Tests.Support;

// Routes and typed values belong to this test application, not to the messaging transport.
[GenerateSerializer]
public sealed record TestApplicationMessage(
    [property: Id(0)] string Route,
    [property: Id(1)] object? Body);

internal static class TestApplicationProtocol
{
    public static MessageId NewMessageId() => MessageId.Create("tests", Guid.NewGuid().ToString("N"));

    public static OutboxMessage Create(SerializerSessionPool sessions, GrainId sender, GrainId receiver,
        string route, object? body, MessageId messageId = default) =>
        new()
        {
            ReceiverId = receiver,
            Envelope = CreateInbox(sessions, sender, route, body, messageId).Envelope
        };

    public static InboxMessage CreateInbox(SerializerSessionPool sessions, GrainId sender,
        string route, object? body, MessageId messageId = default)
    {
        var key = messageId.IsDefault
            ? body is TestMessage message ? message.LogicalId : NewMessageId()
            : messageId;
        return new()
        {
            Envelope = new()
            {
                MessageId = key,
                Payload = Encode(sessions, new TestApplicationMessage(route, body)),
                Subject = route,
                Headers = new(StringComparer.Ordinal)
                {
                    ["sender"] = new Serializer<GrainId>(sessions).SerializeToArray(sender)
                }
            }
        };
    }

    public static byte[] Encode<T>(SerializerSessionPool sessions, T value)
    {
        var output = new ArrayBufferWriter<byte>();
        using var session = sessions.GetSession();
        var writer = Writer.Create(output, session);
        sessions.CodecProvider.GetCodec<T>().WriteField(ref writer, 0, typeof(T), value);
        writer.Commit();
        return output.WrittenSpan.ToArray();
    }

    public static T Decode<T>(SerializerSessionPool sessions, byte[] payload)
    {
        using var session = sessions.GetSession();
        var reader = Reader.Create(payload, session);
        var field = reader.ReadFieldHeader();
        return sessions.CodecProvider.GetCodec<T>().ReadValue(ref reader, field)!;
    }

    public static TestApplicationMessage Read(SerializerSessionPool sessions, InboxMessage message) =>
        Decode<TestApplicationMessage>(sessions, message.Envelope.Payload);

    public static TestApplicationMessage Read(SerializerSessionPool sessions, OutboxMessage message) =>
        Decode<TestApplicationMessage>(sessions, message.Envelope.Payload);

    public static GrainId Sender(SerializerSessionPool sessions, Envelope envelope) =>
        envelope.Headers is { } headers && headers.TryGetValue("sender", out var sender)
            ? new Serializer<GrainId>(sessions).Deserialize(sender) : default;

    public static string? Subject(Envelope envelope) =>
        envelope.Subject;

    public static InboxMessage With(InboxMessage message, SerializerSessionPool sessions,
        MessageId? messageId = null, GrainId? sender = null, string? subject = null,
        byte[]? payload = null, bool removeSender = false, bool removeSubject = false)
    {
        var headers = message.Envelope.Headers is { } existing
            ? new Dictionary<string, byte[]>(existing, StringComparer.Ordinal) : null;
        if (removeSender) headers?.Remove("sender");
        if (sender is { } value)
        {
            (headers ??= new(StringComparer.Ordinal))["sender"] = new Serializer<GrainId>(sessions).SerializeToArray(value);
        }
        return new()
        {
            Envelope = new()
            {
                MessageId = messageId ?? message.Envelope.MessageId,
                Payload = payload ?? message.Envelope.Payload,
                Subject = removeSubject ? null : subject ?? message.Envelope.Subject,
                Headers = headers
            }
        };
    }
}
