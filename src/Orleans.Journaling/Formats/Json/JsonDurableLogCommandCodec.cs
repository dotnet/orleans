using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Orleans.Journaling.Json;

/// <summary>
/// JSON codec for durable log journal entries.
/// </summary>
/// <remarks>
/// Commands are encoded as <c>["append", item, ...]</c>, <c>["trimBefore", sequence]</c>, <c>["clear"]</c>, <c>["seal"]</c>,
/// and <c>["snapshot", firstSequence, resetSequence, sealed, [items]]</c>.
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
    public void WriteClear(JournalStreamWriter writer) =>
        JsonCommandWriter.Write(writer, 0, static (jsonWriter, _) => jsonWriter.WriteStringValue(JsonJournalEntryCommands.Clear));

    /// <inheritdoc/>
    public void WriteSeal(JournalStreamWriter writer) =>
        JsonCommandWriter.Write(writer, 0, static (jsonWriter, _) => jsonWriter.WriteStringValue(JsonJournalEntryCommands.Seal));

    /// <inheritdoc/>
    public void WriteSnapshot(long firstSequence, long resetSequence, bool isSealed, IReadOnlyCollection<T> items, JournalStreamWriter writer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(resetSequence);
        ArgumentOutOfRangeException.ThrowIfLessThan(firstSequence, resetSequence);
        ArgumentNullException.ThrowIfNull(items);
        JsonCommandWriter.Write(
            writer,
            (typeInfo: _itemTypeInfo, firstSequence, resetSequence, isSealed, items),
            static (jsonWriter, command) =>
            {
                var count = CollectionCodecHelpers.GetSnapshotCount(command.items);

                jsonWriter.WriteStringValue(JsonJournalEntryCommands.Snapshot);
                jsonWriter.WriteNumberValue(command.firstSequence);
                jsonWriter.WriteNumberValue(command.resetSequence);
                jsonWriter.WriteBooleanValue(command.isSealed);
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
                var sequence = ReadSequence(ref reader, 1, JsonJournalEntryFields.Sequence);
                reader.EnsureEnd(2);
                consumer.ApplyTrimBefore(sequence);
                break;
            case JsonJournalEntryCommands.Clear:
                reader.EnsureEnd(1);
                consumer.ApplyClear();
                break;
            case JsonJournalEntryCommands.Seal:
                reader.EnsureEnd(1);
                consumer.ApplySeal();
                break;
            case JsonJournalEntryCommands.Snapshot:
                var firstSequence = ReadSequence(ref reader, 1, JsonJournalEntryFields.Sequence);
                var resetSequence = ReadSequence(ref reader, 2, JsonJournalEntryFields.ResetSequence);
                var isSealed = reader.ReadBoolean(3, JsonJournalEntryFields.Sealed);
                var count = reader.StartArray(4, JsonJournalEntryFields.Items);
                consumer.Reset(firstSequence, resetSequence, isSealed, count);
                while (reader.ReadArrayItem(JsonJournalEntryFields.Items))
                {
                    consumer.ApplyAppend(reader.DeserializeCurrentAllowNull(JsonJournalEntryFields.Item, _itemTypeInfo)!);
                }

                reader.EnsureEnd(5);
                break;
            default:
                reader.EnsureEnd(1);
                throw new NotSupportedException($"Command type '{command}' is not supported");
        }
    }

    private static long ReadSequence(ref JsonCommandReader reader, int index, string operandName)
    {
        var value = reader.ReadUInt64(index, operandName);
        if (value > long.MaxValue)
        {
            throw new JsonException($"JSON journal command operand '{operandName}' exceeds the maximum supported value {long.MaxValue}.");
        }

        return (long)value;
    }
}
