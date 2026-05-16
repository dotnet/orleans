using System.Text.Json;

namespace Orleans.Journaling.Json;

internal static class JsonCommandWriter
{
    public static void Write<TArg>(JournalStreamWriter writer, TArg argument, Action<Utf8JsonWriter, TArg> writeArrayElementsTo)
    {
        ArgumentNullException.ThrowIfNull(writeArrayElementsTo);
        using var entry = writer.BeginEntry();
        using (var jsonWriter = new Utf8JsonWriter(entry.Writer))
        {
            jsonWriter.WriteStartArray();
            writeArrayElementsTo(jsonWriter, argument);
            jsonWriter.WriteEndArray();
            jsonWriter.Flush();
        }

        entry.Commit();
    }
}
