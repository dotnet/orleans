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
        var reader = new SequenceReader<byte>(input);
        ReadVersionByte(ref reader);

        var command = VarIntHelper.ReadVarUInt32(ref reader);
        var remaining = input.Slice(reader.Consumed);

        switch (command)
        {
            case EnqueueCommand:
                consumer.ApplyEnqueue(codec.Read(remaining, out _));
                break;
            case DequeueCommand:
                consumer.ApplyDequeue();
                break;
            case ClearCommand:
                consumer.ApplyClear();
                break;
            case SnapshotCommand:
                ApplySnapshot(remaining, consumer);
                break;
            default:
                throw new NotSupportedException($"Command type {command} is not supported");
        }
    }

    private void ApplySnapshot(ReadOnlySequence<byte> remaining, IDurableQueueOperationHandler<T> consumer)
    {
        var reader = new SequenceReader<byte>(remaining);
        var count = CollectionCodecHelpers.ReadSnapshotCount(ref reader);
        remaining = remaining.Slice(reader.Consumed);

        consumer.Reset(count);
        for (var i = 0; i < count; i++)
        {
            var item = codec.Read(remaining, out var consumed);
            remaining = remaining.Slice(consumed);
            consumer.ApplyEnqueue(item);
        }
    }

    private static void WriteVersionByte(IBufferWriter<byte> output)
    {
        var span = output.GetSpan(1);
        span[0] = FormatVersion;
        output.Advance(1);
    }

    private static void ReadVersionByte(ref SequenceReader<byte> reader)
    {
        if (!reader.TryRead(out var version) || version != FormatVersion)
        {
            throw new NotSupportedException($"Unsupported format version: {version}");
        }
    }
}
