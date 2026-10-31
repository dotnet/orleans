using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using Garnet.common;

namespace Orleans.Journaling;

/// <summary>
/// Wire protocol shared by the journal transaction procedures and their client.
/// </summary>
/// <remarks>
/// Every journal operation is one Garnet transaction procedure. Garnet logs a procedure invocation to the AOF as its
/// input and replays it by running the procedure again during recovery, so procedures are deterministic functions of
/// their input and the store: callers generate ETags. Procedure ids are assigned in registration order and are
/// persisted in the AOF, so <see cref="Names"/> must only ever be appended to.
/// </remarks>
internal static class GarnetJournalProtocol
{
    public const string Read = "OJREAD";
    public const string GetMetadata = "OJMETA";
    public const string Create = "OJCREATE";
    public const string Append = "OJAPPEND";
    public const string Replace = "OJREPLACE";
    public const string Delete = "OJDELETE";
    public const string UpdateMetadata = "OJSETMETA";
    public const string Bucket = "OJBUCKET";
    public const string Index = "OJINDEX";
    public const string PullRead = "OJPULLREAD";
    public const string Install = "OJINSTALL";
    public const string Done = "OJDONE";
    public const string Link = "OJLINK";
    public const string Purge = "OJPURGE";
    public const string DropMarker = "OJDROPMARK";
    public const string BucketInfo = "OJBUCKETINFO";

    /// <summary>The load report, a non-transactional procedure registered after <see cref="Names"/>.</summary>
    public const string Load = "OJLOAD";

    /// <summary>Procedure names in registration order.</summary>
    public static readonly string[] Names = [Read, GetMetadata, Create, Append, Replace, Delete, UpdateMetadata, Bucket, Index, PullRead, Install, Done, Link, Purge, DropMarker, BucketInfo];

    public const int MissingStatus = 0;
    public const int SuccessStatus = 1;
    public const int AppearedStatus = 2;
    public const int ConflictStatus = -1;
    public const int InvalidMetadataStatus = -3;
    public const int UnsupportedSchemaVersionStatus = -4;

    /// <summary>The bucket is not open in this partition. Followed by the bucket's state and version.</summary>
    public const int WrongPartitionStatus = -10;

    /// <summary>The bucket's migration is incomplete and the journal must be pulled from the bucket's source first. Followed by the source and the bucket's version.</summary>
    public const int NeedPullStatus = -11;

    public const string CurrentSchemaVersion = "1";

    public static ReadOnlySpan<byte> ETagField => "$etag"u8;
    public static ReadOnlySpan<byte> ContentETagField => "$content-etag"u8;
    public static ReadOnlySpan<byte> AppendLengthField => "$append-length"u8;
    public static ReadOnlySpan<byte> FormatField => "$format"u8;
    public static ReadOnlySpan<byte> SchemaVersionField => "$schema-version"u8;
    public static ReadOnlySpan<byte> DeletedField => "$deleted"u8;
    public static ReadOnlySpan<byte> RecentETagsField => "$recent-etags"u8;
    public static ReadOnlySpan<byte> DeletedAtField => "$deleted-at"u8;

    /// <summary>The tombstone's field holding the bucket's collection generation when the journal was deleted (D25).</summary>
    public static ReadOnlySpan<byte> DeletedGenerationField => "$deleted-gen"u8;
    public static ReadOnlySpan<byte> AbsentField => "$absent"u8;

    /// <summary>Record kinds returned by <see cref="PullRead"/> and accepted by <see cref="Install"/>.</summary>
    public const string LiveKind = "live";
    public const string TombstoneKind = "tomb";
    public const string AbsentKind = "absent";
    public const string NoneKind = "none";
    public static ReadOnlySpan<byte> LiveKindValue => "live"u8;
    public static ReadOnlySpan<byte> TombstoneKindValue => "tomb"u8;
    public static ReadOnlySpan<byte> AbsentKindValue => "absent"u8;
    public static ReadOnlySpan<byte> NoneKindValue => "none"u8;

    /// <summary>
    /// The number of mutation ETags a journal retains, so that a retried mutation is recognized as committed while
    /// fewer than this many later mutations have committed.
    /// </summary>
    public const int RecentETagCount = 8;
    public static ReadOnlySpan<byte> SchemaVersionValue => "1"u8;
    public static ReadOnlySpan<byte> TrueValue => "1"u8;
    public static ReadOnlySpan<byte> FalseValue => "0"u8;
    public static ReadOnlySpan<byte> ZeroValue => "0"u8;

    public const string ETagMetadataKey = "$etag";
    public const string ContentETagMetadataKey = "$content-etag";
    public const string AppendLengthMetadataKey = "$append-length";
    public const string FormatMetadataKey = "$format";
    public const string SchemaVersionMetadataKey = "$schema-version";
    public const string DeletedMetadataKey = "$deleted";

    public static ReadOnlySpan<byte> BucketStateField => "state"u8;
    public static ReadOnlySpan<byte> BucketVersionField => "version"u8;
    public static ReadOnlySpan<byte> BucketSourceField => "source"u8;
    public static ReadOnlySpan<byte> BucketDoneField => "done"u8;
    public static ReadOnlySpan<byte> BucketReclaimedField => "reclaimed"u8;

    /// <summary>
    /// The bucket's collection generation (D25): one more than the generation stamped on the latest-stamped tombstone
    /// which the bucket's history dropped, or 0 (the field is absent) if it dropped none.
    /// </summary>
    public static ReadOnlySpan<byte> BucketCollectedField => "collected"u8;

    /// <summary>The key filters a sealed copy wrote for its destination (see <see cref="GarnetJournalKeyFilter"/>).</summary>
    public static ReadOnlySpan<byte> BucketFilterField => "filter"u8;

    /// <summary>The key filters of the chain an open bucket's migration reads from, copied from its source when it opened.</summary>
    public static ReadOnlySpan<byte> BucketChainField => "chain"u8;
    public static ReadOnlySpan<byte> OpenState => "open"u8;
    public static ReadOnlySpan<byte> SealedState => "sealed"u8;
    public static ReadOnlySpan<byte> AbsentState => "absent"u8;
    public static ReadOnlySpan<byte> OpenOperation => "open"u8;
    public static ReadOnlySpan<byte> SealOperation => "seal"u8;
    public static ReadOnlySpan<byte> ReclaimOperation => "reclaim"u8;

    public const string BucketOpenOperation = "open";
    public const string BucketSealOperation = "seal";
    public const string BucketReclaimOperation = "reclaim";
    public const string OpenStateName = "open";
    public const string SealedStateName = "sealed";
    public const string AbsentStateName = "absent";
    public const string NoSource = "-";

    public static bool TryParseNonNegativeCanonical(ReadOnlySpan<byte> value, out long result)
    {
        result = 0;
        if (value.IsEmpty || value.Length > 1 && value[0] == (byte)'0')
        {
            return false;
        }

        foreach (var digit in value)
        {
            if (digit is < (byte)'0' or > (byte)'9')
            {
                return false;
            }
        }

        return Utf8Parser.TryParse(value, out result, out var consumed) && consumed == value.Length;
    }

    public static byte[] FormatInt64(long value) => System.Text.Encoding.ASCII.GetBytes(value.ToString(CultureInfo.InvariantCulture));
}

/// <summary>
/// Writes a RESP2 array of bulk strings.
/// </summary>
internal sealed class GarnetRespArrayWriter
{
    private readonly ArrayBufferWriter<byte> _body = new(256);
    private int _count;

    public int Count => _count;

    public void WriteBulk(ReadOnlySpan<byte> value)
    {
        WriteLengthPrefix((byte)'$', value.Length);
        value.CopyTo(_body.GetSpan(value.Length));
        _body.Advance(value.Length);
        WriteNewLine();
        _count++;
    }

    public void WriteBulk(long value)
    {
        Span<byte> digits = stackalloc byte[20];
        Utf8Formatter.TryFormat(value, digits, out var length);
        WriteBulk(digits[..length]);
    }

    /// <summary>Appends <paramref name="count"/> already-encoded bulk strings.</summary>
    public void WriteEncodedBulkStrings(ReadOnlySpan<byte> encoded, int count)
    {
        encoded.CopyTo(_body.GetSpan(encoded.Length));
        _body.Advance(encoded.Length);
        _count += count;
    }

    public void CopyTo(ref MemoryResult<byte> output)
    {
        Span<byte> header = stackalloc byte[16];
        header[0] = (byte)'*';
        Utf8Formatter.TryFormat(_count, header[1..], out var digits);
        header[1 + digits] = (byte)'\r';
        header[2 + digits] = (byte)'\n';
        var headerLength = digits + 3;
        var total = headerLength + _body.WrittenCount;

        output.MemoryOwner?.Dispose();
        output.MemoryOwner = MemoryPool<byte>.Shared.Rent(total);
        output.Length = total;
        var destination = output.MemoryOwner.Memory.Span;
        header[..headerLength].CopyTo(destination);
        _body.WrittenSpan.CopyTo(destination[headerLength..]);
    }

    private void WriteLengthPrefix(byte marker, int length)
    {
        var span = _body.GetSpan(16);
        span[0] = marker;
        Utf8Formatter.TryFormat(length, span[1..], out var digits);
        span[1 + digits] = (byte)'\r';
        span[2 + digits] = (byte)'\n';
        _body.Advance(digits + 3);
    }

    private void WriteNewLine()
    {
        var span = _body.GetSpan(2);
        span[0] = (byte)'\r';
        span[1] = (byte)'\n';
        _body.Advance(2);
    }
}
