using System.Buffers;
using Orleans.Serialization.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Binary codec for durable queue journal entries, preserving the legacy Orleans binary wire format.
/// </summary>
internal sealed class OrleansBinaryQueueOperationCodec<T>(
    IJournalValueCodec<T> codec) : IDurableQueueOperationCodec<T>, IOrleansBinaryJournalEntryCodec
{
    private const byte FormatVersion = 0;
    private const uint EnqueueCommand = 0;
    private const uint DequeueCommand = 1;
    private const uint ClearCommand = 2;
    private const uint SnapshotCommand = 3;

    /// <inheritdoc/>
    public void WriteEnqueue(T item, JournalStreamWriter writer) =>
        JournalOperationWriter.Write(writer, output => WriteEnqueuePayload(item, output));

    private void WriteEnqueuePayload(T item, IBufferWriter<byte> output)
    {
        WriteHeader(output, EnqueueCommand);
        codec.Write(item, output);
    }

    /// <inheritdoc/>
    public void WriteDequeue(JournalStreamWriter writer) =>
        JournalOperationWriter.Write(writer, WriteDequeuePayload);

    private static void WriteDequeuePayload(IBufferWriter<byte> output) =>
        WriteHeader(output, DequeueCommand);

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
    public void Apply(ReadOnlySequence<byte> input, IDurableQueueOperationHandler<T> consumer)
    {
        var reader = new OrleansBinaryOperationReader(input);
        var command = reader.ReadCommand();

        switch (command)
        {
            case EnqueueCommand:
                var item = reader.ReadValue("item", codec);
                reader.EnsureEnd();
                consumer.ApplyEnqueue(item);
                break;
            case DequeueCommand:
                reader.EnsureEnd();
                consumer.ApplyDequeue();
                break;
            case ClearCommand:
                reader.EnsureEnd();
                consumer.ApplyClear();
                break;
            case SnapshotCommand:
                ApplySnapshot(ref reader, consumer);
                break;
            default:
                throw new NotSupportedException($"Command type {command} is not supported");
        }
    }

    void IOrleansBinaryJournalEntryCodec.Apply(ReadOnlySequence<byte> input, IJournaledState state) =>
        Apply(input, DurableOperationHandler.GetRequiredHandler<IDurableQueueOperationHandler<T>>(state, this));

    private void ApplySnapshot(ref OrleansBinaryOperationReader reader, IDurableQueueOperationHandler<T> consumer)
    {
        var count = reader.ReadSnapshotCount();

        consumer.Reset(count);
        for (var i = 0; i < count; i++)
        {
            var item = reader.ReadValue("item", codec);
            consumer.ApplyEnqueue(item);
        }

        reader.EnsureEnd();
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
