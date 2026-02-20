using System.Buffers;
using Google.Protobuf;

namespace Orleans.Journaling.Protobuf;

/// <summary>
/// An <see cref="ILogEntryCodecFactory"/> implementation that encodes entire log entries using Protocol Buffers.
/// </summary>
/// <remarks>
/// <para>
/// Each log entry is written as: [version byte 2] [protobuf payload].
/// The payload uses a length-delimited sequence of fields written in order.
/// </para>
/// </remarks>
internal sealed class ProtobufEntryCodec : ILogEntryCodecFactory
{
    /// <summary>
    /// The version byte identifying the Protocol Buffers format.
    /// </summary>
    public const byte FormatVersion = 2;

    /// <inheritdoc/>
    public byte Version => FormatVersion;

    /// <inheritdoc/>
    public ILogEntryWriter CreateWriter() => new ProtobufEntryWriter();

    /// <inheritdoc/>
    public ILogEntryReader CreateReader(ReadOnlySequence<byte> data) => new ProtobufEntryReader(data);
}

/// <summary>
/// Writer that encodes fields using Protocol Buffers wire format.
/// Fields are written as length-prefixed byte sequences.
/// </summary>
internal sealed class ProtobufEntryWriter : ILogEntryWriter
{
    private readonly ArrayBufferWriter<byte> _buffer = new();

    /// <inheritdoc/>
    public void WriteCommand(uint command) => WriteVarint(command);

    /// <inheritdoc/>
    public void WriteUInt32(uint value) => WriteVarint(value);

    /// <inheritdoc/>
    public void WriteUInt64(ulong value) => WriteVarint64(value);

    /// <inheritdoc/>
    public void WriteByte(byte value)
    {
        var span = _buffer.GetSpan(1);
        span[0] = value;
        _buffer.Advance(1);
    }

    /// <inheritdoc/>
    public void WriteValue<T>(ILogDataCodec<T> codec, T value)
    {
        // Serialize value into a temporary buffer, then write length-prefixed.
        var tempBuffer = new ArrayBufferWriter<byte>();
        codec.Write(value, tempBuffer);
        var written = tempBuffer.WrittenSpan;

        // Write length prefix.
        WriteVarint((uint)written.Length);

        // Write data.
        var dest = _buffer.GetSpan(written.Length);
        written.CopyTo(dest);
        _buffer.Advance(written.Length);
    }

    /// <inheritdoc/>
    public void WriteTo(IBufferWriter<byte> output)
    {
        // Write the version byte first.
        var versionSpan = output.GetSpan(1);
        versionSpan[0] = ProtobufEntryCodec.FormatVersion;
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

    private void WriteVarint(uint value)
    {
        var span = _buffer.GetSpan(5);
        var count = 0;
        do
        {
            var b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0) b |= 0x80;
            span[count++] = b;
        } while (value != 0);
        _buffer.Advance(count);
    }

    private void WriteVarint64(ulong value)
    {
        var span = _buffer.GetSpan(10);
        var count = 0;
        do
        {
            var b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0) b |= 0x80;
            span[count++] = b;
        } while (value != 0);
        _buffer.Advance(count);
    }
}

/// <summary>
/// Reader that decodes fields from Protocol Buffers wire format.
/// </summary>
internal sealed class ProtobufEntryReader : ILogEntryReader
{
    private ReadOnlySequence<byte> _remaining;

    public ProtobufEntryReader(ReadOnlySequence<byte> data)
    {
        _remaining = data;
    }

    /// <inheritdoc/>
    public uint ReadCommand() => ReadVarint32();

    /// <inheritdoc/>
    public uint ReadUInt32() => ReadVarint32();

    /// <inheritdoc/>
    public ulong ReadUInt64() => ReadVarint64();

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
        // Read length prefix.
        var length = ReadVarint32();

        // Read the value data.
        var valueData = _remaining.Slice(0, length);
        var result = codec.Read(valueData, out _);
        _remaining = _remaining.Slice(length);
        return result;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
    }

    private uint ReadVarint32()
    {
        var reader = new SequenceReader<byte>(_remaining);
        uint result = 0;
        var shift = 0;
        byte b;
        do
        {
            if (!reader.TryRead(out b))
                throw new InvalidOperationException("Insufficient data while reading a variable-length integer.");
            result |= (uint)(b & 0x7F) << shift;
            shift += 7;
        } while ((b & 0x80) != 0);
        _remaining = _remaining.Slice(reader.Consumed);
        return result;
    }

    private ulong ReadVarint64()
    {
        var reader = new SequenceReader<byte>(_remaining);
        ulong result = 0;
        var shift = 0;
        byte b;
        do
        {
            if (!reader.TryRead(out b))
                throw new InvalidOperationException("Insufficient data while reading a variable-length integer.");
            result |= (ulong)(b & 0x7F) << shift;
            shift += 7;
        } while ((b & 0x80) != 0);
        _remaining = _remaining.Slice(reader.Consumed);
        return result;
    }
}
