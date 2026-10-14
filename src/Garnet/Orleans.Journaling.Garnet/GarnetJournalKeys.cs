using System.Buffers;
using System.Globalization;
using System.IO.Hashing;
using System.Text;

namespace Orleans.Journaling;

/// <summary>
/// Maps journal ids to buckets and partition keys.
/// </summary>
/// <remarks>
/// Journal ids are encoded as generalized UTF-8 (WTF-8): well-formed text encodes as UTF-8, and each unpaired
/// UTF-16 surrogate encodes as its three-byte code point sequence. The encoding is injective, so every
/// <see cref="JournalId"/> round-trips through the bucket index used by the catalog.
/// </remarks>
internal static class GarnetJournalKeys
{
    private static ReadOnlySpan<byte> BucketPrefix => "oj:b:"u8;
    private static ReadOnlySpan<byte> MetadataPrefix => "oj:m:"u8;
    private static ReadOnlySpan<byte> DataPrefix => "oj:j:"u8;
    private static ReadOnlySpan<byte> IndexPrefix => "oj:i:"u8;

    /// <summary>The key of the partition descriptor hash.</summary>
    public static readonly byte[] PartitionKey = "oj:partition"u8.ToArray();

    /// <summary>The partition descriptor field holding the identity of the range map the partition belongs to.</summary>
    public const string MapIdField = "map-id";

    /// <summary>The partition descriptor field holding the partition's id.</summary>
    public const string PartitionIdField = "partition-id";

    /// <summary>The partition descriptor field holding the bucket count.</summary>
    public const string BucketCountField = "bucket-count";

    /// <summary>The partition descriptor field holding the storage schema version.</summary>
    public const string SchemaVersionField = "schema-version";

    public static int GetBucket(ReadOnlySpan<byte> encodedJournalId, int bucketCount)
    {
        // Bucket counts are powers of two, so masking is equivalent to the modulus.
        return (int)(XxHash64.HashToUInt64(encodedJournalId) & (ulong)(bucketCount - 1));
    }

    /// <summary>
    /// Gets the key of a bucket's state in a partition of the map <paramref name="mapId"/>.
    /// </summary>
    /// <remarks>
    /// The key carries the map's identity, so a partition of another map, reached through a stale endpoint, holds no
    /// such bucket and rejects the operation.
    /// </remarks>
    public static byte[] GetBucketKey(string mapId, int bucket)
    {
        var prefix = new byte[BucketPrefix.Length + Encoding.ASCII.GetByteCount(mapId) + 1];
        BucketPrefix.CopyTo(prefix);
        Encoding.ASCII.GetBytes(mapId, prefix.AsSpan(BucketPrefix.Length));
        prefix[^1] = (byte)':';
        return Concat(prefix, bucket, separator: false, []);
    }

    public static byte[] GetIndexKey(int bucket) => Concat(IndexPrefix, bucket, separator: false, []);

    public static byte[] GetMetadataKey(int bucket, ReadOnlySpan<byte> encodedJournalId) => Concat(MetadataPrefix, bucket, separator: true, encodedJournalId);

    public static byte[] GetDataKey(int bucket, ReadOnlySpan<byte> encodedJournalId) => Concat(DataPrefix, bucket, separator: true, encodedJournalId);

    private static byte[] Concat(ReadOnlySpan<byte> prefix, int bucket, bool separator, ReadOnlySpan<byte> suffix)
    {
        Span<byte> digits = stackalloc byte[11];
        bucket.TryFormat(digits, out var digitCount, provider: CultureInfo.InvariantCulture);
        var result = new byte[prefix.Length + digitCount + (separator ? 1 : 0) + suffix.Length];
        var destination = result.AsSpan();
        prefix.CopyTo(destination);
        destination = destination[prefix.Length..];
        digits[..digitCount].CopyTo(destination);
        destination = destination[digitCount..];
        if (separator)
        {
            destination[0] = (byte)':';
            destination = destination[1..];
        }

        suffix.CopyTo(destination);
        return result;
    }

    public static byte[] EncodeJournalId(string value)
    {
        var writer = new ArrayBufferWriter<byte>(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            int codePoint = value[i];
            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                codePoint = char.ConvertToUtf32(value[i], value[i + 1]);
                i++;
            }

            var span = writer.GetSpan(4);
            int length;
            if (codePoint < 0x80)
            {
                span[0] = (byte)codePoint;
                length = 1;
            }
            else if (codePoint < 0x800)
            {
                span[0] = (byte)(0xC0 | (codePoint >> 6));
                span[1] = (byte)(0x80 | (codePoint & 0x3F));
                length = 2;
            }
            else if (codePoint < 0x10000)
            {
                span[0] = (byte)(0xE0 | (codePoint >> 12));
                span[1] = (byte)(0x80 | ((codePoint >> 6) & 0x3F));
                span[2] = (byte)(0x80 | (codePoint & 0x3F));
                length = 3;
            }
            else
            {
                span[0] = (byte)(0xF0 | (codePoint >> 18));
                span[1] = (byte)(0x80 | ((codePoint >> 12) & 0x3F));
                span[2] = (byte)(0x80 | ((codePoint >> 6) & 0x3F));
                span[3] = (byte)(0x80 | (codePoint & 0x3F));
                length = 4;
            }

            writer.Advance(length);
        }

        return writer.WrittenSpan.ToArray();
    }

    public static bool TryDecodeJournalId(ReadOnlySpan<byte> value, out JournalId journalId)
    {
        journalId = default;
        var builder = new StringBuilder(value.Length);
        var previousWasHighSurrogate = false;
        var index = 0;
        while (index < value.Length)
        {
            var first = value[index];
            int codePoint;
            int length;
            if (first < 0x80)
            {
                codePoint = first;
                length = 1;
            }
            else if ((first & 0xE0) == 0xC0)
            {
                length = 2;
                if (!TryReadContinuation(value, index, length, first & 0x1F, out codePoint) || codePoint < 0x80)
                {
                    return false;
                }
            }
            else if ((first & 0xF0) == 0xE0)
            {
                length = 3;
                if (!TryReadContinuation(value, index, length, first & 0x0F, out codePoint) || codePoint < 0x800)
                {
                    return false;
                }
            }
            else if ((first & 0xF8) == 0xF0)
            {
                length = 4;
                if (!TryReadContinuation(value, index, length, first & 0x07, out codePoint) || codePoint < 0x10000 || codePoint > 0x10FFFF)
                {
                    return false;
                }
            }
            else
            {
                return false;
            }

            var isHighSurrogate = codePoint is >= 0xD800 and <= 0xDBFF;
            var isLowSurrogate = codePoint is >= 0xDC00 and <= 0xDFFF;

            // A surrogate pair must be encoded as one four-byte sequence; this keeps the encoding canonical.
            if (isLowSurrogate && previousWasHighSurrogate)
            {
                return false;
            }

            if (codePoint >= 0x10000)
            {
                builder.Append(char.ConvertFromUtf32(codePoint));
            }
            else
            {
                builder.Append((char)codePoint);
            }

            previousWasHighSurrogate = isHighSurrogate;
            index += length;
        }

        var decoded = builder.ToString();
        if (string.IsNullOrWhiteSpace(decoded))
        {
            return false;
        }

        journalId = new JournalId(decoded);
        return true;
    }

    private static bool TryReadContinuation(ReadOnlySpan<byte> value, int index, int length, int initial, out int codePoint)
    {
        codePoint = initial;
        if (index + length > value.Length)
        {
            return false;
        }

        for (var i = 1; i < length; i++)
        {
            var next = value[index + i];
            if ((next & 0xC0) != 0x80)
            {
                return false;
            }

            codePoint = (codePoint << 6) | (next & 0x3F);
        }

        return true;
    }
}
