using System.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Binary codec for durable persistent state journal entries, preserving the legacy Orleans binary wire format.
/// </summary>
internal sealed class OrleansBinaryStateOperationCodec<T>(
    IJournalValueCodec<T> codec) : IDurableStateOperationCodec<T>, IOrleansBinaryJournalEntryCodec
{
    private const byte FormatVersion = 0;
    private const uint SetValueCommand = 0;
    private const uint ClearValueCommand = 1;

    /// <inheritdoc/>
    public void WriteSet(T state, ulong version, JournalStreamWriter writer) =>
        DurableOperationCodecWriter.Write(writer, output => WriteSetPayload(state, version, output));

    private void WriteSetPayload(T state, ulong version, IBufferWriter<byte> output)
    {
        WriteVersionByte(output);
        VarIntHelper.WriteVarUInt32(output, SetValueCommand);
        codec.Write(state, output);
        VarIntHelper.WriteVarUInt64(output, version);
    }

    /// <inheritdoc/>
    public void WriteClear(JournalStreamWriter writer) =>
        DurableOperationCodecWriter.Write(writer, WriteClearPayload);

    private static void WriteClearPayload(IBufferWriter<byte> output)
    {
        WriteVersionByte(output);
        VarIntHelper.WriteVarUInt32(output, ClearValueCommand);
    }

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, IDurableStateOperationHandler<T> consumer)
    {
        var reader = new OrleansBinaryOperationReader(input);
        var command = reader.ReadCommand();

        switch (command)
        {
            case SetValueCommand:
                ApplySetValue(ref reader, consumer);
                break;
            case ClearValueCommand:
                reader.EnsureEnd();
                consumer.ApplyClear();
                break;
            default:
                throw new NotSupportedException($"Command type {command} is not supported");
        }
    }

    void IOrleansBinaryJournalEntryCodec.Apply(ReadOnlySequence<byte> input, IDurableStateMachine stateMachine) =>
        Apply(input, DurableOperationHandler.GetRequiredHandler<IDurableStateOperationHandler<T>>(stateMachine, this));

    private void ApplySetValue(ref OrleansBinaryOperationReader reader, IDurableStateOperationHandler<T> consumer)
    {
        var state = reader.ReadValue("state", codec);
        var version = reader.ReadVarUInt64();
        reader.EnsureEnd();
        consumer.ApplySet(state, version);
    }

    private static void WriteVersionByte(IBufferWriter<byte> output)
    {
        var span = output.GetSpan(1);
        span[0] = FormatVersion;
        output.Advance(1);
    }

}
