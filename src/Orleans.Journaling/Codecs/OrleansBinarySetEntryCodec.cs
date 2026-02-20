using System.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Binary codec for <see cref="DurableSetEntry{T}"/> log entries,
/// preserving the legacy Orleans binary wire format.
/// </summary>
internal sealed class OrleansBinarySetEntryCodec<T>(
    ILogDataCodec<T> codec) : ILogEntryCodec<DurableSetEntry<T>>
{
    private const byte FormatVersion = 0;
    private const uint AddCommand = 0;
    private const uint RemoveCommand = 1;
    private const uint ClearCommand = 2;
    private const uint SnapshotCommand = 3;

    /// <inheritdoc/>
    public void Write(DurableSetEntry<T> entry, IBufferWriter<byte> output)
    {
        WriteVersionByte(output);

        switch (entry)
        {
            case SetAddEntry<T>(var item):
                VarIntHelper.WriteVarUInt32(output, AddCommand);
                codec.Write(item, output);
                break;
            case SetRemoveEntry<T>(var item):
                VarIntHelper.WriteVarUInt32(output, RemoveCommand);
                codec.Write(item, output);
                break;
            case SetClearEntry<T>:
                VarIntHelper.WriteVarUInt32(output, ClearCommand);
                break;
            case SetSnapshotEntry<T>(var items):
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
    public DurableSetEntry<T> Read(ReadOnlySequence<byte> input)
    {
        var reader = new SequenceReader<byte>(input);
        ReadVersionByte(ref reader);

        var command = VarIntHelper.ReadVarUInt32(ref reader);
        var remaining = input.Slice(reader.Consumed);

        return command switch
        {
            AddCommand => new SetAddEntry<T>(codec.Read(remaining, out _)),
            RemoveCommand => new SetRemoveEntry<T>(codec.Read(remaining, out _)),
            ClearCommand => new SetClearEntry<T>(),
            SnapshotCommand => ReadSnapshot(remaining),
            _ => throw new NotSupportedException($"Command type {command} is not supported"),
        };
    }

    private DurableSetEntry<T> ReadSnapshot(ReadOnlySequence<byte> remaining)
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

        return new SetSnapshotEntry<T>(items);
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
