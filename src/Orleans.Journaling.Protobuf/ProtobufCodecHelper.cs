using System.Buffers;
using Google.Protobuf;

namespace Orleans.Journaling.Protobuf;

/// <summary>
/// Shared helper methods for Protocol Buffers entry codecs.
/// </summary>
internal static class ProtobufCodecHelper
{
    /// <summary>
    /// Serializes a value using the provided codec and returns it as a <see cref="ByteString"/>.
    /// </summary>
    internal static ByteString SerializeValue<T>(ILogDataCodec<T> codec, T value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        codec.Write(value, buffer);
        return ByteString.CopyFrom(buffer.WrittenSpan);
    }

    /// <summary>
    /// Deserializes a value from a <see cref="ByteString"/> using the provided codec.
    /// </summary>
    internal static T DeserializeValue<T>(ILogDataCodec<T> codec, ByteString bytes)
    {
        return codec.Read(new ReadOnlySequence<byte>(bytes.Memory), out _);
    }
}
