using System.Buffers.Binary;
using System.IO.Hashing;

namespace Orleans.Journaling;

/// <summary>
/// Bloom filters over the encoded ids of the journals a sealed copy of a bucket holds a record of.
/// </summary>
/// <remarks>
/// <para>
/// A partition writes a filter when it seals a bucket, in the seal's transaction, over the bucket's index and marker
/// set: live journals, tombstones, and absent markers. While its own migration of the bucket is not done, it appends
/// the filters of its own chain, which it copied when it opened the bucket, so the filters describe every journal with a
/// record on the chain. A destination copies them when it opens the bucket, and answers for a journal it holds no record
/// of without a pull when no filter reports the journal: no partition on the chain holds a record of it, so it is absent
/// with no absence ETag, exactly what a pull would install.
/// </para>
/// <para>
/// Filters report false positives, which cost a pull, never false negatives. Tombstones are included so that the
/// destination pulls a deleted journal's tombstone, whose ETags identify the deletion (D3, D31).
/// </para>
/// <para>
/// A chain's filters are encoded as a sequence of filters, each a 4-byte little-endian length followed by the filter:
/// one byte holding the number of hash functions, then the bit array. Filters are pure functions of the ids they hold,
/// so a seal replayed from the AOF writes the same filter.
/// </para>
/// </remarks>
internal static class GarnetJournalKeyFilter
{
    /// <summary>Bits per id: about a 1% false-positive rate with <see cref="HashCount"/> hash functions.</summary>
    private const int BitsPerKey = 10;
    private const int HashCount = 7;

    /// <summary>The minimum filter size in bytes, which keeps false positives negligible in small buckets.</summary>
    private const int MinimumBytes = 64;

    /// <summary>Gets the encoded size of a filter over <paramref name="count"/> ids, including its length prefix.</summary>
    public static int GetEncodedLength(int count) => sizeof(int) + 1 + GetBitBytes(count);

    /// <summary>
    /// Writes the length prefix and header of a filter over <paramref name="count"/> ids to <paramref name="destination"/>,
    /// which must hold <see cref="GetEncodedLength"/> bytes, and returns its cleared bit array for <see cref="Add"/>.
    /// </summary>
    public static Span<byte> Begin(Span<byte> destination, int count)
    {
        var bytes = GetBitBytes(count);
        BinaryPrimitives.WriteInt32LittleEndian(destination, 1 + bytes);
        destination[sizeof(int)] = HashCount;
        var bits = destination.Slice(sizeof(int) + 1, bytes);
        bits.Clear();
        return bits;
    }

    /// <summary>Adds an id to a filter's bit array.</summary>
    public static void Add(Span<byte> bits, ReadOnlySpan<byte> id)
    {
        var bitCount = (ulong)bits.Length * 8;
        var (h1, h2) = Hash(id);
        for (var i = 0UL; i < HashCount; i++)
        {
            var bit = (h1 + i * h2) % bitCount;
            bits[(int)(bit >> 3)] |= (byte)(1 << (int)(bit & 7));
        }
    }

    /// <summary>Creates an encoded filter, with its length prefix, over <paramref name="ids"/>.</summary>
    public static byte[] Create(IReadOnlyCollection<byte[]> ids)
    {
        var result = new byte[GetEncodedLength(ids.Count)];
        var bits = Begin(result, ids.Count);
        foreach (var id in ids)
        {
            Add(bits, id);
        }

        return result;
    }

    /// <summary>
    /// Gets a value indicating whether no filter in <paramref name="filters"/> reports <paramref name="id"/>: no partition
    /// on the chain they describe holds a record of the journal.
    /// </summary>
    /// <returns><see langword="false"/> if a filter may hold the id, or if <paramref name="filters"/> is empty or malformed.</returns>
    public static bool ExcludesAll(ReadOnlySpan<byte> filters, ReadOnlySpan<byte> id)
    {
        if (filters.IsEmpty)
        {
            return false;
        }

        var (h1, h2) = Hash(id);
        while (!filters.IsEmpty)
        {
            if (filters.Length < sizeof(int) + 2)
            {
                return false;
            }

            var length = BinaryPrimitives.ReadInt32LittleEndian(filters);
            if (length < 2 || length > filters.Length - sizeof(int))
            {
                return false;
            }

            var filter = filters.Slice(sizeof(int), length);
            if (MayContain(filter, h1, h2))
            {
                return false;
            }

            filters = filters[(sizeof(int) + length)..];
        }

        return true;
    }

    /// <summary>Gets the number of filters in an encoded sequence.</summary>
    public static int Count(ReadOnlySpan<byte> filters)
    {
        var count = 0;
        while (filters.Length >= sizeof(int))
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(filters);
            if (length < 0 || length > filters.Length - sizeof(int))
            {
                break;
            }

            filters = filters[(sizeof(int) + length)..];
            count++;
        }

        return count;
    }

    private static bool MayContain(ReadOnlySpan<byte> filter, ulong h1, ulong h2)
    {
        var hashCount = filter[0];
        var bits = filter[1..];
        var bitCount = (ulong)bits.Length * 8;
        for (var i = 0UL; i < hashCount; i++)
        {
            var bit = (h1 + i * h2) % bitCount;
            if ((bits[(int)(bit >> 3)] & (1 << (int)(bit & 7))) == 0)
            {
                return false;
            }
        }

        return true;
    }

    private static int GetBitBytes(int count) => Math.Max(MinimumBytes, (int)Math.Min(int.MaxValue / 2, ((long)count * BitsPerKey + 7) / 8));

    // Double hashing (Kirsch and Mitzenmacher) from one 64-bit hash, seeded so it is independent of the bucket hash.
    private static (ulong H1, ulong H2) Hash(ReadOnlySpan<byte> id)
    {
        var hash = XxHash64.HashToUInt64(id, seed: 0x6F6A6B66);
        return (hash & 0xFFFFFFFF, (hash >> 32) | 1);
    }
}
