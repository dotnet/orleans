using Orleans.Serialization.Buffers;
using Orleans.Serialization.Session;

namespace Orleans.Journaling;

/// <summary>
/// Binary codec for durable dictionary journal entries, preserving the legacy Orleans binary wire format.
/// </summary>
internal sealed class OrleansBinaryDictionaryOperationCodec<TKey, TValue>(
    IJournalValueCodec<TKey> keyCodec,
    IJournalValueCodec<TValue> valueCodec,
    SerializerSessionPool sessionPool) : IDictionaryOperationCodec<TKey, TValue> where TKey : notnull
{
    private const byte FormatVersion = 0;
    private const uint SetCommand = 0;
    private const uint RemoveCommand = 1;
    private const uint ClearCommand = 2;
    private const uint SnapshotCommand = 3;

    /// <inheritdoc/>
    public void WriteSet(TKey key, TValue value, JournalStreamWriter writer)
    {
        using var entry = writer.BeginEntry();
        var output = entry.PayloadWriter;
        var binaryKeyCodec = AsBinaryValueCodec(keyCodec, nameof(keyCodec));
        var binaryValueCodec = AsBinaryValueCodec(valueCodec, nameof(valueCodec));
        using var session = sessionPool.GetSession();
        var payloadWriter = Writer.Create(output, session);
        payloadWriter.WriteByte(FormatVersion);
        payloadWriter.WriteVarUInt32(SetCommand);
        binaryKeyCodec.FieldCodec.WriteField(ref payloadWriter, 0, typeof(TKey), key);
        binaryValueCodec.FieldCodec.WriteField(ref payloadWriter, 1, typeof(TValue), value);
        payloadWriter.Commit();
        entry.Commit();
    }

    private static IOrleansBinaryValueCodec<T> AsBinaryValueCodec<T>(IJournalValueCodec<T> codec, string parameterName)
    {
        if (codec is IOrleansBinaryValueCodec<T> binary)
        {
            return binary;
        }

        throw new InvalidOperationException(
            $"{nameof(OrleansBinaryDictionaryOperationCodec<TKey, TValue>)}.{nameof(WriteSet)} requires '{parameterName}' to be an Orleans-binary value codec (an instance of {nameof(IOrleansBinaryValueCodec<T>)}, e.g. {nameof(OrleansJournalValueCodec<T>)}). The supplied codec '{codec.GetType().FullName}' does not satisfy this contract.");
    }

    /// <inheritdoc/>
    public void WriteRemove(TKey key, JournalStreamWriter writer)
    {
        using var entry = writer.BeginEntry();
        var output = entry.PayloadWriter;
        var payloadWriter = Writer.Create(output, session: null!);
        payloadWriter.WriteByte(FormatVersion);
        payloadWriter.WriteVarUInt32(RemoveCommand);
        payloadWriter.Commit();
        keyCodec.Write(key, output);
        entry.Commit();
    }

    /// <inheritdoc/>
    public void WriteClear(JournalStreamWriter writer)
    {
        using var entry = writer.BeginEntry();
        var payloadWriter = Writer.Create(entry.PayloadWriter, session: null!);
        payloadWriter.WriteByte(FormatVersion);
        payloadWriter.WriteVarUInt32(ClearCommand);
        payloadWriter.Commit();
        entry.Commit();
    }

    /// <inheritdoc/>
    public void WriteSnapshot(IReadOnlyCollection<KeyValuePair<TKey, TValue>> items, JournalStreamWriter writer)
    {
        using var entry = writer.BeginEntry();
        var output = entry.PayloadWriter;
        var count = CollectionCodecHelpers.GetSnapshotCount(items);
        var payloadWriter = Writer.Create(output, session: null!);
        payloadWriter.WriteByte(FormatVersion);
        payloadWriter.WriteVarUInt32(SnapshotCommand);
        payloadWriter.WriteVarUInt32((uint)count);
        payloadWriter.Commit();
        var written = 0;
        foreach (var (key, value) in items)
        {
            CollectionCodecHelpers.ThrowIfSnapshotItemCountExceeded(count, written);
            keyCodec.Write(key, output);
            valueCodec.Write(value, output);
            written++;
        }

        CollectionCodecHelpers.RequireSnapshotItemCount(count, written);
        entry.Commit();
    }

    /// <inheritdoc/>
    public void Apply(JournalReadBuffer input, IDictionaryOperationHandler<TKey, TValue> consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        using var slice = input.PeekSlice(input.Length);
        using var session = sessionPool.GetSession();
        var reader = OrleansBinaryOperationApplier.CreateReader(slice, session);
        Apply(ref reader, consumer);
        if (reader.Position != reader.Length)
        {
            throw new InvalidOperationException("Unexpected trailing data after binary journal operation.");
        }
    }

    private void Apply<TInput>(ref Reader<TInput> reader, IDictionaryOperationHandler<TKey, TValue> consumer)
    {
        OrleansBinaryOperationApplier.ReadVersion(ref reader);
        var command = reader.ReadVarUInt32();
        switch (command)
        {
            case SetCommand:
            {
                var key = keyCodec.Read(ref reader);
                var value = valueCodec.Read(ref reader);
                consumer.ApplySet(key, value);
                break;
            }
            case RemoveCommand:
                consumer.ApplyRemove(keyCodec.Read(ref reader));
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

    private void ApplySnapshot<TInput>(ref Reader<TInput> reader, IDictionaryOperationHandler<TKey, TValue> consumer)
    {
        var count = OrleansBinaryCollectionWireHelpers.ReadSnapshotCount(ref reader);

        consumer.Reset(count);
        for (var i = 0; i < count; i++)
        {
            var key = keyCodec.Read(ref reader);
            var value = valueCodec.Read(ref reader);
            consumer.ApplySet(key, value);
        }
    }

}
