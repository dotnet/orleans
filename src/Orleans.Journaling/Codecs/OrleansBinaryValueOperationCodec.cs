using Orleans.Serialization.Buffers;
using Orleans.Serialization.Session;

namespace Orleans.Journaling;

/// <summary>
/// Binary codec for durable value journal entries, preserving the legacy Orleans binary wire format.
/// </summary>
internal sealed class OrleansBinaryValueOperationCodec<T>(
    IJournalValueCodec<T> codec,
    SerializerSessionPool sessionPool) : IValueOperationCodec<T>
{
    private const byte FormatVersion = 0;
    private const uint SetValueCommand = 0;

    /// <inheritdoc/>
    public void WriteSet(T value, JournalStreamWriter writer)
    {
        using var entry = writer.BeginEntry();
        var output = entry.Writer;
        var payloadWriter = Writer.Create(output, session: null!);
        payloadWriter.WriteByte(FormatVersion);
        payloadWriter.WriteVarUInt32(SetValueCommand);
        payloadWriter.Commit();
        codec.Write(value, output);
        entry.Commit();
    }

    /// <inheritdoc/>
    public void Apply(JournalReadBuffer input, IValueOperationHandler<T> consumer)
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

    private void Apply<TInput>(ref Reader<TInput> reader, IValueOperationHandler<T> consumer)
    {
        OrleansBinaryOperationApplier.ReadVersion(ref reader);
        var command = reader.ReadVarUInt32();
        switch (command)
        {
            case SetValueCommand:
                consumer.ApplySet(codec.Read(ref reader));
                break;
            default:
                throw new NotSupportedException($"Command type {command} is not supported");
        }
    }
}
