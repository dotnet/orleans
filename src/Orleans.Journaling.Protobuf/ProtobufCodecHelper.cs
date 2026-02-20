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

    /// <summary>
    /// Copies the content of a <see cref="MemoryStream"/> to an <see cref="IBufferWriter{T}"/>.
    /// </summary>
    internal static void CopyToBufferWriter(MemoryStream stream, IBufferWriter<byte> output)
    {
        var data = stream.GetBuffer().AsSpan(0, (int)stream.Length);
        var dest = output.GetSpan(data.Length);
        data.CopyTo(dest);
        output.Advance(data.Length);
    }

    /// <summary>
    /// Skips an unknown field in the protobuf stream based on its wire type.
    /// </summary>
    internal static void SkipField(CodedInputStream cis, WireFormat.WireType wireType)
    {
        switch (wireType)
        {
            case WireFormat.WireType.Varint:
                cis.ReadUInt64();
                break;
            case WireFormat.WireType.Fixed64:
                cis.ReadFixed64();
                break;
            case WireFormat.WireType.LengthDelimited:
                cis.ReadBytes();
                break;
            case WireFormat.WireType.Fixed32:
                cis.ReadFixed32();
                break;
            default:
                throw new InvalidOperationException($"Unknown wire type: {wireType}");
        }
    }
}
