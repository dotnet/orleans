using System.Text.Json;

namespace Orleans.Journaling.Json;

internal static class JsonOperationCodecWriter
{
    public static void Write<TArg>(
        LogStreamWriter writer,
        TArg argument,
        Action<Utf8JsonWriter, TArg> writeJsonArrayElements)
    {
        var formattedEntry = JsonFormattedLogEntry.Create(argument, writeJsonArrayElements);
        if (writer.TryAppendFormattedEntry(formattedEntry))
        {
            return;
        }

        using var entry = writer.BeginEntry();
        using (var jsonWriter = new Utf8JsonWriter(entry.Writer))
        {
            formattedEntry.WriteTo(jsonWriter);
            jsonWriter.Flush();
        }

        entry.Commit();
    }
}
