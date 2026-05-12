using System.Buffers;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Buffers.Adaptors;
using Orleans.Serialization.Session;

namespace Orleans.Journaling;

/// <summary>
/// Binary codec for durable persistent state journal entries, preserving the legacy Orleans binary wire format.
/// </summary>
internal sealed class OrleansBinaryStateOperationCodec<T>(
    IJournalValueCodec<T> codec,
    SerializerSessionPool sessionPool) : IStateOperationCodec<T>, IOrleansBinaryJournalEntryCodec
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
    public void Apply(ReadOnlySequence<byte> input, IStateOperationHandler<T> consumer)
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

    void IOrleansBinaryJournalEntryCodec.Apply(ref Reader<ArcBufferReaderInput> reader, IJournaledState state) =>
        Apply(ref reader, DurableOperationHandler.GetRequiredHandler<IStateOperationHandler<T>>(state, this));

    private void Apply(ref Reader<ArcBufferReaderInput> reader, IStateOperationHandler<T> consumer)
    {
        OrleansBinaryOperationApplier.ReadVersion(ref reader);
        var command = reader.ReadVarUInt32();
        switch (command)
        {
            case SetValueCommand:
                var state = codec.Read(ref reader);
                var version = reader.ReadVarUInt64();
                consumer.ApplySet(state, version);
                break;
            case ClearValueCommand:
                consumer.ApplyClear();
                break;
            default:
                throw new NotSupportedException($"Command type {command} is not supported");
        }
    }
}
