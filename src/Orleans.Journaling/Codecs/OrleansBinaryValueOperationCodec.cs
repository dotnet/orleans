using System.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Binary codec for durable value journal entries, preserving the legacy Orleans binary wire format.
/// </summary>
internal sealed class OrleansBinaryValueOperationCodec<T>(
    IJournalValueCodec<T> codec) : IDurableValueOperationCodec<T>, IOrleansBinaryJournalEntryCodec
{
    private const byte FormatVersion = 0;
    private const uint SetValueCommand = 0;

    /// <inheritdoc/>
    public void WriteSet(T value, JournalStreamWriter writer) =>
        JournalOperationWriter.Write(writer, output => WriteSetPayload(value, output));

    private void WriteSetPayload(T value, IBufferWriter<byte> output)
    {
        WriteVersionByte(output);
        VarIntHelper.WriteVarUInt32(output, SetValueCommand);
        codec.Write(value, output);
    }

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, IDurableValueOperationHandler<T> consumer)
    {
        var reader = new OrleansBinaryOperationReader(input);
        var command = reader.ReadCommand();

        switch (command)
        {
            case SetValueCommand:
                var value = reader.ReadValue("value", codec);
                reader.EnsureEnd();
                consumer.ApplySet(value);
                break;
            default:
                throw new NotSupportedException($"Command type {command} is not supported");
        }
    }

    void IOrleansBinaryJournalEntryCodec.Apply(ReadOnlySequence<byte> input, IJournaledState state) =>
        Apply(input, DurableOperationHandler.GetRequiredHandler<IDurableValueOperationHandler<T>>(state, this));

    private static void WriteVersionByte(IBufferWriter<byte> output)
    {
        var span = output.GetSpan(1);
        span[0] = FormatVersion;
        output.Advance(1);
    }

}
