using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Session;

namespace Orleans.Journaling;

/// <summary>
/// Binary codec for durable dictionary journal entries, preserving the legacy Orleans binary wire format.
/// </summary>
internal sealed class OrleansBinaryDurableDictionaryCommandCodec<TKey, TValue>(
    IFieldCodec<TKey> keyCodec,
    IFieldCodec<TValue> valueCodec,
    SerializerSessionPool sessionPool) : IDurableDictionaryCommandCodec<TKey, TValue> where TKey : notnull
{
    private const uint SetCommand = 0;
    private const uint RemoveCommand = 1;
    private const uint ClearCommand = 2;
    private const uint SnapshotCommand = 3;

    /// <inheritdoc/>
    public void WriteSet(TKey key, TValue value, JournalStreamWriter writer)
    {
        using var entry = writer.BeginEntry();
        var output = entry.Writer;
        using var session = sessionPool.GetSession();
        var payloadWriter = Writer.Create(output, session);
        payloadWriter.WriteVarUInt32(SetCommand);
        keyCodec.WriteField(ref payloadWriter, 0, typeof(TKey), key);
        valueCodec.WriteField(ref payloadWriter, 1, typeof(TValue), value);
        payloadWriter.Commit();
        entry.Commit();
    }

    /// <inheritdoc/>
    public void WriteRemove(TKey key, JournalStreamWriter writer)
    {
        using var entry = writer.BeginEntry();
        var output = entry.Writer;
        var payloadWriter = Writer.Create(output, session: null!);
        payloadWriter.WriteVarUInt32(RemoveCommand);
        payloadWriter.Commit();
        OrleansBinaryCommandCodecHelpers.WriteValue(keyCodec, key, output, sessionPool);
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
    public void WriteSnapshot(IReadOnlyCollection<KeyValuePair<TKey, TValue>> items, JournalStreamWriter writer)
    {
        using var entry = writer.BeginEntry();
        var output = entry.Writer;
        var count = CollectionCodecHelpers.GetSnapshotCount(items);
        var payloadWriter = Writer.Create(output, session: null!);
        payloadWriter.WriteVarUInt32(SnapshotCommand);
        payloadWriter.WriteVarUInt32((uint)count);
        payloadWriter.Commit();
        var written = 0;
        foreach (var (key, value) in items)
        {
            CollectionCodecHelpers.ThrowIfSnapshotItemCountExceeded(count, written);
            OrleansBinaryCommandCodecHelpers.WriteValue(keyCodec, key, output, sessionPool);
            OrleansBinaryCommandCodecHelpers.WriteValue(valueCodec, value, output, sessionPool);
            written++;
        }

        CollectionCodecHelpers.RequireSnapshotItemCount(count, written);
        entry.Commit();
    }

    /// <inheritdoc/>
    public void Apply(JournalBufferReader input, IDurableDictionaryCommandHandler<TKey, TValue> consumer)
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

    private void Apply<TInput>(ref Reader<TInput> reader, IDurableDictionaryCommandHandler<TKey, TValue> consumer)
    {
        var command = reader.ReadVarUInt32();
        switch (command)
        {
            case SetCommand:
            {
                var key = OrleansBinaryCommandCodecHelpers.ReadValue(keyCodec, ref reader);
                var value = OrleansBinaryCommandCodecHelpers.ReadValue(valueCodec, ref reader);
                consumer.ApplySet(key, value);
                break;
            }
            case RemoveCommand:
                consumer.ApplyRemove(OrleansBinaryCommandCodecHelpers.ReadValue(keyCodec, ref reader));
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

    private void ApplySnapshot<TInput>(ref Reader<TInput> reader, IDurableDictionaryCommandHandler<TKey, TValue> consumer)
    {
        var count = OrleansBinaryCollectionWireHelpers.ReadSnapshotCount(ref reader);

        consumer.Reset(count);
        for (var i = 0; i < count; i++)
        {
            var key = OrleansBinaryCommandCodecHelpers.ReadValue(keyCodec, ref reader);
            var value = OrleansBinaryCommandCodecHelpers.ReadValue(valueCodec, ref reader);
            consumer.ApplySet(key, value);
        }
    }

}
