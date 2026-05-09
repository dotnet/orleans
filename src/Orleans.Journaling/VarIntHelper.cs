using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Orleans.Journaling;

/// <summary>
/// Provides methods for reading and writing variable-length encoded unsigned integers
/// using the Orleans.Serialization wire encoding.
/// </summary>
internal static class VarIntHelper
{
    /// <summary>
    /// Writes a <see cref="uint"/> value using the Orleans.Serialization variable-length integer encoding.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteVarUInt32(IBufferWriter<byte> output, uint value)
    {
        var byteCount = GetVarUInt32ByteCount(value);
        var encoded = (((ulong)value << 1) + 1) << (byteCount - 1);
        WriteLittleEndian(output, encoded, byteCount);
    }

    /// <summary>
    /// Writes a <see cref="ulong"/> value using the Orleans.Serialization variable-length integer encoding.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteVarUInt64(IBufferWriter<byte> output, ulong value)
    {
        var byteCount = GetVarUInt64ByteCount(value);
        var neededBytes = byteCount - 1;
        var lower = ((value << 1) + 1) << neededBytes;
        var span = output.GetSpan(byteCount);
        WriteLittleEndian(span, lower, Math.Min(byteCount, sizeof(ulong)));
        if (byteCount > sizeof(ulong))
        {
            var upper = (ushort)(value >> (63 - neededBytes));
            WriteLittleEndian(span[sizeof(ulong)..], upper, byteCount - sizeof(ulong));
        }

        output.Advance(byteCount);
    }

    /// <summary>
    /// Gets the number of bytes required to encode a <see cref="uint"/> value.
    /// </summary>
    public static int GetVarUInt32ByteCount(uint value) => GetVarUIntByteCount(value);

    /// <summary>
    /// Gets the number of bytes required to encode a <see cref="ulong"/> value.
    /// </summary>
    public static int GetVarUInt64ByteCount(ulong value) => GetVarUIntByteCount(value);

    private static int GetVarUIntByteCount(ulong value) => ((int)BitOperations.Log2(value) / 7) + 1;

    /// <summary>
    /// Attempts to read a variable-length encoded <see cref="uint"/> from the provided sequence reader.
    /// </summary>
    public static bool TryReadVarUInt32(ref SequenceReader<byte> reader, out uint value, out int bytesRead, out int minimumBytes)
    {
        var start = reader;
        value = 0;
        bytesRead = 0;
        minimumBytes = 1;

        if (!reader.TryRead(out var header))
        {
            return false;
        }

        var byteCount = BitOperations.TrailingZeroCount(0x0100U | header) + 1;
        if (byteCount > 5)
        {
            reader = start;
            ThrowOverflow();
        }

        var encoded = (ulong)header;
        if (!TryReadEncodedUInt64(ref reader, byteCount, bytesAlreadyRead: 1, ref encoded, out _))
        {
            reader = start;
            minimumBytes = byteCount;
            return false;
        }

        var result = encoded >> byteCount;
        if (result > uint.MaxValue)
        {
            reader = start;
            ThrowOverflow();
        }

        value = (uint)result;
        bytesRead = byteCount;
        minimumBytes = byteCount;
        return true;
    }

    /// <summary>
    /// Attempts to read a variable-length encoded <see cref="ulong"/> from the provided sequence reader.
    /// </summary>
    public static bool TryReadVarUInt64(ref SequenceReader<byte> reader, out ulong value, out int bytesRead, out int minimumBytes)
    {
        var start = reader;
        value = 0;
        bytesRead = 0;
        minimumBytes = 1;

        if (!reader.TryRead(out var header))
        {
            return false;
        }

        int byteCount;
        if (header != 0)
        {
            byteCount = BitOperations.TrailingZeroCount((uint)header) + 1;
        }
        else
        {
            if (!reader.TryRead(out var secondByte))
            {
                reader = start;
                minimumBytes = 2;
                return false;
            }

            var marker = (uint)secondByte << 8;
            byteCount = BitOperations.TrailingZeroCount(marker) + 1;
            if (byteCount is < 9 or > 10)
            {
                reader = start;
                ThrowOverflow();
            }

            var lower = (ulong)secondByte << 8;
            if (!TryReadEncodedUInt64(ref reader, byteCount, bytesAlreadyRead: 2, ref lower, out var upper))
            {
                reader = start;
                minimumBytes = byteCount;
                return false;
            }

            if (byteCount == 10 && (upper & ~0x03FF) != 0)
            {
                reader = start;
                ThrowOverflow();
            }

            value = DecodeUInt64(lower, byteCount, upper);
            bytesRead = byteCount;
            minimumBytes = byteCount;
            return true;
        }

        var encoded = (ulong)header;
        if (!TryReadEncodedUInt64(ref reader, byteCount, bytesAlreadyRead: 1, ref encoded, out _))
        {
            reader = start;
            minimumBytes = byteCount;
            return false;
        }

        value = encoded >> byteCount;
        bytesRead = byteCount;
        minimumBytes = byteCount;
        return true;
    }

    /// <summary>
    /// Reads an Orleans.Serialization-encoded <see cref="uint"/> from the provided sequence reader.
    /// </summary>
    public static uint ReadVarUInt32(ref SequenceReader<byte> reader)
    {
        if (TryReadVarUInt32(ref reader, out var value, out _, out _))
        {
            return value;
        }

        ThrowInsufficientData();
        return default;
    }

    /// <summary>
    /// Reads an Orleans.Serialization-encoded <see cref="ulong"/> from the provided sequence reader.
    /// </summary>
    public static ulong ReadVarUInt64(ref SequenceReader<byte> reader)
    {
        if (TryReadVarUInt64(ref reader, out var value, out _, out _))
        {
            return value;
        }

        ThrowInsufficientData();
        return default;
    }

    private static bool TryReadEncodedUInt64(ref SequenceReader<byte> reader, int byteCount, int bytesAlreadyRead, ref ulong lower, out ushort upper)
    {
        upper = 0;
        for (var index = bytesAlreadyRead; index < Math.Min(byteCount, sizeof(ulong)); index++)
        {
            if (!reader.TryRead(out var value))
            {
                return false;
            }

            lower |= (ulong)value << (index * 8);
        }

        if (byteCount <= sizeof(ulong))
        {
            return true;
        }

        for (var index = sizeof(ulong); index < byteCount; index++)
        {
            if (!reader.TryRead(out var value))
            {
                return false;
            }

            upper |= (ushort)(value << ((index - sizeof(ulong)) * 8));
        }

        return true;
    }

    private static void WriteLittleEndian(IBufferWriter<byte> output, ulong value, int byteCount)
    {
        var span = output.GetSpan(byteCount);
        WriteLittleEndian(span, value, byteCount);
        output.Advance(byteCount);
    }

    private static void WriteLittleEndian(Span<byte> span, ulong value, int byteCount)
    {
        for (var index = 0; index < byteCount; index++)
        {
            span[index] = (byte)(value >> (index * 8));
        }
    }

    private static ulong DecodeUInt64(ulong lower, int byteCount, ushort upper) =>
        (lower >> byteCount) | ((ulong)upper << (64 - byteCount));

    private static void ThrowInsufficientData() =>
        throw new InvalidOperationException("Insufficient data while reading a variable-length integer.");

    private static void ThrowOverflow() =>
        throw new InvalidOperationException("Malformed variable-length integer: value exceeds the supported range.");
}
