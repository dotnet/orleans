using System.Buffers;
using global::MessagePack;

namespace Orleans.Journaling.MessagePack;

internal ref struct MessagePackOperationReader
{
    private MessagePackReader _reader;
    private readonly int _itemCount;
    private int _nextItemIndex;

    public MessagePackOperationReader(ReadOnlySequence<byte> input)
        : this(new MessagePackReader(input))
    {
    }

    public MessagePackOperationReader(MessagePackReader reader)
    {
        _reader = reader;
        _itemCount = _reader.ReadArrayHeader();
        if (_itemCount == 0)
        {
            throw new InvalidOperationException("Malformed MessagePack log entry: missing command.");
        }

        Command = _reader.ReadInt32();
        _nextItemIndex = 1;
    }

    public int Command { get; }

    public int OperandCount => _itemCount - 1;

    public void RequireOperandCount(int expectedOperandCount)
    {
        var expectedItemCount = expectedOperandCount + 1;
        if (_itemCount != expectedItemCount)
        {
            throw new InvalidOperationException($"Malformed MessagePack log entry: command {Command} expected {expectedItemCount} item(s), found {_itemCount}.");
        }
    }

    public int ReadSnapshotCount(int valuesPerItem, string? unbalancedMessage = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(valuesPerItem);

        if (OperandCount < 1)
        {
            throw new InvalidOperationException("Malformed MessagePack log entry: missing snapshot count.");
        }

        var count = ReadInt32();
        var valueCount = OperandCount - 1;
        MessagePackCodecHelpers.RequireSnapshotCount(count, valueCount / valuesPerItem, Command);
        if ((valueCount % valuesPerItem) != 0)
        {
            throw new InvalidOperationException(unbalancedMessage ?? $"Malformed MessagePack log entry: command {Command} snapshot item value count is not balanced.");
        }

        return count;
    }

    public T ReadValue<T>(MessagePackSerializerOptions options)
    {
        Advance();
        return MessagePackSerializer.Deserialize<T>(ref _reader, options);
    }

    public int ReadInt32()
    {
        Advance();
        return _reader.ReadInt32();
    }

    public ulong ReadUInt64()
    {
        Advance();
        return _reader.ReadUInt64();
    }

    public string? ReadString()
    {
        Advance();
        return _reader.ReadString();
    }

    public void EnsureEnd()
    {
        if (!_reader.End)
        {
            throw new InvalidOperationException("Malformed MessagePack log entry: trailing data.");
        }
    }

    private void Advance()
    {
        if (_nextItemIndex >= _itemCount)
        {
            throw new InvalidOperationException($"Malformed MessagePack log entry: command {Command} is missing operand {_nextItemIndex}.");
        }

        _nextItemIndex++;
    }
}
