using System.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Binary codec for <see cref="DurableQueueEntry{T}"/> log entries,
/// preserving the legacy Orleans binary wire format.
/// </summary>
internal sealed class OrleansBinaryQueueEntryCodec<T>(
    ILogDataCodec<T> codec) : ILogEntryCodec<DurableQueueEntry<T>>
{
    private const byte FormatVersion = 0;
    private const uint EnqueueCommand = 0;
    private const uint DequeueCommand = 1;
    private const uint ClearCommand = 2;
    private const uint SnapshotCommand = 3;

    /// <inheritdoc/>
    public void Write(DurableQueueEntry<T> entry, IBufferWriter<byte> output)
    {
        WriteVersionByte(output);

        switch (entry)
        {
            case QueueEnqueueEntry<T>(var item):
                VarIntHelper.WriteVarUInt32(output, EnqueueCommand);
                codec.Write(item, output);
                break;
            case QueueDequeueEntry<T>:
                VarIntHelper.WriteVarUInt32(output, DequeueCommand);
                break;
            case QueueClearEntry<T>:
                VarIntHelper.WriteVarUInt32(output, ClearCommand);
                break;
            case QueueSnapshotEntry<T>(var items):
                VarIntHelper.WriteVarUInt32(output, SnapshotCommand);
                VarIntHelper.WriteVarUInt32(output, (uint)items.Count);
                foreach (var item in items)
                {
                    codec.Write(item, output);
                }

                break;
            default:
                throw new NotSupportedException($"Unsupported entry type: {entry.GetType()}");
        }
    }

    /// <inheritdoc/>
    public DurableQueueEntry<T> Read(ReadOnlySequence<byte> input)
    {
        var reader = new SequenceReader<byte>(input);
        ReadVersionByte(ref reader);

        var command = VarIntHelper.ReadVarUInt32(ref reader);
        var remaining = input.Slice(reader.Consumed);

        return command switch
        {
            EnqueueCommand => new QueueEnqueueEntry<T>(codec.Read(remaining, out _)),
            DequeueCommand => new QueueDequeueEntry<T>(),
            ClearCommand => new QueueClearEntry<T>(),
            SnapshotCommand => ReadSnapshot(remaining),
            _ => throw new NotSupportedException($"Command type {command} is not supported"),
        };
    }

    private DurableQueueEntry<T> ReadSnapshot(ReadOnlySequence<byte> remaining)
    {
        var reader = new SequenceReader<byte>(remaining);
        var count = (int)VarIntHelper.ReadVarUInt32(ref reader);
        remaining = remaining.Slice(reader.Consumed);

        var items = new List<T>(count);
        for (var i = 0; i < count; i++)
        {
            var item = codec.Read(remaining, out var consumed);
            remaining = remaining.Slice(consumed);
            items.Add(item);
        }

        return new QueueSnapshotEntry<T>(items);
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
