using System.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Binary codec for durable queue log entries, preserving the legacy Orleans binary wire format.
/// </summary>
internal sealed class OrleansBinaryQueueOperationCodec<T>(
    ILogValueCodec<T> codec) : IDurableQueueOperationCodec<T>
{
    private const byte FormatVersion = 0;
    private const uint EnqueueCommand = 0;
    private const uint DequeueCommand = 1;
    private const uint ClearCommand = 2;
    private const uint SnapshotCommand = 3;

    /// <inheritdoc/>
    public void WriteEnqueue(T item, LogStreamWriter writer) =>
        DurableOperationCodecWriter.Write(writer, output => WriteEnqueuePayload(item, output));

    private void WriteEnqueuePayload(T item, IBufferWriter<byte> output)
    {
        WriteVersionByte(output);
        VarIntHelper.WriteVarUInt32(output, EnqueueCommand);
        codec.Write(item, output);
    }

    /// <inheritdoc/>
    public void WriteDequeue(LogStreamWriter writer) =>
        DurableOperationCodecWriter.Write(writer, WriteDequeuePayload);

    private static void WriteDequeuePayload(IBufferWriter<byte> output)
    {
        WriteVersionByte(output);
        VarIntHelper.WriteVarUInt32(output, DequeueCommand);
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
    public void Apply(ReadOnlySequence<byte> input, IDurableQueueOperationHandler<T> consumer)
    {
        var reader = new OrleansBinaryOperationReader(input);
        var command = reader.ReadCommand();

        switch (command)
        {
            case EnqueueCommand:
                var item = reader.ReadValue("item", codec);
                reader.EnsureEnd();
                consumer.ApplyEnqueue(item);
                break;
            case DequeueCommand:
                reader.EnsureEnd();
                consumer.ApplyDequeue();
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

    private void ApplySnapshot(ref OrleansBinaryOperationReader reader, IDurableQueueOperationHandler<T> consumer)
    {
        var count = reader.ReadSnapshotCount();

        consumer.Reset(count);
        for (var i = 0; i < count; i++)
        {
            var item = reader.ReadValue("item", codec);
            consumer.ApplyEnqueue(item);
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
