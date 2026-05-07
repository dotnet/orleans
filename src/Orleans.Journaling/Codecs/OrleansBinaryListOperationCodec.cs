using System.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Binary codec for durable list log entries, preserving the legacy Orleans binary wire format.
/// </summary>
internal sealed class OrleansBinaryListOperationCodec<T>(
    ILogValueCodec<T> codec) : IDurableListOperationCodec<T>
{
    private const byte FormatVersion = 0;
    private const uint AddCommand = 0;
    private const uint SetCommand = 1;
    private const uint InsertCommand = 2;
    private const uint RemoveCommand = 3;
    private const uint ClearCommand = 4;
    private const uint SnapshotCommand = 5;

    /// <inheritdoc/>
    public void WriteAdd(T item, LogStreamWriter writer) =>
        DurableOperationCodecWriter.Write(writer, output => WriteAddPayload(item, output));

    private void WriteAddPayload(T item, IBufferWriter<byte> output)
    {
        WriteVersionByte(output);
        VarIntHelper.WriteVarUInt32(output, AddCommand);
        codec.Write(item, output);
    }

    /// <inheritdoc/>
    public void WriteSet(int index, T item, LogStreamWriter writer) =>
        DurableOperationCodecWriter.Write(writer, output => WriteSetPayload(index, item, output));

    private void WriteSetPayload(int index, T item, IBufferWriter<byte> output)
    {
        WriteVersionByte(output);
        VarIntHelper.WriteVarUInt32(output, SetCommand);
        VarIntHelper.WriteVarUInt32(output, (uint)index);
        codec.Write(item, output);
    }

    /// <inheritdoc/>
    public void WriteInsert(int index, T item, LogStreamWriter writer) =>
        DurableOperationCodecWriter.Write(writer, output => WriteInsertPayload(index, item, output));

    private void WriteInsertPayload(int index, T item, IBufferWriter<byte> output)
    {
        WriteVersionByte(output);
        VarIntHelper.WriteVarUInt32(output, InsertCommand);
        VarIntHelper.WriteVarUInt32(output, (uint)index);
        codec.Write(item, output);
    }

    /// <inheritdoc/>
    public void WriteRemoveAt(int index, LogStreamWriter writer) =>
        DurableOperationCodecWriter.Write(writer, output => WriteRemoveAtPayload(index, output));

    private static void WriteRemoveAtPayload(int index, IBufferWriter<byte> output)
    {
        WriteVersionByte(output);
        VarIntHelper.WriteVarUInt32(output, RemoveCommand);
        VarIntHelper.WriteVarUInt32(output, (uint)index);
    }

    /// <inheritdoc/>
    public void WriteClear(LogStreamWriter writer) =>
        DurableOperationCodecWriter.Write(writer, WriteClearPayload);

    private static void WriteClearPayload(IBufferWriter<byte> output)
    {
        WriteVersionByte(output);
        VarIntHelper.WriteVarUInt32(output, ClearCommand);
    }

    /// <inheritdoc/>
    public void WriteSnapshot(IReadOnlyCollection<T> items, LogStreamWriter writer) =>
        DurableOperationCodecWriter.Write(writer, output => WriteSnapshotPayload(items, output));

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
    public void Apply(ReadOnlySequence<byte> input, IDurableListOperationHandler<T> consumer)
    {
        var reader = new OrleansBinaryOperationReader(input);
        var command = reader.ReadCommand();

        switch (command)
        {
            case AddCommand:
                var item = reader.ReadValue("item", codec);
                reader.EnsureEnd();
                consumer.ApplyAdd(item);
                break;
            case SetCommand:
                ApplyIndexAndItem(ref reader, consumer.ApplySet);
                break;
            case InsertCommand:
                ApplyIndexAndItem(ref reader, consumer.ApplyInsert);
                break;
            case RemoveCommand:
                var index = reader.ReadListIndex();
                reader.EnsureEnd();
                consumer.ApplyRemoveAt(index);
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

    private void ApplyIndexAndItem(ref OrleansBinaryOperationReader reader, Action<int, T> apply)
    {
        var index = reader.ReadListIndex();
        var item = reader.ReadValue("item", codec);
        reader.EnsureEnd();
        apply(index, item);
    }

    private void ApplySnapshot(ref OrleansBinaryOperationReader reader, IDurableListOperationHandler<T> consumer)
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
