using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Session;
using Orleans.Serialization.WireProtocol;

namespace Orleans.DurableMessaging.Tests.Support;

// Routes and typed values belong to this test application, not to the messaging transport.
[GenerateSerializer]
public sealed record TestApplicationMessage(
    [property: Id(0)] string Route,
    [property: Id(1)] object? Body);

internal static class TestApplicationProtocol
{
    public static DurableEnvelope Create(SerializerSessionPool sessions, GrainId sender, GrainId receiver, string route, object? body) =>
        new()
        {
            MessageId = Guid.NewGuid(),
            SenderId = sender,
            ReceiverId = receiver,
            Payload = Encode(sessions, new TestApplicationMessage(route, body))
        };

    public static ImmutableBuffer Encode<T>(SerializerSessionPool sessions, T value) => ImmutableBuffer.Create(output =>
    {
        using var session = sessions.GetSession();
        var writer = Writer.Create(output, session);
        sessions.CodecProvider.GetCodec<T>().WriteField(ref writer, 0, typeof(T), value);
        writer.Commit();
    });

    public static T Decode<T>(SerializerSessionPool sessions, ImmutableBuffer payload)
    {
        using var session = sessions.GetSession();
        var reader = Reader.Create(new System.Buffers.ReadOnlySequence<byte>(payload.Memory), session);
        var field = reader.ReadFieldHeader();
        return sessions.CodecProvider.GetCodec<T>().ReadValue(ref reader, field)!;
    }

    public static TestApplicationMessage Read(SerializerSessionPool sessions, DurableEnvelope envelope) =>
        Decode<TestApplicationMessage>(sessions, envelope.Payload);
}
