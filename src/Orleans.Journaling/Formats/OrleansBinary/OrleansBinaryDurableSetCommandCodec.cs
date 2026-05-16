using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Session;

namespace Orleans.Journaling;

/// <summary>
/// Binary codec for durable set journal entries, preserving the legacy Orleans binary wire format.
/// </summary>
internal sealed class OrleansBinaryDurableSetCommandCodec<T>(
    IFieldCodec<T> codec,
    SerializerSessionPool sessionPool) : IDurableSetCommandCodec<T>
{
    private const uint AddCommand = 0;
    private const uint RemoveCommand = 1;
    private const uint ClearCommand = 2;
    private const uint SnapshotCommand = 3;

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
    public void WriteRemove(T item, JournalStreamWriter writer)
    {
        using var entry = writer.BeginEntry();
        var output = entry.Writer;
        var payloadWriter = Writer.Create(output, session: null!);
        payloadWriter.WriteVarUInt32(RemoveCommand);
        payloadWriter.Commit();
        OrleansBinaryCommandCodecHelpers.WriteValue(codec, item, output, sessionPool);
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
    public void Apply(JournalBufferReader input, IDurableSetCommandHandler<T> consumer)
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

    private void Apply<TInput>(ref Reader<TInput> reader, IDurableSetCommandHandler<T> consumer)
    {
        var command = reader.ReadVarUInt32();
        switch (command)
        {
            case AddCommand:
                consumer.ApplyAdd(OrleansBinaryCommandCodecHelpers.ReadValue(codec, ref reader));
                break;
            case RemoveCommand:
                consumer.ApplyRemove(OrleansBinaryCommandCodecHelpers.ReadValue(codec, ref reader));
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

    private void ApplySnapshot<TInput>(ref Reader<TInput> reader, IDurableSetCommandHandler<T> consumer)
    {
        var count = OrleansBinaryCollectionWireHelpers.ReadSnapshotCount(ref reader);

        consumer.Reset(count);
        for (var i = 0; i < count; i++)
        {
            consumer.ApplyAdd(OrleansBinaryCommandCodecHelpers.ReadValue(codec, ref reader));
        }
    }
}
