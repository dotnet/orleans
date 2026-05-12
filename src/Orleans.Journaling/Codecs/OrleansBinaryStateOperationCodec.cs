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
        using var entry = writer.BeginEntry();
        var output = entry.Writer;
        var headerWriter = Writer.Create(output, session: null!);
        headerWriter.WriteByte(FormatVersion);
        headerWriter.WriteVarUInt32(SetValueCommand);
        headerWriter.Commit();
        codec.Write(state, output);
        var versionWriter = Writer.Create(output, session: null!);
        versionWriter.WriteVarUInt64(version);
        versionWriter.Commit();
        entry.Commit();
    }

    /// <inheritdoc/>
    public void WriteClear(JournalStreamWriter writer)
    {
        using var entry = writer.BeginEntry();
        var payloadWriter = Writer.Create(entry.Writer, session: null!);
        payloadWriter.WriteByte(FormatVersion);
        payloadWriter.WriteVarUInt32(ClearValueCommand);
        payloadWriter.Commit();
        entry.Commit();
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
