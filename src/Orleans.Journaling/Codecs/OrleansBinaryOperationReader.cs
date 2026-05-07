using System.Buffers;

namespace Orleans.Journaling;

internal ref struct OrleansBinaryOperationReader
{
    private const byte FormatVersion = 0;

    private readonly ReadOnlySequence<byte> _input;
    private SequenceReader<byte> _reader;

    public OrleansBinaryOperationReader(ReadOnlySequence<byte> input)
    {
        _input = input;
        _reader = new SequenceReader<byte>(input);
        ReadVersion();
    }

    public uint ReadCommand() => VarIntHelper.ReadVarUInt32(ref _reader);

    public byte ReadByte(string fieldName)
    {
        if (!_reader.TryRead(out var value))
        {
            throw new InvalidOperationException($"Insufficient data while reading {fieldName}.");
        }

        return value;
    }

    public int ReadListIndex() => CollectionCodecHelpers.ReadListIndex(ref _reader);

    public int ReadSnapshotCount() => CollectionCodecHelpers.ReadSnapshotCount(ref _reader);

    public ulong ReadVarUInt64() => VarIntHelper.ReadVarUInt64(ref _reader);

    public T ReadValue<T>(string operandName, ILogValueCodec<T> codec)
    {
        var value = codec.Read(_input.Slice(_reader.Consumed), out var bytesConsumed);
        Advance(bytesConsumed, operandName);
        return value;
    }

    public void EnsureEnd()
    {
        if (_reader.Consumed != _input.Length)
        {
            throw new InvalidOperationException("Unexpected trailing data after binary journal operation.");
        }
    }

    private void ReadVersion()
    {
        if (!_reader.TryRead(out var version) || version != FormatVersion)
        {
            throw new NotSupportedException($"Unsupported format version: {version}");
        }
    }

    private void Advance(long bytesConsumed, string operandName)
    {
        var remaining = _input.Length - _reader.Consumed;
        if ((ulong)bytesConsumed > (ulong)remaining)
        {
            throw new InvalidOperationException(
                $"The value codec for binary journal operation operand '{operandName}' reported consuming {bytesConsumed} bytes, but {remaining} bytes remain.");
        }

        _reader.Advance(bytesConsumed);
    }
}
