using System.Buffers;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Buffers.Adaptors;
using Orleans.Serialization.Session;

namespace Orleans.Journaling;

/// <summary>
/// Binary codec for durable task completion source journal entries, preserving the legacy Orleans binary wire format.
/// </summary>
/// <remarks>
/// Unlike other durable type codecs, the TCS format uses a status byte instead of a
/// VarUInt32 command discriminator after the version byte.
/// </remarks>
internal sealed class OrleansBinaryTcsOperationCodec<T>(
    IJournalValueCodec<T> codec,
    IJournalValueCodec<Exception> exceptionCodec,
    SerializerSessionPool sessionPool) : IDurableTaskCompletionSourceOperationCodec<T>, IOrleansBinaryJournalEntryCodec
{
    private const byte FormatVersion = 0;

    /// <inheritdoc/>
    public void WritePending(JournalStreamWriter writer) =>
        JournalOperationWriter.Write(writer, WritePendingPayload);

    private static void WritePendingPayload(IBufferWriter<byte> output)
    {
        WriteVersionByte(output);
        WriteByte(output, (byte)DurableTaskCompletionSourceStatus.Pending);
    }

    /// <inheritdoc/>
    public void WriteCompleted(T value, JournalStreamWriter writer) =>
        JournalOperationWriter.Write(writer, output => WriteCompletedPayload(value, output));

    private void WriteCompletedPayload(T value, IBufferWriter<byte> output)
    {
        WriteVersionByte(output);
        WriteByte(output, (byte)DurableTaskCompletionSourceStatus.Completed);
        codec.Write(value, output);
    }

    /// <inheritdoc/>
    public void WriteFaulted(Exception exception, JournalStreamWriter writer) =>
        JournalOperationWriter.Write(writer, output => WriteFaultedPayload(exception, output));

    private void WriteFaultedPayload(Exception exception, IBufferWriter<byte> output)
    {
        WriteVersionByte(output);
        WriteByte(output, (byte)DurableTaskCompletionSourceStatus.Faulted);
        exceptionCodec.Write(exception, output);
    }

    /// <inheritdoc/>
    public void WriteCanceled(JournalStreamWriter writer) =>
        JournalOperationWriter.Write(writer, WriteCanceledPayload);

    private static void WriteCanceledPayload(IBufferWriter<byte> output)
    {
        WriteVersionByte(output);
        WriteByte(output, (byte)DurableTaskCompletionSourceStatus.Canceled);
    }

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, IDurableTaskCompletionSourceOperationHandler<T> consumer)
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
        Apply(ref reader, DurableOperationHandler.GetRequiredHandler<IDurableTaskCompletionSourceOperationHandler<T>>(state, this));

    private void Apply(ref Reader<ArcBufferReaderInput> reader, IDurableTaskCompletionSourceOperationHandler<T> consumer)
    {
        OrleansBinaryOperationApplier.ReadVersion(ref reader);
        if (reader.Position >= reader.Length)
        {
            throw new InvalidOperationException("Missing TCS status byte.");
        }

        var status = (DurableTaskCompletionSourceStatus)reader.ReadByte();
        switch (status)
        {
            case DurableTaskCompletionSourceStatus.Pending:
                consumer.ApplyPending();
                break;
            case DurableTaskCompletionSourceStatus.Completed:
                consumer.ApplyCompleted(codec.Read(ref reader));
                break;
            case DurableTaskCompletionSourceStatus.Faulted:
                consumer.ApplyFaulted(exceptionCodec.Read(ref reader));
                break;
            case DurableTaskCompletionSourceStatus.Canceled:
                consumer.ApplyCanceled();
                break;
            default:
                throw new NotSupportedException($"Unsupported status: {status}");
        }
    }

    private static void WriteVersionByte(IBufferWriter<byte> output)
    {
        var span = output.GetSpan(1);
        span[0] = FormatVersion;
        output.Advance(1);
    }

    private static void WriteByte(IBufferWriter<byte> output, byte value)
    {
        var span = output.GetSpan(1);
        span[0] = value;
        output.Advance(1);
    }
}
