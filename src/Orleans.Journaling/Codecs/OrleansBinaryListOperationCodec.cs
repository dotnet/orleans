using System.Buffers;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Buffers.Adaptors;
using Orleans.Serialization.Session;

namespace Orleans.Journaling;

/// <summary>
/// Binary codec for durable list journal entries, preserving the legacy Orleans binary wire format.
/// </summary>
internal sealed class OrleansBinaryListOperationCodec<T>(
    IJournalValueCodec<T> codec,
    SerializerSessionPool sessionPool) : IDurableListOperationCodec<T>, IOrleansBinaryJournalEntryCodec
{
    private const byte FormatVersion = 0;
    private const uint AddCommand = 0;
    private const uint SetCommand = 1;
    private const uint InsertCommand = 2;
    private const uint RemoveCommand = 3;
    private const uint ClearCommand = 4;
    private const uint SnapshotCommand = 5;

    /// <inheritdoc/>
    public void WriteAdd(T item, JournalStreamWriter writer) =>
        JournalOperationWriter.Write(writer, output => WriteAddPayload(item, output));

    private void WriteAddPayload(T item, IBufferWriter<byte> output)
    {
        WriteHeader(output, AddCommand);
        codec.Write(item, output);
    }

    /// <inheritdoc/>
    public void WriteSet(int index, T item, JournalStreamWriter writer) =>
        JournalOperationWriter.Write(writer, output => WriteSetPayload(index, item, output));

    private void WriteSetPayload(int index, T item, IBufferWriter<byte> output)
    {
        WriteHeader(output, SetCommand, (uint)index);
        codec.Write(item, output);
    }

    /// <inheritdoc/>
    public void WriteInsert(int index, T item, JournalStreamWriter writer) =>
        JournalOperationWriter.Write(writer, output => WriteInsertPayload(index, item, output));

    private void WriteInsertPayload(int index, T item, IBufferWriter<byte> output)
    {
        WriteHeader(output, InsertCommand, (uint)index);
        codec.Write(item, output);
    }

    /// <inheritdoc/>
    public void WriteRemoveAt(int index, JournalStreamWriter writer) =>
        JournalOperationWriter.Write(writer, output => WriteRemoveAtPayload(index, output));

    private static void WriteRemoveAtPayload(int index, IBufferWriter<byte> output) =>
        WriteHeader(output, RemoveCommand, (uint)index);

    /// <inheritdoc/>
    public void WriteClear(JournalStreamWriter writer) =>
        JournalOperationWriter.Write(writer, WriteClearPayload);

    private static void WriteClearPayload(IBufferWriter<byte> output) =>
        WriteHeader(output, ClearCommand);

    /// <inheritdoc/>
    public void WriteSnapshot(IReadOnlyCollection<T> items, JournalStreamWriter writer) =>
        JournalOperationWriter.Write(writer, output => WriteSnapshotPayload(items, output));

    private void WriteSnapshotPayload(IReadOnlyCollection<T> items, IBufferWriter<byte> output)
    {
        var count = CollectionCodecHelpers.GetSnapshotCount(items);
        WriteHeader(output, SnapshotCommand, (uint)count);
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
    public void Apply(ReadOnlySequence<byte> input, IDurableListOperationHandler<T> consumer)
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

    void IOrleansBinaryJournalEntryCodec.Apply(ref Reader<ArcBufferReaderInput> reader, IJournaledState state) =>
        Apply(ref reader, DurableOperationHandler.GetRequiredHandler<IDurableListOperationHandler<T>>(state, this));

    private void Apply(ref Reader<ArcBufferReaderInput> reader, IDurableListOperationHandler<T> consumer)
    {
        OrleansBinaryOperationApplier.ReadVersion(ref reader);
        var command = reader.ReadVarUInt32();
        switch (command)
        {
            case AddCommand:
                consumer.ApplyAdd(codec.Read(ref reader));
                break;
            case SetCommand:
            {
                var index = OrleansBinaryCollectionWireHelpers.ReadListIndex(ref reader);
                var item = codec.Read(ref reader);
                consumer.ApplySet(index, item);
                break;
            }
            case InsertCommand:
            {
                var index = OrleansBinaryCollectionWireHelpers.ReadListIndex(ref reader);
                var item = codec.Read(ref reader);
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

    private void ApplySnapshot(ref Reader<ArcBufferReaderInput> reader, IDurableListOperationHandler<T> consumer)
    {
        var count = OrleansBinaryCollectionWireHelpers.ReadSnapshotCount(ref reader);

        consumer.Reset(count);
        for (var i = 0; i < count; i++)
        {
            consumer.ApplyAdd(codec.Read(ref reader));
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
