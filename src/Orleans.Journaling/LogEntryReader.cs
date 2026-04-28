using System.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Reads the payload for one log entry.
/// </summary>
public ref struct LogEntryReader
{
    private readonly ReadOnlySequence<byte> _input;
    private SequenceReader<byte> _reader;

    /// <summary>
    /// Initializes a new instance of the <see cref="LogEntryReader"/> struct.
    /// </summary>
    /// <param name="input">The encoded log entry payload.</param>
    public LogEntryReader(ReadOnlySequence<byte> input)
    {
        _input = input;
        _reader = new(input);
    }

    /// <summary>
    /// Gets a value indicating whether all payload bytes have been consumed.
    /// </summary>
    public readonly bool End => _reader.End;

    /// <summary>
    /// Gets the number of bytes consumed from the payload.
    /// </summary>
    public readonly long Consumed => _reader.Consumed;

    /// <summary>
    /// Gets the unread payload bytes.
    /// </summary>
    public readonly ReadOnlySequence<byte> Remaining => _input.Slice(_reader.Consumed);

    /// <summary>
    /// Reads one byte from the payload.
    /// </summary>
    public byte ReadByte()
    {
        if (!_reader.TryRead(out var value))
        {
            ThrowInsufficientData();
        }

        return value;
    }

    /// <summary>
    /// Reads a LEB128-encoded unsigned 32-bit integer from the payload.
    /// </summary>
    public uint ReadVarUInt32() => VarIntHelper.ReadVarUInt32(ref _reader);

    /// <summary>
    /// Reads a LEB128-encoded unsigned 64-bit integer from the payload.
    /// </summary>
    public ulong ReadVarUInt64() => VarIntHelper.ReadVarUInt64(ref _reader);

    /// <summary>
    /// Reads the specified number of bytes from the payload.
    /// </summary>
    /// <param name="length">The number of bytes to read.</param>
    public ReadOnlySequence<byte> ReadBytes(int length)
    {
        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        if (_reader.Remaining < length)
        {
            ThrowInsufficientData();
        }

        var result = _input.Slice(_reader.Consumed, length);
        _reader.Advance(length);
        return result;
    }

    private static void ThrowInsufficientData() =>
        throw new InvalidOperationException("Insufficient data while reading a log entry.");
}
