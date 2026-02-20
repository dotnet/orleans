using System.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Binary codec for <see cref="DurableTaskCompletionSourceEntry{T}"/> log entries,
/// preserving the legacy Orleans binary wire format.
/// </summary>
/// <remarks>
/// Unlike other durable type codecs, the TCS format uses a status byte instead of a
/// VarUInt32 command discriminator after the version byte.
/// </remarks>
internal sealed class OrleansBinaryTcsEntryCodec<T>(
    ILogDataCodec<T> codec,
    ILogDataCodec<Exception> exceptionCodec) : ILogEntryCodec<DurableTaskCompletionSourceEntry<T>>
{
    private const byte FormatVersion = 0;

    /// <inheritdoc/>
    public void Write(DurableTaskCompletionSourceEntry<T> entry, IBufferWriter<byte> output)
    {
        WriteVersionByte(output);

        switch (entry)
        {
            case TcsPendingEntry<T>:
                WriteByte(output, (byte)DurableTaskCompletionSourceStatus.Pending);
                break;
            case TcsCompletedEntry<T>(var value):
                WriteByte(output, (byte)DurableTaskCompletionSourceStatus.Completed);
                codec.Write(value, output);
                break;
            case TcsFaultedEntry<T>(var exception):
                WriteByte(output, (byte)DurableTaskCompletionSourceStatus.Faulted);
                exceptionCodec.Write(exception, output);
                break;
            case TcsCanceledEntry<T>:
                WriteByte(output, (byte)DurableTaskCompletionSourceStatus.Canceled);
                break;
            default:
                throw new NotSupportedException($"Unsupported entry type: {entry.GetType()}");
        }
    }

    /// <inheritdoc/>
    public DurableTaskCompletionSourceEntry<T> Read(ReadOnlySequence<byte> input)
    {
        var reader = new SequenceReader<byte>(input);
        ReadVersionByte(ref reader);

        if (!reader.TryRead(out var statusByte))
        {
            throw new InvalidOperationException("Insufficient data while reading status byte.");
        }

        var remaining = input.Slice(reader.Consumed);
        var status = (DurableTaskCompletionSourceStatus)statusByte;

        return status switch
        {
            DurableTaskCompletionSourceStatus.Pending => new TcsPendingEntry<T>(),
            DurableTaskCompletionSourceStatus.Completed => new TcsCompletedEntry<T>(codec.Read(remaining, out _)),
            DurableTaskCompletionSourceStatus.Faulted => new TcsFaultedEntry<T>(exceptionCodec.Read(remaining, out _)),
            DurableTaskCompletionSourceStatus.Canceled => new TcsCanceledEntry<T>(),
            _ => throw new NotSupportedException($"Unsupported status: {status}"),
        };
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
