using System.Buffers;
using System.Text.Json;

namespace Orleans.Journaling.Json;

internal static class JsonOperationCodecWriter
{
    public static void Write<TArg>(
        LogWriter writer,
        TArg argument,
        Action<Utf8JsonWriter, TArg> writeJson,
        Action<IBufferWriter<byte>, TArg> writeBytes)
    {
        if (writer.TryAppendFormattedEntry(JsonFormattedLogEntry.Create(argument, writeJson)))
        {
            return;
        }

        using var entry = writer.BeginEntry();
        writeBytes(entry.Writer, argument);
        entry.Commit();
    }
}
