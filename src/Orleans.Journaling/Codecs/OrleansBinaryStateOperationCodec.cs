using System.Buffers;
using Orleans.Serialization.Buffers;

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
        JournalOperationWriter.Write(writer, output => WriteSetPayload(state, version, output));

    private void WriteSetPayload(T state, ulong version, IBufferWriter<byte> output)
    {
        var headerWriter = Writer.Create(output, session: null!);
        headerWriter.WriteByte(FormatVersion);
        headerWriter.WriteVarUInt32(SetValueCommand);
        headerWriter.Commit();
        codec.Write(state, output);
        var versionWriter = Writer.Create(output, session: null!);
        versionWriter.WriteVarUInt64(version);
        versionWriter.Commit();
    }

    /// <inheritdoc/>
    public void WriteClear(JournalStreamWriter writer) =>
        JournalOperationWriter.Write(writer, WriteClearPayload);

    private static void WriteClearPayload(IBufferWriter<byte> output)
    {
        var writer = Writer.Create(output, session: null!);
        writer.WriteByte(FormatVersion);
        writer.WriteVarUInt32(ClearValueCommand);
        writer.Commit();
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

    void IOrleansBinaryJournalEntryCodec.Apply(ReadOnlySequence<byte> input, IJournaledState state) =>
        Apply(input, DurableOperationHandler.GetRequiredHandler<IDurableStateOperationHandler<T>>(state, this));

    private void ApplySetValue(ref OrleansBinaryOperationReader reader, IDurableStateOperationHandler<T> consumer)
    {
        var state = reader.ReadValue("state", codec);
        var version = reader.ReadVarUInt64();
        reader.EnsureEnd();
        consumer.ApplySet(state, version);
    }
}
