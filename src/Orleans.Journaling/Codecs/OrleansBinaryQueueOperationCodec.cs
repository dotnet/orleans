using Orleans.Serialization.Buffers;
using Orleans.Serialization.Session;

namespace Orleans.Journaling;

/// <summary>
/// Binary codec for durable queue journal entries, preserving the legacy Orleans binary wire format.
/// </summary>
internal sealed class OrleansBinaryQueueOperationCodec<T>(
    IJournalValueCodec<T> codec,
    SerializerSessionPool sessionPool) : IQueueOperationCodec<T>
{
    private const byte FormatVersion = 0;
    private const uint EnqueueCommand = 0;
    private const uint DequeueCommand = 1;
    private const uint ClearCommand = 2;
    private const uint SnapshotCommand = 3;

    /// <inheritdoc/>
    public void WriteEnqueue(T item, JournalStreamWriter writer)
    {
        using var entry = writer.BeginEntry();
        var output = entry.PayloadWriter;
        var payloadWriter = Writer.Create(output, session: null!);
        payloadWriter.WriteByte(FormatVersion);
        payloadWriter.WriteVarUInt32(EnqueueCommand);
        payloadWriter.Commit();
        codec.Write(item, output);
        entry.Commit();
    }

    /// <inheritdoc/>
    public void WriteDequeue(JournalStreamWriter writer)
    {
        using var entry = writer.BeginEntry();
        var payloadWriter = Writer.Create(entry.PayloadWriter, session: null!);
        payloadWriter.WriteByte(FormatVersion);
        payloadWriter.WriteVarUInt32(DequeueCommand);
        payloadWriter.Commit();
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
    public void WriteSnapshot(IReadOnlyCollection<T> items, JournalStreamWriter writer)
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
        foreach (var item in items)
        {
            CollectionCodecHelpers.ThrowIfSnapshotItemCountExceeded(count, written);
            codec.Write(item, output);
            written++;
        }

        CollectionCodecHelpers.RequireSnapshotItemCount(count, written);
        entry.Commit();
    }

    /// <inheritdoc/>
    public void Apply(JournalReadBuffer input, IQueueOperationHandler<T> consumer)
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

    private void Apply<TInput>(ref Reader<TInput> reader, IQueueOperationHandler<T> consumer)
    {
        OrleansBinaryOperationApplier.ReadVersion(ref reader);
        var command = reader.ReadVarUInt32();
        switch (command)
        {
            case EnqueueCommand:
                consumer.ApplyEnqueue(codec.Read(ref reader));
                break;
            case DequeueCommand:
                consumer.ApplyDequeue();
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

    private void ApplySnapshot<TInput>(ref Reader<TInput> reader, IQueueOperationHandler<T> consumer)
    {
        var count = OrleansBinaryCollectionWireHelpers.ReadSnapshotCount(ref reader);

        consumer.Reset(count);
        for (var i = 0; i < count; i++)
        {
            consumer.ApplyEnqueue(codec.Read(ref reader));
        }
    }

}
