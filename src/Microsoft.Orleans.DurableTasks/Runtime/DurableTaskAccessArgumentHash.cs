using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;
using Orleans.Journaling;

namespace Orleans.DurableTasks.Runtime;

/// <summary>
/// Computes the hash of an access's arguments which a record of the access stores, so a replay whose access passes other
/// arguments, such as another key, diverges instead of returning the record's outcome.
/// </summary>
/// <remarks>
/// <para>
/// The hash is FNV-1a over a stable encoding of the argument, the same in every process and runtime version: strings
/// by their UTF-16 code units, Booleans, characters, integers, floating-point numbers, decimals and enums by their
/// values, <see cref="Guid"/>, <see cref="DateTime"/>, <see cref="DateTimeOffset"/> and <see cref="TimeSpan"/> by their
/// binary representations, tuples element by element, and other <see cref="ISpanFormattable"/> values, such as message
/// identifiers, by their invariant text. Other values contribute only a marker of their presence, because their
/// encodings, such as a hash-ordered collection's, can differ between processes.
/// </para>
/// <para>
/// An access whose result depends only on the state passes <see cref="JournaledStateAccessNoArgument"/>, and its record
/// stores no hash.
/// </para>
/// </remarks>
internal static class DurableTaskAccessArgumentHash
{
    private const uint OffsetBasis = 2166136261;
    private const uint Prime = 16777619;

    /// <summary>Computes the hash of <paramref name="argument"/>.</summary>
    /// <returns><see langword="false"/> when the access has no argument.</returns>
    public static bool TryCompute<TArgument>(TArgument argument, out int hash)
    {
        if (typeof(TArgument) == typeof(JournaledStateAccessNoArgument))
        {
            hash = 0;
            return false;
        }

        hash = unchecked((int)Add(OffsetBasis, argument));
        return true;
    }

    private static uint Add(uint hash, object? value)
    {
        switch (value)
        {
            case null:
                return AddTag(hash, 0);
            case string text:
                hash = AddTag(hash, 1);
                foreach (var character in text)
                {
                    hash = AddUInt16(hash, character);
                }

                return AddUInt64(hash, (ulong)text.Length);
            case bool boolean:
                return AddUInt64(AddTag(hash, 2), boolean ? 1UL : 0UL);
            case char character:
                return AddUInt64(AddTag(hash, 3), character);
            case sbyte or short or int or long:
                return AddUInt64(AddTag(hash, 4), unchecked((ulong)Convert.ToInt64(value, CultureInfo.InvariantCulture)));
            case byte or ushort or uint or ulong:
                return AddUInt64(AddTag(hash, 4), Convert.ToUInt64(value, CultureInfo.InvariantCulture));
            case float single:
                return AddUInt64(AddTag(hash, 5), BitConverter.SingleToUInt32Bits(single));
            case double number:
                return AddUInt64(AddTag(hash, 6), BitConverter.DoubleToUInt64Bits(number));
            case decimal number:
                hash = AddTag(hash, 7);
                foreach (var part in decimal.GetBits(number))
                {
                    hash = AddUInt64(hash, unchecked((uint)part));
                }

                return hash;
            case Enum enumeration:
                var underlying = Convert.GetTypeCode(enumeration) is TypeCode.UInt64
                    ? Convert.ToUInt64(enumeration, CultureInfo.InvariantCulture)
                    : unchecked((ulong)Convert.ToInt64(enumeration, CultureInfo.InvariantCulture));
                return AddUInt64(AddTag(hash, 8), underlying);
            case Guid guid:
                Span<byte> bytes = stackalloc byte[16];
                guid.TryWriteBytes(bytes);
                return AddUInt64(AddUInt64(AddTag(hash, 9), BinaryPrimitives.ReadUInt64LittleEndian(bytes)), BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]));
            case DateTime time:
                return AddUInt64(AddTag(hash, 10), unchecked((ulong)time.ToBinary()));
            case DateTimeOffset time:
                return AddUInt64(AddUInt64(AddTag(hash, 11), unchecked((ulong)time.UtcTicks)), unchecked((ulong)time.Offset.Ticks));
            case TimeSpan span:
                return AddUInt64(AddTag(hash, 12), unchecked((ulong)span.Ticks));
            case ITuple tuple:
                hash = AddUInt64(AddTag(hash, 13), (ulong)tuple.Length);
                for (var i = 0; i < tuple.Length; i++)
                {
                    hash = Add(hash, tuple[i]);
                }

                return hash;
            case ISpanFormattable formattable:
                return AddFormatted(AddTag(hash, 14), formattable);
            default:
                return AddTag(hash, 15);
        }
    }

    private static uint AddFormatted(uint hash, ISpanFormattable value)
    {
        Span<char> buffer = stackalloc char[128];
        if (value.TryFormat(buffer, out var written, default, CultureInfo.InvariantCulture))
        {
            foreach (var character in buffer[..written])
            {
                hash = AddUInt16(hash, character);
            }

            return AddUInt64(hash, (ulong)written);
        }

        return Add(hash, value.ToString(null, CultureInfo.InvariantCulture));
    }

    private static uint AddTag(uint hash, byte tag) => unchecked((hash ^ tag) * Prime);

    private static uint AddUInt16(uint hash, ushort value) => AddTag(AddTag(hash, (byte)value), (byte)(value >> 8));

    private static uint AddUInt64(uint hash, ulong value)
    {
        for (var i = 0; i < 8; i++)
        {
            hash = AddTag(hash, (byte)(value >> (8 * i)));
        }

        return hash;
    }
}
