using System.Buffers;
using Orleans.Serialization.Buffers;

namespace Orleans.Journaling;

internal ref struct OrleansBinaryOperationReader
{
    private const byte FormatVersion = 0;

    private readonly ReadOnlySequence<byte> _input;
    private Reader<ReadOnlySequenceInput> _reader;

    public OrleansBinaryOperationReader(ReadOnlySequence<byte> input)
    {
        _input = input;
        _reader = Reader.Create(input, session: null!);
        ReadVersion();
    }

    public uint ReadCommand() => _reader.ReadVarUInt32();

    public byte ReadByte(string fieldName)
    {
        if (_reader.Position >= _input.Length)
        {
            throw new InvalidOperationException($"Insufficient data while reading {fieldName}.");
        }

        return _reader.ReadByte();
    }

    public int ReadListIndex() => OrleansBinaryCollectionWireHelpers.ReadListIndex(ref _reader);

    public int ReadSnapshotCount() => OrleansBinaryCollectionWireHelpers.ReadSnapshotCount(ref _reader);

    public ulong ReadVarUInt64() => _reader.ReadVarUInt64();

    public T ReadValue<T>(string operandName, IJournalValueCodec<T> codec)
    {
        var value = codec.Read(_input.Slice(_reader.Position), out var bytesConsumed);
        Advance(bytesConsumed, operandName);
        return value;
    }

    public void EnsureEnd()
    {
        if (_reader.Position != _input.Length)
        {
            throw new InvalidOperationException("Unexpected trailing data after binary journal operation.");
        }
    }

    private void ReadVersion()
    {
        if (_reader.Position >= _input.Length)
        {
            throw new InvalidOperationException("Insufficient data while reading binary journal operation format version.");
        }

        var version = _reader.ReadByte();
        if (version != FormatVersion)
        {
            throw new NotSupportedException($"Unsupported format version: {version}");
        }
    }

    private void Advance(long bytesConsumed, string operandName)
    {
        if (bytesConsumed < 0)
        {
            throw new InvalidOperationException(
                $"The value codec for binary journal operation operand '{operandName}' reported consuming {bytesConsumed} bytes, which is negative.");
        }

        var remaining = _input.Length - _reader.Position;
        if ((ulong)bytesConsumed > (ulong)remaining)
        {
            throw new InvalidOperationException(
                $"The value codec for binary journal operation operand '{operandName}' reported consuming {bytesConsumed} bytes, but {remaining} bytes remain.");
        }

        _reader.Skip(bytesConsumed);
    }
}
