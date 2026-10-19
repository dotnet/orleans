using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Orleans.Journaling.Json;

/// <summary>
/// JSON codec for durable log journal entries.
/// </summary>
/// <remarks>
/// Commands are encoded as <c>["epoch", epoch]</c>, <c>["append", item, ...]</c>,
/// <c>["commitDraft", draftId, sequence, item, ...]</c>, <c>["trimBefore", sequence]</c>, <c>["clear"]</c>, <c>["seal"]</c>,
/// and <c>["snapshot", epoch, firstSequence, resetSequence, sealed, [items], [[draftId, sequence, count], ...]]</c>.
/// </remarks>
/// <typeparam name="T">The type of entries in the log.</typeparam>
public sealed class JsonDurableLogCommandCodec<T>(JsonSerializerOptions? options = null)
    : IDurableLogCommandCodec<T>
{
    private readonly JsonTypeInfo<T> _itemTypeInfo = JsonTypeInfoHelpers.GetTypeInfo<T>(options);

    /// <inheritdoc/>
    public void WriteEpoch(long epoch, JournalStreamWriter writer)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(epoch);
        JsonCommandWriter.Write(
            writer,
            epoch,
            static (jsonWriter, epoch) =>
            {
                jsonWriter.WriteStringValue(JsonJournalEntryCommands.Epoch);
                jsonWriter.WriteNumberValue(epoch);
            });
    }

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
            WriteItems(jsonWriter, items);
            jsonWriter.WriteEndArray();
            jsonWriter.Flush();
        }

        entry.Commit();
    }

    /// <inheritdoc/>
    public void WriteDraftCommit(LogDraftId draftId, long sequence, ReadOnlySpan<T> items, JournalStreamWriter writer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        using var entry = writer.BeginEntry();
        using (var jsonWriter = new Utf8JsonWriter(entry.Writer))
        {
            jsonWriter.WriteStartArray();
            jsonWriter.WriteStringValue(JsonJournalEntryCommands.CommitDraft);
            jsonWriter.WriteStringValue(draftId.ToString());
            jsonWriter.WriteNumberValue(sequence);
            WriteItems(jsonWriter, items);
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
    public void WriteSnapshot(
        long epoch,
        long firstSequence,
        long resetSequence,
        bool isSealed,
        IReadOnlyCollection<T> items,
        IReadOnlyCollection<LogDraftCommit> draftCommits,
        JournalStreamWriter writer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(epoch);
        ArgumentOutOfRangeException.ThrowIfNegative(resetSequence);
        ArgumentOutOfRangeException.ThrowIfLessThan(firstSequence, resetSequence);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(draftCommits);
        JsonCommandWriter.Write(
            writer,
            (typeInfo: _itemTypeInfo, epoch, firstSequence, resetSequence, isSealed, items, draftCommits),
            static (jsonWriter, command) =>
            {
                jsonWriter.WriteStringValue(JsonJournalEntryCommands.Snapshot);
                jsonWriter.WriteNumberValue(command.epoch);
                jsonWriter.WriteNumberValue(command.firstSequence);
                jsonWriter.WriteNumberValue(command.resetSequence);
                jsonWriter.WriteBooleanValue(command.isSealed);

                var count = CollectionCodecHelpers.GetSnapshotCount(command.items);
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

                var commitCount = CollectionCodecHelpers.GetSnapshotCount(command.draftCommits);
                jsonWriter.WriteStartArray();
                written = 0;
                foreach (var commit in command.draftCommits)
                {
                    CollectionCodecHelpers.ThrowIfSnapshotItemCountExceeded(commitCount, written);
                    jsonWriter.WriteStartArray();
                    jsonWriter.WriteStringValue(commit.DraftId.ToString());
                    jsonWriter.WriteNumberValue(commit.Sequence);
                    jsonWriter.WriteNumberValue(commit.Count);
                    jsonWriter.WriteEndArray();
                    written++;
                }

                CollectionCodecHelpers.RequireSnapshotItemCount(commitCount, written);
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

    private void WriteItems(Utf8JsonWriter jsonWriter, ReadOnlySpan<T> items)
    {
        foreach (var item in items)
        {
            JsonSerializer.Serialize(jsonWriter, item, _itemTypeInfo);
        }
    }

    private void Apply(ref JsonCommandReader reader, IDurableLogCommandHandler<T> consumer)
    {
        var command = reader.Command;
        switch (command)
        {
            case JsonJournalEntryCommands.Epoch:
                var epoch = ReadSequence(ref reader, 1, JsonJournalEntryFields.Epoch);
                reader.EnsureEnd(2);
                consumer.ApplyEpoch(epoch);
                break;
            case JsonJournalEntryCommands.Append:
                consumer.ApplyAppend(reader.DeserializeAllowNull(1, JsonJournalEntryFields.Item, _itemTypeInfo)!);
                ApplyRemainingItems(ref reader, consumer);
                break;
            case JsonJournalEntryCommands.CommitDraft:
                var draftId = JsonCommandReader.ParseDraftId(reader.ReadString(1, JsonJournalEntryFields.DraftId));
                var draftSequence = ReadSequence(ref reader, 2, JsonJournalEntryFields.Sequence);
                var count = ApplyRemainingItems(ref reader, consumer);
                consumer.ApplyDraftCommit(new(draftId, draftSequence, count));
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
                ApplySnapshot(ref reader, consumer);
                break;
            default:
                reader.EnsureEnd(1);
                throw new NotSupportedException($"Command type '{command}' is not supported");
        }
    }

    private void ApplySnapshot(ref JsonCommandReader reader, IDurableLogCommandHandler<T> consumer)
    {
        var epoch = ReadSequence(ref reader, 1, JsonJournalEntryFields.Epoch);
        var firstSequence = ReadSequence(ref reader, 2, JsonJournalEntryFields.Sequence);
        var resetSequence = ReadSequence(ref reader, 3, JsonJournalEntryFields.ResetSequence);
        var isSealed = reader.ReadBoolean(4, JsonJournalEntryFields.Sealed);
        var count = reader.StartArray(5, JsonJournalEntryFields.Items);
        consumer.Reset(epoch, firstSequence, resetSequence, count);
        while (reader.ReadArrayItem(JsonJournalEntryFields.Items))
        {
            consumer.ApplyAppend(reader.DeserializeCurrentAllowNull(JsonJournalEntryFields.Item, _itemTypeInfo)!);
        }

        reader.StartArray(6, JsonJournalEntryFields.DraftCommits);
        while (reader.ReadArrayItem(JsonJournalEntryFields.DraftCommits))
        {
            consumer.ApplyDraftCommit(reader.ReadCurrentDraftCommit());
        }

        reader.EnsureEnd(7);
        if (isSealed)
        {
            consumer.ApplySeal();
        }
    }

    // Applies the items which follow the command's fixed operands and returns their number.
    private int ApplyRemainingItems(ref JsonCommandReader reader, IDurableLogCommandHandler<T> consumer)
    {
        var count = 0;
        while (reader.TryReadNextOperand())
        {
            consumer.ApplyAppend(reader.DeserializeCurrentAllowNull(JsonJournalEntryFields.Item, _itemTypeInfo)!);
            count++;
        }

        return count;
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
