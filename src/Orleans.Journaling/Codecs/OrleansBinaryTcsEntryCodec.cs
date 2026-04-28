using System.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Binary codec for durable task completion source log entries, preserving the legacy Orleans binary wire format.
/// </summary>
/// <remarks>
/// Unlike other durable type codecs, the TCS format uses a status byte instead of a
/// VarUInt32 command discriminator after the version byte.
/// </remarks>
internal sealed class OrleansBinaryTcsEntryCodec<T>(
    ILogDataCodec<T> codec,
    ILogDataCodec<Exception> exceptionCodec) : IDurableTaskCompletionSourceCodec<T>
{
    private const byte FormatVersion = 0;

    /// <inheritdoc/>
    public void WritePending(IBufferWriter<byte> output)
    {
        WriteVersionByte(output);
        WriteByte(output, (byte)DurableTaskCompletionSourceStatus.Pending);
    }

    /// <inheritdoc/>
    public void WriteCompleted(T value, IBufferWriter<byte> output)
    {
        WriteVersionByte(output);
        WriteByte(output, (byte)DurableTaskCompletionSourceStatus.Completed);
        codec.Write(value, output);
    }

    /// <inheritdoc/>
    public void WriteFaulted(Exception exception, IBufferWriter<byte> output)
    {
        WriteVersionByte(output);
        WriteByte(output, (byte)DurableTaskCompletionSourceStatus.Faulted);
        exceptionCodec.Write(exception, output);
    }

    /// <inheritdoc/>
    public void WriteCanceled(IBufferWriter<byte> output)
    {
        WriteVersionByte(output);
        WriteByte(output, (byte)DurableTaskCompletionSourceStatus.Canceled);
    }

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, IDurableTaskCompletionSourceLogEntryConsumer<T> consumer)
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
