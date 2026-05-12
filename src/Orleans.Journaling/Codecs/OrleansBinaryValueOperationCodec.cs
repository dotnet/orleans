using System.Buffers;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Buffers.Adaptors;
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
    public void WriteSet(T value, JournalStreamWriter writer) =>
        JournalOperationWriter.Write(writer, output => WriteSetPayload(value, output));

    private void WriteSetPayload(T value, IBufferWriter<byte> output)
    {
        var writer = Writer.Create(output, session: null!);
        writer.WriteByte(FormatVersion);
        writer.WriteVarUInt32(SetValueCommand);
        writer.Commit();
        codec.Write(value, output);
    }

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, IValueOperationHandler<T> consumer)
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

    private void Apply(ref Reader<ArcBufferReaderInput> reader, IValueOperationHandler<T> consumer)
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
