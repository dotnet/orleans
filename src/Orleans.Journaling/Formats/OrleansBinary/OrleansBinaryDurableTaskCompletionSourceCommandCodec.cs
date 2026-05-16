using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Session;

namespace Orleans.Journaling;

/// <summary>
/// Binary codec for durable task completion source journal entries, preserving the legacy Orleans binary wire format.
/// </summary>
/// <remarks>
/// Unlike other durable type codecs, the TCS format uses a status byte instead of a
/// VarUInt32 command discriminator.
/// </remarks>
internal sealed class OrleansBinaryDurableTaskCompletionSourceCommandCodec<T>(
    IFieldCodec<T> codec,
    IFieldCodec<Exception> exceptionCodec,
    SerializerSessionPool sessionPool) : IDurableTaskCompletionSourceCommandCodec<T>
{
    /// <inheritdoc/>
    public void WritePending(JournalStreamWriter writer)
    {
        using var entry = writer.BeginEntry();
        var output = entry.Writer;
        var span = output.GetSpan(1);
        span[0] = (byte)DurableTaskCompletionSourceStatus.Pending;
        output.Advance(1);
        entry.Commit();
    }

    /// <inheritdoc/>
    public void WriteCompleted(T value, JournalStreamWriter writer)
    {
        using var entry = writer.BeginEntry();
        var output = entry.Writer;
        var span = output.GetSpan(1);
        span[0] = (byte)DurableTaskCompletionSourceStatus.Completed;
        output.Advance(1);
        OrleansBinaryCommandCodecHelpers.WriteValue(codec, value, output, sessionPool);
        entry.Commit();
    }

    /// <inheritdoc/>
    public void WriteFaulted(Exception exception, JournalStreamWriter writer)
    {
        using var entry = writer.BeginEntry();
        var output = entry.Writer;
        var span = output.GetSpan(1);
        span[0] = (byte)DurableTaskCompletionSourceStatus.Faulted;
        output.Advance(1);
        OrleansBinaryCommandCodecHelpers.WriteValue(exceptionCodec, exception, output, sessionPool);
        entry.Commit();
    }

    /// <inheritdoc/>
    public void WriteCanceled(JournalStreamWriter writer)
    {
        using var entry = writer.BeginEntry();
        var output = entry.Writer;
        var span = output.GetSpan(1);
        span[0] = (byte)DurableTaskCompletionSourceStatus.Canceled;
        output.Advance(1);
        entry.Commit();
    }

    /// <inheritdoc/>
    public void Apply(JournalBufferReader input, IDurableTaskCompletionSourceCommandHandler<T> consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        using var slice = input.Peek(input.Length);
        using var session = sessionPool.GetSession();
        var reader = Reader.Create(slice, session);
        Apply(ref reader, consumer);
        if (reader.Position != reader.Length)
        {
            throw new InvalidOperationException("Unexpected trailing data after binary journal command.");
        }
    }

    private void Apply<TInput>(ref Reader<TInput> reader, IDurableTaskCompletionSourceCommandHandler<T> consumer)
    {
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
                consumer.ApplyCompleted(OrleansBinaryCommandCodecHelpers.ReadValue(codec, ref reader));
                break;
            case DurableTaskCompletionSourceStatus.Faulted:
                consumer.ApplyFaulted(OrleansBinaryCommandCodecHelpers.ReadValue(exceptionCodec, ref reader));
                break;
            case DurableTaskCompletionSourceStatus.Canceled:
                consumer.ApplyCanceled();
                break;
            default:
                throw new NotSupportedException($"Unsupported status: {status}");
        }
    }

}
