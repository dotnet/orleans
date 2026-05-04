using System.Buffers;

namespace Orleans.Journaling.MessagePack;

internal static class MessagePackOperationCodecWriter
{
    public static void Write(LogStreamWriter writer, Action<IBufferWriter<byte>> write)
    {
        using var entry = writer.BeginEntry();
        write(entry.Writer);
        entry.Commit();
    }
}
