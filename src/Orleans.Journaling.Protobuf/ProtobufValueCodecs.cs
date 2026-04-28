using System.Buffers;
using System.Text;
using Google.Protobuf;

namespace Orleans.Journaling.Protobuf;

internal interface IProtobufValueCodec<T>
{
    byte[] ToBytes(T value);

    T FromBytes(ReadOnlySequence<byte> bytes);
}

internal sealed class ProtobufStringValueCodec : IProtobufValueCodec<string>
{
    public static ProtobufStringValueCodec Instance { get; } = new();

    public byte[] ToBytes(string value) => Encoding.UTF8.GetBytes(value);

    public string FromBytes(ReadOnlySequence<byte> bytes) => Encoding.UTF8.GetString(bytes.ToArray());
}

internal sealed class ProtobufMessageValueCodec<T>(MessageParser<T> parser) : IProtobufValueCodec<T>
    where T : IMessage<T>
{
    public byte[] ToBytes(T value) => value.ToByteArray();

    public T FromBytes(ReadOnlySequence<byte> bytes) => parser.ParseFrom(bytes.ToArray());
}
