using Orleans.Serialization.Buffers;
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
    SerializerSessionPool sessionPool) : ITaskCompletionSourceOperationCodec<T>
{
    private const byte FormatVersion = 0;

    /// <inheritdoc/>
    public void WritePending(JournalStreamWriter writer)
    {
        using var entry = writer.BeginEntry();
        var output = entry.PayloadWriter;
        var span = output.GetSpan(2);
        span[0] = FormatVersion;
        span[1] = (byte)DurableTaskCompletionSourceStatus.Pending;
        output.Advance(2);
        entry.Commit();
    }

    /// <inheritdoc/>
    public void WriteCompleted(T value, JournalStreamWriter writer)
    {
        using var entry = writer.BeginEntry();
        var output = entry.PayloadWriter;
        var span = output.GetSpan(2);
        span[0] = FormatVersion;
        span[1] = (byte)DurableTaskCompletionSourceStatus.Completed;
        output.Advance(2);
        codec.Write(value, output);
        entry.Commit();
    }

    /// <inheritdoc/>
    public void WriteFaulted(Exception exception, JournalStreamWriter writer)
    {
        using var entry = writer.BeginEntry();
        var output = entry.PayloadWriter;
        var span = output.GetSpan(2);
        span[0] = FormatVersion;
        span[1] = (byte)DurableTaskCompletionSourceStatus.Faulted;
        output.Advance(2);
        exceptionCodec.Write(exception, output);
        entry.Commit();
    }

    /// <inheritdoc/>
    public void WriteCanceled(JournalStreamWriter writer)
    {
        using var entry = writer.BeginEntry();
        var output = entry.PayloadWriter;
        var span = output.GetSpan(2);
        span[0] = FormatVersion;
        span[1] = (byte)DurableTaskCompletionSourceStatus.Canceled;
        output.Advance(2);
        entry.Commit();
    }

    /// <inheritdoc/>
    public void Apply(JournalReadBuffer input, ITaskCompletionSourceOperationHandler<T> consumer)
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

    private void Apply<TInput>(ref Reader<TInput> reader, ITaskCompletionSourceOperationHandler<T> consumer)
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

}
