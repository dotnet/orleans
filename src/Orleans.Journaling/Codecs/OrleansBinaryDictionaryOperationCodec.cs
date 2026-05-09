using System.Buffers;
using Orleans.Serialization.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Binary codec for durable dictionary journal entries, preserving the legacy Orleans binary wire format.
/// </summary>
internal sealed class OrleansBinaryDictionaryOperationCodec<TKey, TValue>(
    IJournalValueCodec<TKey> keyCodec,
    IJournalValueCodec<TValue> valueCodec) : IDurableDictionaryOperationCodec<TKey, TValue>, IOrleansBinaryJournalEntryCodec where TKey : notnull
{
    private const byte FormatVersion = 0;
    private const uint SetCommand = 0;
    private const uint RemoveCommand = 1;
    private const uint ClearCommand = 2;
    private const uint SnapshotCommand = 3;

    /// <inheritdoc/>
    public void WriteSet(TKey key, TValue value, JournalStreamWriter writer) =>
        JournalOperationWriter.Write(writer, output => WriteSetPayload(key, value, output));

    private void WriteSetPayload(TKey key, TValue value, IBufferWriter<byte> output)
    {
        WriteHeader(output, SetCommand);
        keyCodec.Write(key, output);
        valueCodec.Write(value, output);
    }

    /// <inheritdoc/>
    public void WriteRemove(TKey key, JournalStreamWriter writer) =>
        JournalOperationWriter.Write(writer, output => WriteRemovePayload(key, output));

    private void WriteRemovePayload(TKey key, IBufferWriter<byte> output)
    {
        WriteHeader(output, RemoveCommand);
        keyCodec.Write(key, output);
    }

    /// <inheritdoc/>
    public void WriteClear(JournalStreamWriter writer) =>
        JournalOperationWriter.Write(writer, WriteClearPayload);

    private static void WriteClearPayload(IBufferWriter<byte> output) =>
        WriteHeader(output, ClearCommand);

    /// <inheritdoc/>
    public void WriteSnapshot(IReadOnlyCollection<KeyValuePair<TKey, TValue>> items, JournalStreamWriter writer) =>
        JournalOperationWriter.Write(writer, output => WriteSnapshotPayload(items, output));

    private void WriteSnapshotPayload(IReadOnlyCollection<KeyValuePair<TKey, TValue>> items, IBufferWriter<byte> output)
    {
        var count = CollectionCodecHelpers.GetSnapshotCount(items);
        WriteHeader(output, SnapshotCommand, (uint)count);
        var written = 0;
        foreach (var (key, value) in items)
        {
            CollectionCodecHelpers.ThrowIfSnapshotItemCountExceeded(count, written);
            keyCodec.Write(key, output);
            valueCodec.Write(value, output);
            written++;
        }

        CollectionCodecHelpers.RequireSnapshotItemCount(count, written);
    }

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, IDurableDictionaryOperationHandler<TKey, TValue> consumer)
    {
        var reader = new OrleansBinaryOperationReader(input);
        var command = reader.ReadCommand();

        switch (command)
        {
            case SetCommand:
                ApplySet(ref reader, consumer);
                break;
            case RemoveCommand:
                ApplyRemove(ref reader, consumer);
                break;
            case ClearCommand:
                reader.EnsureEnd();
                consumer.ApplyClear();
                break;
            case SnapshotCommand:
                ApplySnapshot(ref reader, consumer);
                break;
            default:
                throw new NotSupportedException($"Command type {command} is not supported");
        }
    }

    void IOrleansBinaryJournalEntryCodec.Apply(ReadOnlySequence<byte> input, IJournaledState state) =>
        Apply(input, DurableOperationHandler.GetRequiredHandler<IDurableDictionaryOperationHandler<TKey, TValue>>(state, this));

    private void ApplySet(ref OrleansBinaryOperationReader reader, IDurableDictionaryOperationHandler<TKey, TValue> consumer)
    {
        var key = reader.ReadValue("key", keyCodec);
        var value = reader.ReadValue("value", valueCodec);
        reader.EnsureEnd();
        consumer.ApplySet(key, value);
    }

    private void ApplyRemove(ref OrleansBinaryOperationReader reader, IDurableDictionaryOperationHandler<TKey, TValue> consumer)
    {
        var key = reader.ReadValue("key", keyCodec);
        reader.EnsureEnd();
        consumer.ApplyRemove(key);
    }

    private void ApplySnapshot(ref OrleansBinaryOperationReader reader, IDurableDictionaryOperationHandler<TKey, TValue> consumer)
    {
        var count = reader.ReadSnapshotCount();

        consumer.Reset(count);
        for (var i = 0; i < count; i++)
        {
            var key = reader.ReadValue("key", keyCodec);
            var value = reader.ReadValue("value", valueCodec);
            consumer.ApplySet(key, value);
        }

        reader.EnsureEnd();
    }

    private static void WriteHeader(IBufferWriter<byte> output, uint command)
    {
        var writer = Writer.Create(output, session: null!);
        writer.WriteByte(FormatVersion);
        writer.WriteVarUInt32(command);
        writer.Commit();
    }

    private static void WriteHeader(IBufferWriter<byte> output, uint command, uint operand)
    {
        var writer = Writer.Create(output, session: null!);
        writer.WriteByte(FormatVersion);
        writer.WriteVarUInt32(command);
        writer.WriteVarUInt32(operand);
        writer.Commit();
    }

}
