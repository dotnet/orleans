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
        const byte maxFinalBytePayload = 0x0F;
        uint result = 0;
        for (var index = 0; index < maxBytes; index++)
        {
            if (!reader.TryRead(out var b))
            {
                ThrowInsufficientData();
            }

            var payload = b & 0x7F;
            if (index == maxBytes - 1 && (payload > maxFinalBytePayload || (b & 0x80) != 0))
            {
                ThrowOverflow();
            }

            result |= (uint)payload << (index * 7);
            if ((b & 0x80) == 0)
            {
                return result;
            }
        }

        ThrowOverflow();
        return default;
    }

    /// <summary>
    /// Reads a LEB128-encoded <see cref="ulong"/> from the provided sequence reader.
    /// </summary>
    /// <remarks>A LEB128-encoded <see cref="ulong"/> occupies at most 10 bytes.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ReadVarUInt64(ref SequenceReader<byte> reader)
    {
        const int maxBytes = 10; // ceil(64 / 7)
        const byte maxFinalBytePayload = 0x01;
        ulong result = 0;
        for (var index = 0; index < maxBytes; index++)
        {
            if (!reader.TryRead(out var b))
            {
                ThrowInsufficientData();
            }

            var payload = b & 0x7F;
            if (index == maxBytes - 1 && (payload > maxFinalBytePayload || (b & 0x80) != 0))
            {
                ThrowOverflow();
            }

            result |= (ulong)payload << (index * 7);
            if ((b & 0x80) == 0)
            {
                return result;
            }
        }

        ThrowOverflow();
        return default;
    }

    private static void ThrowInsufficientData() =>
        throw new InvalidOperationException("Insufficient data while reading a variable-length integer.");

    private static void ThrowOverflow() =>
        throw new InvalidOperationException("Malformed variable-length integer: value exceeds the supported range.");
}
