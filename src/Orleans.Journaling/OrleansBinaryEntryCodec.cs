using System.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// An <see cref="ILogEntryCodecFactory"/> implementation that preserves the legacy Orleans binary wire format.
/// </summary>
/// <remarks>
/// <para>
/// This codec writes entries using the original format: a version byte (0), followed by
/// VarUInt32-encoded command types, VarUInt-encoded integers, and Orleans <see cref="ILogDataCodec{T}"/>
/// serialized values. This is the default codec used when no other codec is configured,
/// ensuring backward compatibility with existing persisted data.
/// </para>
/// </remarks>
internal sealed class OrleansBinaryEntryCodec : ILogEntryCodecFactory
{
    /// <summary>
    /// The version byte identifying the legacy Orleans binary format.
    /// </summary>
    public const byte FormatVersion = 0;

    /// <inheritdoc/>
    public byte Version => FormatVersion;

    /// <inheritdoc/>
    public ILogEntryWriter CreateWriter() => new OrleansBinaryEntryWriter();

    /// <inheritdoc/>
    public ILogEntryReader CreateReader(ReadOnlySequence<byte> data) => new OrleansBinaryEntryReader(data);
}

/// <summary>
/// Writer for the legacy Orleans binary log entry format.
/// </summary>
internal sealed class OrleansBinaryEntryWriter : ILogEntryWriter
{
    private readonly ArrayBufferWriter<byte> _buffer = new();

    /// <inheritdoc/>
    public void WriteCommand(uint command) => VarIntHelper.WriteVarUInt32(_buffer, command);

    /// <inheritdoc/>
    public void WriteUInt32(uint value) => VarIntHelper.WriteVarUInt32(_buffer, value);

    /// <inheritdoc/>
    public void WriteUInt64(ulong value) => VarIntHelper.WriteVarUInt64(_buffer, value);

    /// <inheritdoc/>
    public void WriteByte(byte value)
    {
        var span = _buffer.GetSpan(1);
        span[0] = value;
        _buffer.Advance(1);
    }

    /// <inheritdoc/>
    public void WriteValue<T>(ILogDataCodec<T> codec, T value) => codec.Write(value, _buffer);

    /// <inheritdoc/>
    public void WriteTo(IBufferWriter<byte> output)
    {
        // Write the version byte first.
        var versionSpan = output.GetSpan(1);
        versionSpan[0] = OrleansBinaryEntryCodec.FormatVersion;
        output.Advance(1);

        // Copy the buffered entry data.
        var written = _buffer.WrittenSpan;
        var dest = output.GetSpan(written.Length);
        written.CopyTo(dest);
        output.Advance(written.Length);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
    }
}

/// <summary>
/// Reader for the legacy Orleans binary log entry format.
/// </summary>
internal sealed class OrleansBinaryEntryReader : ILogEntryReader
{
    private ReadOnlySequence<byte> _remaining;

    public OrleansBinaryEntryReader(ReadOnlySequence<byte> data)
    {
        _remaining = data;
    }

    /// <inheritdoc/>
    public uint ReadCommand()
    {
        var reader = new SequenceReader<byte>(_remaining);
        var result = VarIntHelper.ReadVarUInt32(ref reader);
        _remaining = _remaining.Slice(reader.Consumed);
        return result;
    }

    /// <inheritdoc/>
    public uint ReadUInt32()
    {
        var reader = new SequenceReader<byte>(_remaining);
        var result = VarIntHelper.ReadVarUInt32(ref reader);
        _remaining = _remaining.Slice(reader.Consumed);
        return result;
    }

    /// <inheritdoc/>
    public ulong ReadUInt64()
    {
        var reader = new SequenceReader<byte>(_remaining);
        var result = VarIntHelper.ReadVarUInt64(ref reader);
        _remaining = _remaining.Slice(reader.Consumed);
        return result;
    }

    /// <inheritdoc/>
    public byte ReadByte()
    {
        var reader = new SequenceReader<byte>(_remaining);
        if (!reader.TryRead(out var value))
        {
            throw new InvalidOperationException("Insufficient data while reading a byte.");
        }

        _remaining = _remaining.Slice(reader.Consumed);
        return value;
    }

    /// <inheritdoc/>
    public T ReadValue<T>(ILogDataCodec<T> codec)
    {
        var result = codec.Read(_remaining, out var bytesConsumed);
        _remaining = _remaining.Slice(bytesConsumed);
        return result;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
    }
}
