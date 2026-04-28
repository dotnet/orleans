using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace Orleans.Journaling.Protobuf;

internal static class ProtobufWire
{
    public const uint WireTypeVarint = 0;
    public const uint WireTypeFixed64 = 1;
    public const uint WireTypeLengthDelimited = 2;
    public const uint WireTypeFixed32 = 5;

    public static void WriteTag(IBufferWriter<byte> output, uint fieldNumber, uint wireType)
        => WriteVarUInt32(output, (fieldNumber << 3) | wireType);

    public static void WriteUInt32Field(IBufferWriter<byte> output, uint fieldNumber, uint value)
    {
        WriteTag(output, fieldNumber, WireTypeVarint);
        WriteVarUInt32(output, value);
    }

    public static void WriteUInt64Field(IBufferWriter<byte> output, uint fieldNumber, ulong value)
    {
        WriteTag(output, fieldNumber, WireTypeVarint);
        WriteVarUInt64(output, value);
    }

    public static void WriteStringField(IBufferWriter<byte> output, uint fieldNumber, string value)
        => WriteBytesField(output, fieldNumber, Encoding.UTF8.GetBytes(value));

    public static void WriteBytesField(IBufferWriter<byte> output, uint fieldNumber, ReadOnlySpan<byte> value)
    {
        WriteTag(output, fieldNumber, WireTypeLengthDelimited);
        WriteVarUInt32(output, (uint)value.Length);
        WriteRaw(output, value);
    }

    public static void WriteBytesField(IBufferWriter<byte> output, uint fieldNumber, ReadOnlySequence<byte> value)
    {
        WriteTag(output, fieldNumber, WireTypeLengthDelimited);
        WriteLengthDelimited(output, value);
    }

    public static void WriteBytesField<TState>(IBufferWriter<byte> output, uint fieldNumber, int length, TState state, Action<TState, IBufferWriter<byte>> write)
    {
        if (length < 0)
        {
            throw new InvalidOperationException("Length-delimited protobuf value cannot have a negative length.");
        }

        WriteTag(output, fieldNumber, WireTypeLengthDelimited);
        WriteVarUInt32(output, (uint)length);
        write(state, output);
    }

    public static void WriteLengthDelimited(IBufferWriter<byte> output, ReadOnlySpan<byte> value)
    {
        WriteVarUInt32(output, (uint)value.Length);
        WriteRaw(output, value);
    }

    public static void WriteLengthDelimited(IBufferWriter<byte> output, ReadOnlySequence<byte> value)
    {
        if (value.Length > uint.MaxValue)
        {
            throw new InvalidOperationException("Length-delimited protobuf value is too large.");
        }

        WriteVarUInt32(output, (uint)value.Length);
        WriteRaw(output, value);
    }

    public static uint ReadTag(ref SequenceReader<byte> reader)
    {
        var tag = ReadVarUInt32(ref reader);
        if (tag == 0)
        {
            throw new InvalidOperationException("Malformed protobuf log entry: field number 0 is not valid.");
        }

        return tag;
    }

    public static uint ReadUInt32(ref SequenceReader<byte> reader) => ReadVarUInt32(ref reader);

    public static ulong ReadUInt64(ref SequenceReader<byte> reader) => ReadVarUInt64(ref reader);

    public static void RequireCommand(bool hasCommand)
    {
        if (!hasCommand)
        {
            throw new InvalidOperationException("Malformed protobuf log entry: missing required field 'command'.");
        }
    }

    public static void RequireNoDuplicateCommand(bool hasCommand)
    {
        if (hasCommand)
        {
            throw new InvalidOperationException("Malformed protobuf log entry: duplicate field 'command'.");
        }
    }

    public static void RequireField(bool hasField, string fieldName, uint command)
    {
        if (!hasField)
        {
            throw new InvalidOperationException($"Malformed protobuf log entry: missing required field '{fieldName}' for command {command}.");
        }
    }

    public static T RequireValue<T>(bool hasField, T? value, string fieldName, uint command)
    {
        RequireField(hasField, fieldName, command);
        return value!;
    }

    public static void RequireSnapshotCount(int expectedCount, int actualCount, uint command)
    {
        if (expectedCount != actualCount)
        {
            throw new InvalidOperationException($"Malformed protobuf log entry: command {command} declared {expectedCount} snapshot item(s) but contained {actualCount}.");
        }
    }

    public static ReadOnlySequence<byte> ReadBytes(ref SequenceReader<byte> reader)
    {
        var length = ReadVarUInt32(ref reader);
        EnsureRemaining(ref reader, length, "length-delimited field");
        var result = reader.Sequence.Slice(reader.Position, length);
        reader.Advance(length);
        return result;
    }

    public static string ReadString(ref SequenceReader<byte> reader)
    {
        var bytes = ReadBytes(ref reader);
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    public static int ComputeVarUInt32Size(uint value)
    {
        if (value < 1u << 7)
        {
            return 1;
        }

        if (value < 1u << 14)
        {
            return 2;
        }

        if (value < 1u << 21)
        {
            return 3;
        }

        if (value < 1u << 28)
        {
            return 4;
        }

        return 5;
    }

    public static int ComputeVarUInt64Size(ulong value)
    {
        if (value < 1UL << 7)
        {
            return 1;
        }

        if (value < 1UL << 14)
        {
            return 2;
        }

        if (value < 1UL << 21)
        {
            return 3;
        }

        if (value < 1UL << 28)
        {
            return 4;
        }

        if (value < 1UL << 35)
        {
            return 5;
        }

        if (value < 1UL << 42)
        {
            return 6;
        }

        if (value < 1UL << 49)
        {
            return 7;
        }

        if (value < 1UL << 56)
        {
            return 8;
        }

        if (value < 1UL << 63)
        {
            return 9;
        }

        return 10;
    }

    public static void WriteInt32Value(IBufferWriter<byte> output, int value)
    {
        if (value >= 0)
        {
            WriteVarUInt32(output, (uint)value);
        }
        else
        {
            WriteVarUInt64(output, unchecked((ulong)value));
        }
    }

    public static void WriteUInt32Value(IBufferWriter<byte> output, uint value) => WriteVarUInt32(output, value);

    public static void WriteInt64Value(IBufferWriter<byte> output, long value) => WriteVarUInt64(output, unchecked((ulong)value));

    public static void WriteUInt64Value(IBufferWriter<byte> output, ulong value) => WriteVarUInt64(output, value);

    public static void WriteFixed32Value(IBufferWriter<byte> output, uint value)
    {
        var span = output.GetSpan(4);
        BinaryPrimitives.WriteUInt32LittleEndian(span, value);
        output.Advance(4);
    }

    public static void WriteFixed64Value(IBufferWriter<byte> output, ulong value)
    {
        var span = output.GetSpan(8);
        BinaryPrimitives.WriteUInt64LittleEndian(span, value);
        output.Advance(8);
    }

    public static int ReadInt32Value(ref SequenceReader<byte> reader) => unchecked((int)ReadVarUInt64(ref reader));

    public static uint ReadUInt32Value(ref SequenceReader<byte> reader) => ReadVarUInt32(ref reader);

    public static long ReadInt64Value(ref SequenceReader<byte> reader) => unchecked((long)ReadVarUInt64(ref reader));

    public static ulong ReadUInt64Value(ref SequenceReader<byte> reader) => ReadVarUInt64(ref reader);

    public static uint ReadFixed32Value(ref SequenceReader<byte> reader)
    {
        Span<byte> bytes = stackalloc byte[4];
        if (!reader.TryCopyTo(bytes))
        {
            throw new InvalidOperationException("Malformed protobuf value payload: insufficient data for fixed32 value.");
        }

        reader.Advance(4);
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    public static ulong ReadFixed64Value(ref SequenceReader<byte> reader)
    {
        Span<byte> bytes = stackalloc byte[8];
        if (!reader.TryCopyTo(bytes))
        {
            throw new InvalidOperationException("Malformed protobuf value payload: insufficient data for fixed64 value.");
        }

        reader.Advance(8);
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    public static void SkipField(ref SequenceReader<byte> reader, uint tag)
    {
        switch (tag & 0b111)
        {
            case WireTypeVarint:
                _ = ReadVarUInt64(ref reader);
                break;
            case WireTypeFixed64:
                EnsureRemaining(ref reader, 8, "fixed64 field");
                reader.Advance(8);
                break;
            case WireTypeLengthDelimited:
                var length = ReadVarUInt32(ref reader);
                EnsureRemaining(ref reader, length, "length-delimited field");
                reader.Advance(length);
                break;
            case WireTypeFixed32:
                EnsureRemaining(ref reader, 4, "fixed32 field");
                reader.Advance(4);
                break;
            default:
                throw new NotSupportedException($"Unsupported protobuf wire type: {tag & 0b111}");
        }
    }

    public static void WriteRaw(IBufferWriter<byte> output, ReadOnlySpan<byte> value)
    {
        var span = output.GetSpan(value.Length);
        value.CopyTo(span);
        output.Advance(value.Length);
    }

    private static void WriteRaw(IBufferWriter<byte> output, ReadOnlySequence<byte> value)
    {
        foreach (var segment in value)
        {
            WriteRaw(output, segment.Span);
        }
    }

    private static void EnsureRemaining(ref SequenceReader<byte> reader, uint length, string fieldKind)
    {
        if (reader.Remaining < length)
        {
            throw new InvalidOperationException($"Malformed protobuf log entry: insufficient data for {fieldKind}.");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteVarUInt32(IBufferWriter<byte> output, uint value)
    {
        var span = output.GetSpan(5);
        var count = 0;
        do
        {
            var b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0)
            {
                b |= 0x80;
            }

            span[count++] = b;
        }
        while (value != 0);

        output.Advance(count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteVarUInt64(IBufferWriter<byte> output, ulong value)
    {
        var span = output.GetSpan(10);
        var count = 0;
        do
        {
            var b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0)
            {
                b |= 0x80;
            }

            span[count++] = b;
        }
        while (value != 0);

        output.Advance(count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ReadVarUInt32(ref SequenceReader<byte> reader)
    {
        const int maxBytes = 5;
        uint result = 0;
        var shift = 0;
        byte b;
        var count = 0;
        do
        {
            if (!reader.TryRead(out b))
            {
                throw new InvalidOperationException("Insufficient data while reading a variable-length integer.");
            }

            result |= (uint)(b & 0x7F) << shift;
            shift += 7;

            if (++count > maxBytes)
            {
                throw new InvalidOperationException("Malformed variable-length integer: too many bytes.");
            }
        }
        while ((b & 0x80) != 0);

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ReadVarUInt64(ref SequenceReader<byte> reader)
    {
        const int maxBytes = 10;
        ulong result = 0;
        var shift = 0;
        byte b;
        var count = 0;
        do
        {
            if (!reader.TryRead(out b))
            {
                throw new InvalidOperationException("Insufficient data while reading a variable-length integer.");
            }

            result |= (ulong)(b & 0x7F) << shift;
            shift += 7;

            if (++count > maxBytes)
            {
                throw new InvalidOperationException("Malformed variable-length integer: too many bytes.");
            }
        }
        while ((b & 0x80) != 0);

        return result;
    }
}
