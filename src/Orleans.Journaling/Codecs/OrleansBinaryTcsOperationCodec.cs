using System.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Binary codec for durable task completion source log entries, preserving the legacy Orleans binary wire format.
/// </summary>
/// <remarks>
/// Unlike other durable type codecs, the TCS format uses a status byte instead of a
/// VarUInt32 command discriminator after the version byte.
/// </remarks>
internal sealed class OrleansBinaryTcsOperationCodec<T>(
    ILogValueCodec<T> codec,
    ILogValueCodec<Exception> exceptionCodec) : IDurableTaskCompletionSourceOperationCodec<T>
{
    private const byte FormatVersion = 0;

    /// <inheritdoc/>
    public void WritePending(LogStreamWriter writer) =>
        DurableOperationCodecWriter.Write(writer, WritePendingPayload);

    private static void WritePendingPayload(IBufferWriter<byte> output)
    {
        WriteVersionByte(output);
        WriteByte(output, (byte)DurableTaskCompletionSourceStatus.Pending);
    }

    /// <inheritdoc/>
    public void WriteCompleted(T value, LogStreamWriter writer) =>
        DurableOperationCodecWriter.Write(writer, output => WriteCompletedPayload(value, output));

    private void WriteCompletedPayload(T value, IBufferWriter<byte> output)
    {
        WriteVersionByte(output);
        WriteByte(output, (byte)DurableTaskCompletionSourceStatus.Completed);
        codec.Write(value, output);
    }

    /// <inheritdoc/>
    public void WriteFaulted(Exception exception, LogStreamWriter writer) =>
        DurableOperationCodecWriter.Write(writer, output => WriteFaultedPayload(exception, output));

    private void WriteFaultedPayload(Exception exception, IBufferWriter<byte> output)
    {
        WriteVersionByte(output);
        WriteByte(output, (byte)DurableTaskCompletionSourceStatus.Faulted);
        exceptionCodec.Write(exception, output);
    }

    /// <inheritdoc/>
    public void WriteCanceled(LogStreamWriter writer) =>
        DurableOperationCodecWriter.Write(writer, WriteCanceledPayload);

    private static void WriteCanceledPayload(IBufferWriter<byte> output)
    {
        WriteVersionByte(output);
        WriteByte(output, (byte)DurableTaskCompletionSourceStatus.Canceled);
    }

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, IDurableTaskCompletionSourceOperationHandler<T> consumer)
    {
        var reader = new SequenceReader<byte>(input);
        ReadVersionByte(ref reader);

        if (!reader.TryRead(out var statusByte))
        {
            throw new InvalidOperationException("Insufficient data while reading status byte.");
        }

        var remaining = input.Slice(reader.Consumed);
        var status = (DurableTaskCompletionSourceStatus)statusByte;

        switch (status)
        {
            case DurableTaskCompletionSourceStatus.Pending:
                consumer.ApplyPending();
                break;
            case DurableTaskCompletionSourceStatus.Completed:
                consumer.ApplyCompleted(codec.Read(remaining, out _));
                break;
            case DurableTaskCompletionSourceStatus.Faulted:
                consumer.ApplyFaulted(exceptionCodec.Read(remaining, out _));
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

    private static void ReadVersionByte(ref SequenceReader<byte> reader)
    {
        if (!reader.TryRead(out var version) || version != FormatVersion)
        {
            throw new NotSupportedException($"Unsupported format version: {version}");
        }
    }
}
