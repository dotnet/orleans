using System.Buffers;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Session;

namespace Orleans.Journaling;

/// <summary>
/// Binary codec for durable persistent state journal entries, preserving the legacy Orleans binary wire format.
/// </summary>
internal sealed class OrleansBinaryStateOperationCodec<T>(
    IJournalValueCodec<T> codec,
    SerializerSessionPool sessionPool) : IStateOperationCodec<T>
{
    private const byte FormatVersion = 0;
    private const uint SetValueCommand = 0;
    private const uint ClearValueCommand = 1;

    /// <inheritdoc/>
    public void WriteSet(T state, ulong version, JournalStreamWriter writer)
    {
        JournalOperationWriter.Write(
            writer,
            (codec: this, state, version),
            static (output, operation) => operation.codec.WriteSetPayload(operation.state, operation.version, output));
    }

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
    public void WriteClear(JournalStreamWriter writer)
    {
        JournalOperationWriter.Write(
            writer,
            ClearValueCommand,
            static (output, command) => WriteHeader(output, command));
    }

    private static void WriteHeader(IBufferWriter<byte> output, uint command)
    {
        var writer = Writer.Create(output, session: null!);
        writer.WriteByte(FormatVersion);
        writer.WriteVarUInt32(command);
        writer.Commit();
    }

    /// <inheritdoc/>
    public void Apply(JournalReadBuffer input, IStateOperationHandler<T> consumer)
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

    private void Apply<TInput>(ref Reader<TInput> reader, IStateOperationHandler<T> consumer)
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
