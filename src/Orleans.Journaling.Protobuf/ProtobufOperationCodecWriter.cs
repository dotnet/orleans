using System.Buffers;

namespace Orleans.Journaling.Protobuf;

internal static class ProtobufOperationCodecWriter
{
    public static void Write(LogStreamWriter writer, Action<IBufferWriter<byte>> write)
    {
        using var entry = writer.BeginEntry();
        write(entry.Writer);
        entry.Commit();
    }
}
