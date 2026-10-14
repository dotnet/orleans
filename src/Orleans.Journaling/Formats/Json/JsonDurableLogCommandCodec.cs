using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Orleans.Journaling.Json;

/// <summary>
/// JSON codec for durable log journal entries.
/// </summary>
/// <remarks>
/// Commands are encoded as <c>["append", item, ...]</c>, <c>["trimBefore", sequence]</c>, and
/// <c>["snapshot", firstSequence, [items]]</c>.
/// </remarks>
/// <typeparam name="T">The type of entries in the log.</typeparam>
public sealed class JsonDurableLogCommandCodec<T>(JsonSerializerOptions? options = null)
    : IDurableLogCommandCodec<T>
{
    private readonly JsonTypeInfo<T> _itemTypeInfo = JsonTypeInfoHelpers.GetTypeInfo<T>(options);

    /// <inheritdoc/>
    public void WriteAppend(ReadOnlySpan<T> items, JournalStreamWriter writer)
    {
        if (items.IsEmpty)
        {
            throw new ArgumentException("An append command requires at least one item.", nameof(items));
        }

        using var entry = writer.BeginEntry();
        using (var jsonWriter = new Utf8JsonWriter(entry.Writer))
        {
            jsonWriter.WriteStartArray();
            jsonWriter.WriteStringValue(JsonJournalEntryCommands.Append);
            foreach (var item in items)
            {
                JsonSerializer.Serialize(jsonWriter, item, _itemTypeInfo);
            }

            jsonWriter.WriteEndArray();
            jsonWriter.Flush();
        }

        entry.Commit();
    }

    /// <inheritdoc/>
    public void WriteTrimBefore(long sequence, JournalStreamWriter writer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        JsonCommandWriter.Write(
            writer,
            sequence,
            static (jsonWriter, sequence) =>
            {
                jsonWriter.WriteStringValue(JsonJournalEntryCommands.TrimBefore);
                jsonWriter.WriteNumberValue(sequence);
            });
    }

    /// <inheritdoc/>
    public void WriteSnapshot(long firstSequence, IReadOnlyCollection<T> items, JournalStreamWriter writer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(firstSequence);
        ArgumentNullException.ThrowIfNull(items);
        JsonCommandWriter.Write(
            writer,
            (typeInfo: _itemTypeInfo, firstSequence, items),
            static (jsonWriter, command) =>
            {
                var count = CollectionCodecHelpers.GetSnapshotCount(command.items);

                jsonWriter.WriteStringValue(JsonJournalEntryCommands.Snapshot);
                jsonWriter.WriteNumberValue(command.firstSequence);
                jsonWriter.WriteStartArray();
                var written = 0;
                foreach (var item in command.items)
                {
                    CollectionCodecHelpers.ThrowIfSnapshotItemCountExceeded(count, written);
                    JsonSerializer.Serialize(jsonWriter, item, command.typeInfo);
                    written++;
                }

                CollectionCodecHelpers.RequireSnapshotItemCount(count, written);
                jsonWriter.WriteEndArray();
            });
    }

    /// <inheritdoc/>
    public void Apply(JournalBufferReader input, IDurableLogCommandHandler<T> consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        var reader = new JsonCommandReader(input);
        try
        {
            Apply(ref reader, consumer);
        }
        finally
        {
            reader.Dispose();
        }
    }

    private void Apply(ref JsonCommandReader reader, IDurableLogCommandHandler<T> consumer)
    {
        var command = reader.Command;
        switch (command)
        {
            case JsonJournalEntryCommands.Append:
                consumer.ApplyAppend(reader.DeserializeAllowNull(1, JsonJournalEntryFields.Item, _itemTypeInfo)!);
                while (reader.TryReadNextOperand())
                {
                    consumer.ApplyAppend(reader.DeserializeCurrentAllowNull(JsonJournalEntryFields.Item, _itemTypeInfo)!);
                }

                break;
            case JsonJournalEntryCommands.TrimBefore:
                var sequence = ReadSequence(ref reader, 1);
                reader.EnsureEnd(2);
                consumer.ApplyTrimBefore(sequence);
                break;
            case JsonJournalEntryCommands.Snapshot:
                var firstSequence = ReadSequence(ref reader, 1);
                var count = reader.StartArray(2, JsonJournalEntryFields.Items);
                consumer.Reset(firstSequence, count);
                while (reader.ReadArrayItem(JsonJournalEntryFields.Items))
                {
                    consumer.ApplyAppend(reader.DeserializeCurrentAllowNull(JsonJournalEntryFields.Item, _itemTypeInfo)!);
                }

                reader.EnsureEnd(3);
                break;
            default:
                reader.EnsureEnd(1);
                throw new NotSupportedException($"Command type '{command}' is not supported");
        }
    }

    private static long ReadSequence(ref JsonCommandReader reader, int index)
    {
        var value = reader.ReadUInt64(index, JsonJournalEntryFields.Sequence);
        if (value > long.MaxValue)
        {
            throw new JsonException($"JSON journal command operand '{JsonJournalEntryFields.Sequence}' exceeds the maximum supported value {long.MaxValue}.");
        }

        return (long)value;
    }
}
