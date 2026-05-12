using System.Buffers;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Buffers.Adaptors;
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
    public void WriteSet(TKey key, TValue value, JournalStreamWriter writer) =>
        JournalOperationWriter.Write(writer, output => WriteSetPayload(key, value, output));

    private void WriteSetPayload(TKey key, TValue value, IBufferWriter<byte> output)
    {
        var binaryKeyCodec = AsBinaryValueCodec(keyCodec, nameof(keyCodec));
        var binaryValueCodec = AsBinaryValueCodec(valueCodec, nameof(valueCodec));
        using var session = sessionPool.GetSession();
        var writer = Writer.Create(output, session);
        writer.WriteByte(FormatVersion);
        writer.WriteVarUInt32(SetCommand);
        binaryKeyCodec.FieldCodec.WriteField(ref writer, 0, typeof(TKey), key);
        binaryValueCodec.FieldCodec.WriteField(ref writer, 1, typeof(TValue), value);
        writer.Commit();
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
    public void WriteRemove(TKey key, JournalStreamWriter writer) =>
        JournalOperationWriter.Write(writer, output => WriteRemovePayload(key, output));

    private void WriteRemovePayload(TKey key, IBufferWriter<byte> output)
    {
        WriteHeader(output, RemoveCommand);
        keyCodec.Write(key, output);
    }

    /// <inheritdoc/>
    public void WriteClear(JournalStreamWriter writer) =>
        JournalOperationWriter.Write(writer, WriteClearPayload);

    private static void WriteClearPayload(IBufferWriter<byte> output) =>
        WriteHeader(output, ClearCommand);

    /// <inheritdoc/>
    public void WriteSnapshot(IReadOnlyCollection<KeyValuePair<TKey, TValue>> items, JournalStreamWriter writer) =>
        JournalOperationWriter.Write(writer, output => WriteSnapshotPayload(items, output));

    private void WriteSnapshotPayload(IReadOnlyCollection<KeyValuePair<TKey, TValue>> items, IBufferWriter<byte> output)
    {
        var count = CollectionCodecHelpers.GetSnapshotCount(items);
        WriteHeader(output, SnapshotCommand, (uint)count);
        var written = 0;
        foreach (var (key, value) in items)
        {
            CollectionCodecHelpers.ThrowIfSnapshotItemCountExceeded(count, written);
            keyCodec.Write(key, output);
            valueCodec.Write(value, output);
            written++;
        }

        CollectionCodecHelpers.RequireSnapshotItemCount(count, written);
    }

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, IDictionaryOperationHandler<TKey, TValue> consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        using var arcBuffer = OrleansBinaryOperationApplier.Materialize(input);
        using var session = sessionPool.GetSession();
        var reader = Reader.Create(arcBuffer, session);
        Apply(ref reader, consumer);
        if (reader.Position != reader.Length)
        {
            throw new InvalidOperationException("Unexpected trailing data after binary journal operation.");
        }
    }

    private void Apply(ref Reader<ArcBufferReaderInput> reader, IDictionaryOperationHandler<TKey, TValue> consumer)
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

    private void ApplySnapshot(ref Reader<ArcBufferReaderInput> reader, IDictionaryOperationHandler<TKey, TValue> consumer)
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

    private static void WriteHeader(IBufferWriter<byte> output, uint command)
    {
        var writer = Writer.Create(output, session: null!);
        writer.WriteByte(FormatVersion);
        writer.WriteVarUInt32(command);
        writer.Commit();
    }

    private static void WriteHeader(IBufferWriter<byte> output, uint command, uint operand)
    {
        var writer = Writer.Create(output, session: null!);
        writer.WriteByte(FormatVersion);
        writer.WriteVarUInt32(command);
        writer.WriteVarUInt32(operand);
        writer.Commit();
    }
}
