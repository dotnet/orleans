using System.Buffers;
using System.Runtime.CompilerServices;

namespace Orleans.Journaling;

/// <summary>
/// Provides methods for reading and writing variable-length encoded unsigned integers
/// using LEB128 (Little-Endian Base 128) encoding, independent of Orleans.Serialization.
/// </summary>
internal static class VarIntHelper
{
    /// <summary>
    /// Writes a <see cref="uint"/> value in LEB128 encoding to the provided buffer writer.
    /// </summary>
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

    /// <summary>
    /// Writes a <see cref="ulong"/> value in LEB128 encoding to the provided buffer writer.
    /// </summary>
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

    /// <summary>
    /// Reads a LEB128-encoded <see cref="uint"/> from the provided sequence reader.
    /// </summary>
    /// <remarks>A LEB128-encoded <see cref="uint"/> occupies at most 5 bytes.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ReadVarUInt32(ref SequenceReader<byte> reader)
    {
        const int maxBytes = 5; // ceil(32 / 7)
        uint result = 0;
        var shift = 0;
        byte b;
        var count = 0;
        do
        {
            if (!reader.TryRead(out b))
            {
                ThrowInsufficientData();
            }

            result |= (uint)(b & 0x7F) << shift;
            shift += 7;

            if (++count > maxBytes)
            {
                ThrowOverflow();
            }
        }
        while ((b & 0x80) != 0);

        return result;
    }

    /// <summary>
    /// Reads a LEB128-encoded <see cref="ulong"/> from the provided sequence reader.
    /// </summary>
    /// <remarks>A LEB128-encoded <see cref="ulong"/> occupies at most 10 bytes.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ReadVarUInt64(ref SequenceReader<byte> reader)
    {
        const int maxBytes = 10; // ceil(64 / 7)
        ulong result = 0;
        var shift = 0;
        byte b;
        var count = 0;
        do
        {
            if (!reader.TryRead(out b))
            {
                ThrowInsufficientData();
            }

            result |= (ulong)(b & 0x7F) << shift;
            shift += 7;

            if (++count > maxBytes)
            {
                ThrowOverflow();
            }
        }
        while ((b & 0x80) != 0);

        return result;
    }

    private static void ThrowInsufficientData() =>
        throw new InvalidOperationException("Insufficient data while reading a variable-length integer.");

    private static void ThrowOverflow() =>
        throw new InvalidOperationException("Malformed variable-length integer: too many bytes.");
}
