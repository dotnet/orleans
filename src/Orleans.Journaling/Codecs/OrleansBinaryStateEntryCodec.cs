using System.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Binary codec for <see cref="DurableStateEntry{T}"/> log entries,
/// preserving the legacy Orleans binary wire format.
/// </summary>
internal sealed class OrleansBinaryStateEntryCodec<T>(
    ILogDataCodec<T> codec) : ILogEntryCodec<DurableStateEntry<T>>
{
    private const byte FormatVersion = 0;
    private const uint SetValueCommand = 0;
    private const uint ClearValueCommand = 1;

    /// <inheritdoc/>
    public void Write(DurableStateEntry<T> entry, IBufferWriter<byte> output)
    {
        WriteVersionByte(output);

        switch (entry)
        {
            case StateSetEntry<T>(var state, var version):
                VarIntHelper.WriteVarUInt32(output, SetValueCommand);
                codec.Write(state, output);
                VarIntHelper.WriteVarUInt64(output, version);
                break;
            case StateClearEntry<T>:
                VarIntHelper.WriteVarUInt32(output, ClearValueCommand);
                break;
            default:
                throw new NotSupportedException($"Unsupported entry type: {entry.GetType()}");
        }
    }

    /// <inheritdoc/>
    public DurableStateEntry<T> Read(ReadOnlySequence<byte> input)
    {
        var reader = new SequenceReader<byte>(input);
        ReadVersionByte(ref reader);

        var command = VarIntHelper.ReadVarUInt32(ref reader);
        var remaining = input.Slice(reader.Consumed);

        return command switch
        {
            SetValueCommand => ReadSetValue(remaining),
            ClearValueCommand => new StateClearEntry<T>(),
            _ => throw new NotSupportedException($"Command type {command} is not supported"),
        };
    }

    private DurableStateEntry<T> ReadSetValue(ReadOnlySequence<byte> remaining)
    {
        var state = codec.Read(remaining, out var consumed);
        remaining = remaining.Slice(consumed);
        var reader = new SequenceReader<byte>(remaining);
        var version = VarIntHelper.ReadVarUInt64(ref reader);
        return new StateSetEntry<T>(state, version);
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
