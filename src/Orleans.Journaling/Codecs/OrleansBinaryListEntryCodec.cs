using System.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Binary codec for <see cref="DurableListEntry{T}"/> log entries,
/// preserving the legacy Orleans binary wire format.
/// </summary>
internal sealed class OrleansBinaryListEntryCodec<T>(
    ILogDataCodec<T> codec) : ILogEntryCodec<DurableListEntry<T>>
{
    private const byte FormatVersion = 0;
    private const uint AddCommand = 0;
    private const uint SetCommand = 1;
    private const uint InsertCommand = 2;
    private const uint RemoveCommand = 3;
    private const uint ClearCommand = 4;
    private const uint SnapshotCommand = 5;

    /// <inheritdoc/>
    public void Write(DurableListEntry<T> entry, IBufferWriter<byte> output)
    {
        WriteVersionByte(output);

        switch (entry)
        {
            case ListAddEntry<T>(var item):
                VarIntHelper.WriteVarUInt32(output, AddCommand);
                codec.Write(item, output);
                break;
            case ListSetEntry<T>(var index, var item):
                VarIntHelper.WriteVarUInt32(output, SetCommand);
                VarIntHelper.WriteVarUInt32(output, (uint)index);
                codec.Write(item, output);
                break;
            case ListInsertEntry<T>(var index, var item):
                VarIntHelper.WriteVarUInt32(output, InsertCommand);
                VarIntHelper.WriteVarUInt32(output, (uint)index);
                codec.Write(item, output);
                break;
            case ListRemoveAtEntry<T>(var index):
                VarIntHelper.WriteVarUInt32(output, RemoveCommand);
                VarIntHelper.WriteVarUInt32(output, (uint)index);
                break;
            case ListClearEntry<T>:
                VarIntHelper.WriteVarUInt32(output, ClearCommand);
                break;
            case ListSnapshotEntry<T>(var items):
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
    public DurableListEntry<T> Read(ReadOnlySequence<byte> input)
    {
        var reader = new SequenceReader<byte>(input);
        ReadVersionByte(ref reader);

        var command = VarIntHelper.ReadVarUInt32(ref reader);
        var remaining = input.Slice(reader.Consumed);

        return command switch
        {
            AddCommand => ReadAdd(remaining),
            SetCommand => ReadIndexAndItem(remaining, static (index, item) => new ListSetEntry<T>(index, item)),
            InsertCommand => ReadIndexAndItem(remaining, static (index, item) => new ListInsertEntry<T>(index, item)),
            RemoveCommand => ReadRemoveAt(remaining),
            ClearCommand => new ListClearEntry<T>(),
            SnapshotCommand => ReadSnapshot(remaining),
            _ => throw new NotSupportedException($"Command type {command} is not supported"),
        };
    }

    private DurableListEntry<T> ReadAdd(ReadOnlySequence<byte> remaining)
    {
        var item = codec.Read(remaining, out _);
        return new ListAddEntry<T>(item);
    }

    private DurableListEntry<T> ReadIndexAndItem(ReadOnlySequence<byte> remaining, Func<int, T, DurableListEntry<T>> factory)
    {
        var reader = new SequenceReader<byte>(remaining);
        var index = (int)VarIntHelper.ReadVarUInt32(ref reader);
        remaining = remaining.Slice(reader.Consumed);
        var item = codec.Read(remaining, out _);
        return factory(index, item);
    }

    private static DurableListEntry<T> ReadRemoveAt(ReadOnlySequence<byte> remaining)
    {
        var reader = new SequenceReader<byte>(remaining);
        var index = (int)VarIntHelper.ReadVarUInt32(ref reader);
        return new ListRemoveAtEntry<T>(index);
    }

    private DurableListEntry<T> ReadSnapshot(ReadOnlySequence<byte> remaining)
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

        return new ListSnapshotEntry<T>(items);
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
