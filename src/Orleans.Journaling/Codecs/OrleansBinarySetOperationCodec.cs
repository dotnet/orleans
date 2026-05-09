using System.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Binary codec for durable set journal entries, preserving the legacy Orleans binary wire format.
/// </summary>
internal sealed class OrleansBinarySetOperationCodec<T>(
    IJournalValueCodec<T> codec) : IDurableSetOperationCodec<T>, IOrleansBinaryJournalEntryCodec
{
    private const byte FormatVersion = 0;
    private const uint AddCommand = 0;
    private const uint RemoveCommand = 1;
    private const uint ClearCommand = 2;
    private const uint SnapshotCommand = 3;

    /// <inheritdoc/>
    public void WriteAdd(T item, JournalStreamWriter writer) =>
        JournalOperationWriter.Write(writer, output => WriteAddPayload(item, output));

    private void WriteAddPayload(T item, IBufferWriter<byte> output)
    {
        WriteVersionByte(output);
        VarIntHelper.WriteVarUInt32(output, AddCommand);
        codec.Write(item, output);
    }

    /// <inheritdoc/>
    public void WriteRemove(T item, JournalStreamWriter writer) =>
        JournalOperationWriter.Write(writer, output => WriteRemovePayload(item, output));

    private void WriteRemovePayload(T item, IBufferWriter<byte> output)
    {
        WriteVersionByte(output);
        VarIntHelper.WriteVarUInt32(output, RemoveCommand);
        codec.Write(item, output);
    }

    /// <inheritdoc/>
    public void WriteClear(JournalStreamWriter writer) =>
        JournalOperationWriter.Write(writer, WriteClearPayload);

    private static void WriteClearPayload(IBufferWriter<byte> output)
    {
        WriteVersionByte(output);
        VarIntHelper.WriteVarUInt32(output, ClearCommand);
    }

    /// <inheritdoc/>
    public void WriteSnapshot(IReadOnlyCollection<T> items, JournalStreamWriter writer) =>
        JournalOperationWriter.Write(writer, output => WriteSnapshotPayload(items, output));

    private void WriteSnapshotPayload(IReadOnlyCollection<T> items, IBufferWriter<byte> output)
    {
        var count = CollectionCodecHelpers.GetSnapshotCount(items);
        WriteVersionByte(output);
        VarIntHelper.WriteVarUInt32(output, SnapshotCommand);
        VarIntHelper.WriteVarUInt32(output, (uint)count);
        var written = 0;
        foreach (var item in items)
        {
            CollectionCodecHelpers.ThrowIfSnapshotItemCountExceeded(count, written);
            codec.Write(item, output);
            written++;
        }

        CollectionCodecHelpers.RequireSnapshotItemCount(count, written);
    }

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, IDurableSetOperationHandler<T> consumer)
    {
        var reader = new OrleansBinaryOperationReader(input);
        var command = reader.ReadCommand();

        switch (command)
        {
            case AddCommand:
            {
                var item = reader.ReadValue("item", codec);
                reader.EnsureEnd();
                consumer.ApplyAdd(item);
                break;
            }
            case RemoveCommand:
            {
                var item = reader.ReadValue("item", codec);
                reader.EnsureEnd();
                consumer.ApplyRemove(item);
                break;
            }
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
        Apply(input, DurableOperationHandler.GetRequiredHandler<IDurableSetOperationHandler<T>>(state, this));

    private void ApplySnapshot(ref OrleansBinaryOperationReader reader, IDurableSetOperationHandler<T> consumer)
    {
        var count = reader.ReadSnapshotCount();

        consumer.Reset(count);
        for (var i = 0; i < count; i++)
        {
            var item = reader.ReadValue("item", codec);
            consumer.ApplyAdd(item);
        }

        reader.EnsureEnd();
    }

    private static void WriteVersionByte(IBufferWriter<byte> output)
    {
        var span = output.GetSpan(1);
        span[0] = FormatVersion;
        output.Advance(1);
    }

}
