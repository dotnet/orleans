using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Session;

namespace Orleans.Journaling;

/// <summary>
/// Binary codec for durable list journal entries, preserving the legacy Orleans binary wire format.
/// </summary>
internal sealed class OrleansBinaryDurableListCommandCodec<T>(
    IFieldCodec<T> codec,
    SerializerSessionPool sessionPool) : IDurableListCommandCodec<T>
{
    private const uint AddCommand = 0;
    private const uint SetCommand = 1;
    private const uint InsertCommand = 2;
    private const uint RemoveCommand = 3;
    private const uint ClearCommand = 4;
    private const uint SnapshotCommand = 5;

    /// <inheritdoc/>
    public void WriteAdd(T item, JournalStreamWriter writer)
    {
        using var entry = writer.BeginEntry();
        var output = entry.Writer;
        var payloadWriter = Writer.Create(output, session: null!);
        payloadWriter.WriteVarUInt32(AddCommand);
        payloadWriter.Commit();
        OrleansBinaryCommandCodecHelpers.WriteValue(codec, item, output, sessionPool);
        entry.Commit();
    }

    /// <inheritdoc/>
    public void WriteSet(int index, T item, JournalStreamWriter writer)
    {
        using var entry = writer.BeginEntry();
        var output = entry.Writer;
        var payloadWriter = Writer.Create(output, session: null!);
        payloadWriter.WriteVarUInt32(SetCommand);
        payloadWriter.WriteVarUInt32((uint)index);
        payloadWriter.Commit();
        OrleansBinaryCommandCodecHelpers.WriteValue(codec, item, output, sessionPool);
        entry.Commit();
    }

    /// <inheritdoc/>
    public void WriteInsert(int index, T item, JournalStreamWriter writer)
    {
        using var entry = writer.BeginEntry();
        var output = entry.Writer;
        var payloadWriter = Writer.Create(output, session: null!);
        payloadWriter.WriteVarUInt32(InsertCommand);
        payloadWriter.WriteVarUInt32((uint)index);
        payloadWriter.Commit();
        OrleansBinaryCommandCodecHelpers.WriteValue(codec, item, output, sessionPool);
        entry.Commit();
    }

    /// <inheritdoc/>
    public void WriteRemoveAt(int index, JournalStreamWriter writer)
    {
        using var entry = writer.BeginEntry();
        var payloadWriter = Writer.Create(entry.Writer, session: null!);
        payloadWriter.WriteVarUInt32(RemoveCommand);
        payloadWriter.WriteVarUInt32((uint)index);
        payloadWriter.Commit();
        entry.Commit();
    }

    /// <inheritdoc/>
    public void WriteClear(JournalStreamWriter writer)
    {
        using var entry = writer.BeginEntry();
        var payloadWriter = Writer.Create(entry.Writer, session: null!);
        payloadWriter.WriteVarUInt32(ClearCommand);
        payloadWriter.Commit();
        entry.Commit();
    }

    /// <inheritdoc/>
    public void WriteSnapshot(IReadOnlyCollection<T> items, JournalStreamWriter writer)
    {
        using var entry = writer.BeginEntry();
        var output = entry.Writer;
        var count = CollectionCodecHelpers.GetSnapshotCount(items);
        var payloadWriter = Writer.Create(output, session: null!);
        payloadWriter.WriteVarUInt32(SnapshotCommand);
        payloadWriter.WriteVarUInt32((uint)count);
        payloadWriter.Commit();
        var written = 0;
        foreach (var item in items)
        {
            CollectionCodecHelpers.ThrowIfSnapshotItemCountExceeded(count, written);
            OrleansBinaryCommandCodecHelpers.WriteValue(codec, item, output, sessionPool);
            written++;
        }

        CollectionCodecHelpers.RequireSnapshotItemCount(count, written);
        entry.Commit();
    }

    /// <inheritdoc/>
    public void Apply(JournalBufferReader input, IDurableListCommandHandler<T> consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        using var slice = input.Peek(input.Length);
        using var session = sessionPool.GetSession();
        var reader = Reader.Create(slice, session);
        Apply(ref reader, consumer);
        if (reader.Position != reader.Length)
        {
            throw new InvalidOperationException("Unexpected trailing data after binary journal command.");
        }
    }

    private void Apply<TInput>(ref Reader<TInput> reader, IDurableListCommandHandler<T> consumer)
    {
        var command = reader.ReadVarUInt32();
        switch (command)
        {
            case AddCommand:
                consumer.ApplyAdd(OrleansBinaryCommandCodecHelpers.ReadValue(codec, ref reader));
                break;
            case SetCommand:
            {
                var index = OrleansBinaryCollectionWireHelpers.ReadListIndex(ref reader);
                var item = OrleansBinaryCommandCodecHelpers.ReadValue(codec, ref reader);
                consumer.ApplySet(index, item);
                break;
            }
            case InsertCommand:
            {
                var index = OrleansBinaryCollectionWireHelpers.ReadListIndex(ref reader);
                var item = OrleansBinaryCommandCodecHelpers.ReadValue(codec, ref reader);
                consumer.ApplyInsert(index, item);
                break;
            }
            case RemoveCommand:
                consumer.ApplyRemoveAt(OrleansBinaryCollectionWireHelpers.ReadListIndex(ref reader));
                break;
            case ClearCommand:
                consumer.ApplyClear();
                break;
            case SnapshotCommand:
                ApplySnapshot(ref reader, consumer);
                break;
            default:
                throw new NotSupportedException($"Command type {command} is not supported");
        }
    }

    private void ApplySnapshot<TInput>(ref Reader<TInput> reader, IDurableListCommandHandler<T> consumer)
    {
        var count = OrleansBinaryCollectionWireHelpers.ReadSnapshotCount(ref reader);

        consumer.Reset(count);
        for (var i = 0; i < count; i++)
        {
            consumer.ApplyAdd(OrleansBinaryCommandCodecHelpers.ReadValue(codec, ref reader));
        }
    }

}
