using System.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Binary codec for durable persistent state log entries, preserving the legacy Orleans binary wire format.
/// </summary>
internal sealed class OrleansBinaryStateEntryCodec<T>(
    ILogDataCodec<T> codec) : IDurableStateCodec<T>
{
    private const byte FormatVersion = 0;
    private const uint SetValueCommand = 0;
    private const uint ClearValueCommand = 1;

    /// <inheritdoc/>
    public void WriteSet(T state, ulong version, IBufferWriter<byte> output)
    {
        WriteVersionByte(output);
        VarIntHelper.WriteVarUInt32(output, SetValueCommand);
        codec.Write(state, output);
        VarIntHelper.WriteVarUInt64(output, version);
    }

    /// <inheritdoc/>
    public void WriteClear(IBufferWriter<byte> output)
    {
        WriteVersionByte(output);
        VarIntHelper.WriteVarUInt32(output, ClearValueCommand);
    }

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, IDurableStateLogEntryConsumer<T> consumer)
    {
        var reader = new SequenceReader<byte>(input);
        ReadVersionByte(ref reader);

        var command = VarIntHelper.ReadVarUInt32(ref reader);
        var remaining = input.Slice(reader.Consumed);

        switch (command)
        {
            case SetValueCommand:
                ApplySetValue(remaining, consumer);
                break;
            case ClearValueCommand:
                consumer.ApplyClear();
                break;
            default:
                throw new NotSupportedException($"Command type {command} is not supported");
        }
    }

    private void ApplySetValue(ReadOnlySequence<byte> remaining, IDurableStateLogEntryConsumer<T> consumer)
    {
        var state = codec.Read(remaining, out var consumed);
        remaining = remaining.Slice(consumed);
        var reader = new SequenceReader<byte>(remaining);
        var version = VarIntHelper.ReadVarUInt64(ref reader);
        consumer.ApplySet(state, version);
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
