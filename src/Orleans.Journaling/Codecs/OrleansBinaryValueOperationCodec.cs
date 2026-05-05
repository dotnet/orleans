using System.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Binary codec for durable value log entries, preserving the legacy Orleans binary wire format.
/// </summary>
internal sealed class OrleansBinaryValueOperationCodec<T>(
    ILogValueCodec<T> codec) : IDurableValueOperationCodec<T>
{
    private const byte FormatVersion = 0;
    private const uint SetValueCommand = 0;

    /// <inheritdoc/>
    public void WriteSet(T value, LogStreamWriter writer) =>
        DurableOperationCodecWriter.Write(writer, output => WriteSetPayload(value, output));

    private void WriteSetPayload(T value, IBufferWriter<byte> output)
    {
        WriteVersionByte(output);
        VarIntHelper.WriteVarUInt32(output, SetValueCommand);
        codec.Write(value, output);
    }

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, IDurableValueOperationHandler<T> consumer)
    {
        var reader = new SequenceReader<byte>(input);
        ReadVersionByte(ref reader);

        var command = VarIntHelper.ReadVarUInt32(ref reader);
        var remaining = input.Slice(reader.Consumed);

        switch (command)
        {
            case SetValueCommand:
                consumer.ApplySet(codec.Read(remaining, out _));
                break;
            default:
                throw new NotSupportedException($"Command type {command} is not supported");
        }
    }

    private static void WriteVersionByte(IBufferWriter<byte> output)
    {
        var span = output.GetSpan(1);
        span[0] = FormatVersion;
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
