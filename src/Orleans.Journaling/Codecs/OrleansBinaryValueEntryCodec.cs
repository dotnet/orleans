using System.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Binary codec for <see cref="DurableValueEntry{T}"/> log entries,
/// preserving the legacy Orleans binary wire format.
/// </summary>
internal sealed class OrleansBinaryValueEntryCodec<T>(
    ILogDataCodec<T> codec) : ILogEntryCodec<DurableValueEntry<T>>
{
    private const byte FormatVersion = 0;
    private const uint SetValueCommand = 0;

    /// <inheritdoc/>
    public void Write(DurableValueEntry<T> entry, IBufferWriter<byte> output)
    {
        WriteVersionByte(output);

        switch (entry)
        {
            case ValueSetEntry<T>(var value):
                VarIntHelper.WriteVarUInt32(output, SetValueCommand);
                codec.Write(value, output);
                break;
            default:
                throw new NotSupportedException($"Unsupported entry type: {entry.GetType()}");
        }
    }

    /// <inheritdoc/>
    public DurableValueEntry<T> Read(ReadOnlySequence<byte> input)
    {
        var reader = new SequenceReader<byte>(input);
        ReadVersionByte(ref reader);

        var command = VarIntHelper.ReadVarUInt32(ref reader);
        var remaining = input.Slice(reader.Consumed);

        return command switch
        {
            SetValueCommand => new ValueSetEntry<T>(codec.Read(remaining, out _)),
            _ => throw new NotSupportedException($"Command type {command} is not supported"),
        };
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
