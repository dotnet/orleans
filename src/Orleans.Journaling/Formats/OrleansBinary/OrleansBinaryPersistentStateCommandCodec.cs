using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Session;

namespace Orleans.Journaling;

/// <summary>
/// Binary codec for durable persistent state journal entries, preserving the legacy Orleans binary wire format.
/// </summary>
internal sealed class OrleansBinaryPersistentStateCommandCodec<T>(
    IFieldCodec<T> codec,
    SerializerSessionPool sessionPool) : IPersistentStateCommandCodec<T>
{
    private const uint SetValueCommand = 0;
    private const uint ClearValueCommand = 1;

    /// <inheritdoc/>
    public void WriteSet(T state, ulong version, JournalStreamWriter writer)
    {
        using var entry = writer.BeginEntry();
        var output = entry.Writer;
        var headerWriter = Writer.Create(output, session: null!);
        headerWriter.WriteVarUInt32(SetValueCommand);
        headerWriter.Commit();
        OrleansBinaryCommandCodecHelpers.WriteValue(codec, state, output, sessionPool);
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
        payloadWriter.WriteVarUInt32(ClearValueCommand);
        payloadWriter.Commit();
        entry.Commit();
    }

    /// <inheritdoc/>
    public void Apply(JournalBufferReader input, IPersistentStateCommandHandler<T> consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        using var slice = input.Peek(input.Length);
        using var session = sessionPool.GetSession();
        var reader = Reader.Create(slice, session);
        Apply(ref reader, consumer);
        if (reader.Position != reader.Length)
        {
            throw new InvalidOperationException("Unexpected trailing data after binary journal command.");
        }
    }

    private void Apply<TInput>(ref Reader<TInput> reader, IPersistentStateCommandHandler<T> consumer)
    {
        var command = reader.ReadVarUInt32();
        switch (command)
        {
            case SetValueCommand:
                var state = OrleansBinaryCommandCodecHelpers.ReadValue(codec, ref reader);
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
