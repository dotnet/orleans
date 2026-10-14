using System.Buffers.Text;
using Garnet.common;
using Garnet.server;
using Tsavorite.core;

namespace Orleans.Journaling;

/// <summary>
/// Base class for journal transaction procedures.
/// </summary>
/// <remarks>
/// <para>
/// Journal procedures take their keys as the first arguments: the bucket state key, the journal metadata key, the
/// journal data key, the bucket index key, and the encoded journal id. Every procedure locks the bucket state key, so
/// it linearizes against bucket transitions (the in-band seal): a procedure which observes an open bucket commits before
/// any seal of that bucket, and a procedure ordered after a seal observes the seal and fails.
/// </para>
/// <para>
/// Garnet applies <c>Main</c>'s writes as they are issued and logs the invocation after <c>Main</c> returns, so
/// procedures validate everything before their first write and issue writes which cannot fail.
/// </para>
/// </remarks>
internal abstract class GarnetJournalProcedure : CustomTransactionProcedure
{
    private const int BucketKeyArg = 0;
    private const int MetadataKeyArg = 1;
    private const int DataKeyArg = 2;
    private const int IndexKeyArg = 3;
    protected const int FirstOperationArg = 5;

    // Locks are held only while Main runs in memory; AOF commit waits happen after the locks are released.
    public override TimeSpan KeyLockTimeout => TimeSpan.FromSeconds(10);

    protected abstract bool LocksData { get; }

    protected abstract bool IsWrite { get; }

    public override bool Prepare<TGarnetReadApi>(TGarnetReadApi api, ref CustomProcedureInput procInput)
    {
        var offset = 0;
        var bucketKey = GetNextArg(ref procInput, ref offset);
        var metadataKey = GetNextArg(ref procInput, ref offset);
        var dataKey = GetNextArg(ref procInput, ref offset);
        var indexKey = GetNextArg(ref procInput, ref offset);
        if (!bucketKey.IsValid || !metadataKey.IsValid || !dataKey.IsValid || !indexKey.IsValid)
        {
            return false;
        }

        var type = IsWrite ? LockType.Exclusive : LockType.Shared;
        AddKey(bucketKey, LockType.Shared, StoreType.Object);
        AddKey(metadataKey, type, StoreType.Object);
        if (LocksData)
        {
            AddKey(dataKey, type, StoreType.Object);
            if (IsWrite)
            {
                AddKey(indexKey, LockType.Exclusive, StoreType.Object);
            }
        }

        return true;
    }

    protected static PinnedSpanByte GetArg(ref CustomProcedureInput procInput, int index)
    {
        var offset = index;
        return GetNextArg(ref procInput, ref offset);
    }

    protected static PinnedSpanByte BucketKey(ref CustomProcedureInput procInput) => GetArg(ref procInput, BucketKeyArg);

    protected static PinnedSpanByte MetadataKey(ref CustomProcedureInput procInput) => GetArg(ref procInput, MetadataKeyArg);

    protected static PinnedSpanByte DataKey(ref CustomProcedureInput procInput) => GetArg(ref procInput, DataKeyArg);

    protected static PinnedSpanByte IndexKey(ref CustomProcedureInput procInput) => GetArg(ref procInput, IndexKeyArg);

    protected static PinnedSpanByte Member(ref CustomProcedureInput procInput) => GetArg(ref procInput, 4);

    /// <summary>
    /// Checks that the bucket is open in this partition and, for journals absent from it, that its migration is complete.
    /// </summary>
    /// <returns><see langword="true"/> if the operation may proceed; otherwise, the response has been written.</returns>
    protected static bool CheckBucket<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, in JournalRecord journal, ref MemoryResult<byte> output)
        where TGarnetApi : IGarnetReadApi
    {
        var bucket = BucketRecord.Load(api, BucketKey(ref procInput));
        if (!bucket.IsOpen)
        {
            var response = new GarnetRespArrayWriter();
            response.WriteBulk(GarnetJournalProtocol.WrongPartitionStatus);
            response.WriteBulk(bucket.Exists ? bucket.State : GarnetJournalProtocol.AbsentState);
            response.WriteBulk(bucket.Exists ? bucket.Version : GarnetJournalProtocol.ZeroValue);
            response.CopyTo(ref output);
            return false;
        }

        // Journals the partition holds, including deletion tombstones, are authoritative. Others may still be in the
        // bucket's source until migration completes.
        if (!journal.Exists && !bucket.IsDone)
        {
            var response = new GarnetRespArrayWriter();
            response.WriteBulk(GarnetJournalProtocol.NeedPullStatus);
            response.WriteBulk(bucket.Source);
            response.CopyTo(ref output);
            return false;
        }

        return true;
    }

    protected static void WriteStatus(ref MemoryResult<byte> output, int status)
    {
        var response = new GarnetRespArrayWriter();
        response.WriteBulk(status);
        response.CopyTo(ref output);
    }

    protected static void WriteStatus(ref MemoryResult<byte> output, int status, long value)
    {
        var response = new GarnetRespArrayWriter();
        response.WriteBulk(status);
        response.WriteBulk(value);
        response.CopyTo(ref output);
    }

    /// <summary>Writes the status followed by the journal's metadata fields and values.</summary>
    protected static void WriteMetadata<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output, int status)
        where TGarnetApi : IGarnetReadApi
    {
        var response = new GarnetRespArrayWriter();
        response.WriteBulk(status);
        if (api.HashGetAll(MetadataKey(ref procInput), out PinnedSpanByte[] values) == GarnetStatus.OK)
        {
            foreach (var value in values)
            {
                response.WriteBulk(value.ReadOnlySpan);
            }
        }

        response.CopyTo(ref output);
    }

    /// <summary>
    /// Validates the provider-owned metadata of an existing journal.
    /// </summary>
    /// <returns>The success status if the metadata is valid; otherwise, the failure status.</returns>
    protected static int Validate(in JournalRecord journal)
    {
        if (journal.ETag.IsEmpty
            || journal.ContentETag.IsEmpty
            || journal.Format.IsEmpty
            || !journal.HasValidAppendLength)
        {
            return GarnetJournalProtocol.InvalidMetadataStatus;
        }

        if (!journal.SchemaVersion.SequenceEqual(GarnetJournalProtocol.SchemaVersionValue))
        {
            return GarnetJournalProtocol.UnsupportedSchemaVersionStatus;
        }

        return GarnetJournalProtocol.SuccessStatus;
    }

    /// <summary>Removes a deletion tombstone and any stale data before the journal is created.</summary>
    protected static void ClearForCreate<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, in JournalRecord journal)
        where TGarnetApi : IGarnetApi
    {
        if (journal.IsTombstone)
        {
            api.DELETE(MetadataKey(ref procInput));
        }

        api.DELETE(DataKey(ref procInput));
    }

    /// <summary>
    /// Creates the journal's recent-ETag list with <paramref name="newETag"/> appended, retaining the most recent
    /// <see cref="GarnetJournalProtocol.RecentETagCount"/> entries.
    /// </summary>
    protected PinnedSpanByte AppendRecentETag(ReadOnlySpan<byte> recent, PinnedSpanByte newETag)
    {
        // Entries are newline-separated; ETags are generated by the client and contain no newlines.
        var keep = recent;
        var count = recent.IsEmpty ? 0 : recent.Count((byte)'\n') + 1;
        while (count >= GarnetJournalProtocol.RecentETagCount)
        {
            var next = keep.IndexOf((byte)'\n');
            keep = next < 0 ? default : keep[(next + 1)..];
            count--;
        }

        var value = new byte[keep.Length + (keep.IsEmpty ? 0 : 1) + newETag.Length];
        keep.CopyTo(value);
        if (!keep.IsEmpty)
        {
            value[keep.Length] = (byte)'\n';
        }

        newETag.ReadOnlySpan.CopyTo(value.AsSpan(value.Length - newETag.Length));
        return CreateArgSlice(value);
    }

    protected static PinnedSpanByte Pinned(ReadOnlySpan<byte> staticData) => PinnedSpanByte.FromPinnedSpan(staticData);

    protected static bool ArgEquals(PinnedSpanByte arg, ReadOnlySpan<byte> value) => arg.ReadOnlySpan.SequenceEqual(value);
}

/// <summary>The state of a bucket in a partition.</summary>
internal readonly ref struct BucketRecord
{
    public bool Exists { get; init; }
    public ReadOnlySpan<byte> State { get; init; }
    public ReadOnlySpan<byte> Version { get; init; }
    public ReadOnlySpan<byte> Source { get; init; }
    public bool IsDone { get; init; }
    public bool IsOpen => Exists && State.SequenceEqual(GarnetJournalProtocol.OpenState);

    public static BucketRecord Load<TGarnetApi>(TGarnetApi api, PinnedSpanByte key)
        where TGarnetApi : IGarnetReadApi
    {
        if (api.HashGetAll(key, out PinnedSpanByte[] values) != GarnetStatus.OK || values.Length == 0)
        {
            return default;
        }

        ReadOnlySpan<byte> state = default, version = default, source = default, done = default;
        for (var i = 0; i + 1 < values.Length; i += 2)
        {
            var field = values[i].ReadOnlySpan;
            var value = values[i + 1].ReadOnlySpan;
            if (field.SequenceEqual(GarnetJournalProtocol.BucketStateField)) state = value;
            else if (field.SequenceEqual(GarnetJournalProtocol.BucketVersionField)) version = value;
            else if (field.SequenceEqual(GarnetJournalProtocol.BucketSourceField)) source = value;
            else if (field.SequenceEqual(GarnetJournalProtocol.BucketDoneField)) done = value;
        }

        return new()
        {
            Exists = true,
            State = state,
            Version = version,
            Source = source,
            IsDone = done.SequenceEqual(GarnetJournalProtocol.TrueValue),
        };
    }
}

/// <summary>The provider-owned metadata of a journal in a partition.</summary>
internal readonly ref struct JournalRecord
{
    /// <summary>Gets a value indicating whether the partition holds the journal or its deletion tombstone.</summary>
    public bool Exists { get; init; }
    public bool IsTombstone { get; init; }
    public bool IsLive => Exists && !IsTombstone;
    public ReadOnlySpan<byte> ETag { get; init; }
    public ReadOnlySpan<byte> ContentETag { get; init; }
    public ReadOnlySpan<byte> Format { get; init; }
    public ReadOnlySpan<byte> SchemaVersion { get; init; }
    public long AppendLength { get; init; }
    public bool HasValidAppendLength { get; init; }

    /// <summary>Gets the newline-separated ETags of the journal's most recent mutations, including its deletion.</summary>
    public ReadOnlySpan<byte> RecentETags { get; init; }

    /// <summary>
    /// Gets a value indicating whether the mutation which writes <paramref name="eTag"/> has already committed.
    /// </summary>
    public bool HasCommitted(PinnedSpanByte eTag)
    {
        var remaining = RecentETags;
        while (!remaining.IsEmpty)
        {
            var end = remaining.IndexOf((byte)'\n');
            var entry = end < 0 ? remaining : remaining[..end];
            if (entry.SequenceEqual(eTag.ReadOnlySpan))
            {
                return true;
            }

            remaining = end < 0 ? default : remaining[(end + 1)..];
        }

        return false;
    }

    public static JournalRecord Load<TGarnetApi>(TGarnetApi api, PinnedSpanByte key)
        where TGarnetApi : IGarnetReadApi
    {
        if (api.HashGetAll(key, out PinnedSpanByte[] values) != GarnetStatus.OK || values.Length == 0)
        {
            return default;
        }

        ReadOnlySpan<byte> eTag = default, contentETag = default, format = default, schemaVersion = default, appendLength = default, recent = default;
        var deleted = false;
        for (var i = 0; i + 1 < values.Length; i += 2)
        {
            var field = values[i].ReadOnlySpan;
            var value = values[i + 1].ReadOnlySpan;
            if (field.SequenceEqual(GarnetJournalProtocol.ETagField)) eTag = value;
            else if (field.SequenceEqual(GarnetJournalProtocol.ContentETagField)) contentETag = value;
            else if (field.SequenceEqual(GarnetJournalProtocol.FormatField)) format = value;
            else if (field.SequenceEqual(GarnetJournalProtocol.SchemaVersionField)) schemaVersion = value;
            else if (field.SequenceEqual(GarnetJournalProtocol.AppendLengthField)) appendLength = value;
            else if (field.SequenceEqual(GarnetJournalProtocol.RecentETagsField)) recent = value;
            else if (field.SequenceEqual(GarnetJournalProtocol.DeletedField)) deleted = true;
        }

        var validLength = GarnetJournalProtocol.TryParseNonNegativeCanonical(appendLength, out var length);
        return new()
        {
            Exists = true,
            IsTombstone = deleted,
            ETag = eTag,
            ContentETag = contentETag,
            Format = format,
            SchemaVersion = schemaVersion,
            AppendLength = length,
            HasValidAppendLength = validLength,
            RecentETags = recent,
        };
    }
}

/// <summary>
/// Reads a journal's metadata and data. Arguments: keys.
/// Returns <c>[1, metadataValueCount, metadata..., segments...]</c> or <c>[0]</c>.
/// </summary>
internal sealed class GarnetJournalReadProcedure : GarnetJournalProcedure
{
    protected override bool LocksData => true;

    protected override bool IsWrite => false;

    public override void Main<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
    {
        var journal = JournalRecord.Load(api, MetadataKey(ref procInput));
        if (!CheckBucket(api, ref procInput, journal, ref output))
        {
            return;
        }

        if (!journal.IsLive)
        {
            WriteStatus(ref output, GarnetJournalProtocol.MissingStatus);
            return;
        }

        var response = new GarnetRespArrayWriter();
        response.WriteBulk(GarnetJournalProtocol.SuccessStatus);
        api.HashGetAll(MetadataKey(ref procInput), out PinnedSpanByte[] metadata);
        response.WriteBulk(metadata.Length);
        foreach (var value in metadata)
        {
            response.WriteBulk(value.ReadOnlySpan);
        }

        // RespInputHeader stores the object type in its first byte and the object operation in its second.
        var header = new RespInputHeader(GarnetObjectType.List);
        header.SetHeader((ushort)((byte)GarnetObjectType.List | ((byte)ListOperation.LRANGE << 8)), 0);
        var input = new ObjectInput(header, 0, -1);
        var listOutput = new ObjectOutput();
        try
        {
            if (api.ListRange(DataKey(ref procInput), ref input, ref listOutput) == GarnetStatus.OK)
            {
                // The output is a RESP array of bulk strings: copy its elements after the header.
                var encoded = listOutput.SpanByteAndMemory.ReadOnlySpan;
                var headerEnd = encoded.IndexOf((byte)'\n');
                if (encoded.Length == 0 || encoded[0] != (byte)'*' || headerEnd < 2
                    || !Utf8Parser.TryParse(encoded[1..(headerEnd - 1)], out int count, out _))
                {
                    throw new InvalidOperationException("Unexpected journal data encoding.");
                }

                response.WriteEncodedBulkStrings(encoded[(headerEnd + 1)..], count);
            }
        }
        finally
        {
            listOutput.SpanByteAndMemory.Dispose();
        }

        response.CopyTo(ref output);
    }
}

/// <summary>
/// Reads a journal's metadata. Arguments: keys. Returns <c>[1, metadata...]</c> or <c>[0]</c>.
/// </summary>
internal sealed class GarnetJournalGetMetadataProcedure : GarnetJournalProcedure
{
    protected override bool LocksData => false;

    protected override bool IsWrite => false;

    public override void Main<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
    {
        var journal = JournalRecord.Load(api, MetadataKey(ref procInput));
        if (!CheckBucket(api, ref procInput, journal, ref output))
        {
            return;
        }

        if (!journal.IsLive)
        {
            WriteStatus(ref output, GarnetJournalProtocol.MissingStatus);
            return;
        }

        WriteMetadata(api, ref procInput, ref output, GarnetJournalProtocol.SuccessStatus);
    }
}

/// <summary>
/// Creates a journal if it does not exist. Arguments: keys, new ETag, format, property count, properties.
/// Returns <c>[1, metadata...]</c> if created, or <c>[0, metadata...]</c> if it already existed.
/// </summary>
internal sealed class GarnetJournalCreateProcedure : GarnetJournalProcedure
{
    protected override bool LocksData => true;

    protected override bool IsWrite => true;

    public override void Main<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
    {
        var journal = JournalRecord.Load(api, MetadataKey(ref procInput));
        if (!CheckBucket(api, ref procInput, journal, ref output))
        {
            return;
        }

        var newETag = GetArg(ref procInput, FirstOperationArg);
        var format = GetArg(ref procInput, FirstOperationArg + 1);
        var countArg = GetArg(ref procInput, FirstOperationArg + 2);
        if (journal.Exists && journal.HasCommitted(newETag))
        {
            // A retry of a create which committed reports the creation.
            if (journal.IsLive)
            {
                WriteMetadata(api, ref procInput, ref output, GarnetJournalProtocol.SuccessStatus);
            }
            else
            {
                WriteStatus(ref output, GarnetJournalProtocol.SuccessStatus);
            }

            return;
        }

        if (journal.IsLive)
        {
            var status = Validate(journal);
            if (status != GarnetJournalProtocol.SuccessStatus)
            {
                WriteStatus(ref output, status);
                return;
            }

            WriteMetadata(api, ref procInput, ref output, GarnetJournalProtocol.MissingStatus);
            return;
        }

        if (!Utf8Parser.TryParse(countArg.ReadOnlySpan, out int count, out _) || count < 0)
        {
            WriteStatus(ref output, GarnetJournalProtocol.InvalidMetadataStatus);
            return;
        }

        var fields = new (PinnedSpanByte, PinnedSpanByte)[6 + count];
        fields[0] = (Pinned(GarnetJournalProtocol.ETagField), newETag);
        fields[1] = (Pinned(GarnetJournalProtocol.ContentETagField), newETag);
        fields[2] = (Pinned(GarnetJournalProtocol.AppendLengthField), Pinned(GarnetJournalProtocol.ZeroValue));
        fields[3] = (Pinned(GarnetJournalProtocol.FormatField), format);
        fields[4] = (Pinned(GarnetJournalProtocol.SchemaVersionField), Pinned(GarnetJournalProtocol.SchemaVersionValue));
        fields[5] = (Pinned(GarnetJournalProtocol.RecentETagsField), AppendRecentETag(journal.RecentETags, newETag));
        for (var i = 0; i < count; i++)
        {
            fields[6 + i] = (GetArg(ref procInput, FirstOperationArg + 3 + 2 * i), GetArg(ref procInput, FirstOperationArg + 4 + 2 * i));
        }

        ClearForCreate(api, ref procInput, journal);
        api.HashSet(MetadataKey(ref procInput), fields, out _);
        api.SetAdd(IndexKey(ref procInput), Member(ref procInput), out _);
        WriteMetadata(api, ref procInput, ref output, GarnetJournalProtocol.SuccessStatus);
    }
}

/// <summary>
/// Appends to or replaces a journal, creating it if it is expected to be absent.
/// Arguments: keys, expectation (<c>1</c>: the content ETag matches; <c>0</c>: the journal is absent), expected content ETag, new ETag, payload, format.
/// Returns <c>[1, appendLength]</c> or a failure status.
/// </summary>
internal abstract class GarnetJournalWriteProcedure : GarnetJournalProcedure
{
    protected override bool LocksData => true;

    protected override bool IsWrite => true;

    protected abstract bool IsReplace { get; }

    public override void Main<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
    {
        var journal = JournalRecord.Load(api, MetadataKey(ref procInput));
        if (!CheckBucket(api, ref procInput, journal, ref output))
        {
            return;
        }

        var expectsETag = ArgEquals(GetArg(ref procInput, FirstOperationArg), GarnetJournalProtocol.TrueValue);
        var expected = GetArg(ref procInput, FirstOperationArg + 1);
        var newETag = GetArg(ref procInput, FirstOperationArg + 2);
        var payload = GetArg(ref procInput, FirstOperationArg + 3);
        var format = GetArg(ref procInput, FirstOperationArg + 4);
        var dataKey = DataKey(ref procInput);
        var metadataKey = MetadataKey(ref procInput);

        // A retry of a write which committed reports its success instead of applying it again.
        if (journal.Exists && journal.HasCommitted(newETag))
        {
            WriteStatus(ref output, GarnetJournalProtocol.SuccessStatus, journal.IsLive ? journal.AppendLength : 0);
            return;
        }

        if (!journal.IsLive)
        {
            if (expectsETag)
            {
                WriteStatus(ref output, GarnetJournalProtocol.ConflictStatus);
                return;
            }

            var length = IsReplace ? 0 : payload.Length;
            var lengthValue = CreateArgSlice(GarnetJournalProtocol.FormatInt64(length));
            var createdRecent = AppendRecentETag(journal.RecentETags, newETag);
            ClearForCreate(api, ref procInput, journal);
            api.ListRightPush(dataKey, payload, out _);
            api.HashSet(metadataKey,
            [
                (Pinned(GarnetJournalProtocol.ETagField), newETag),
                (Pinned(GarnetJournalProtocol.ContentETagField), newETag),
                (Pinned(GarnetJournalProtocol.AppendLengthField), lengthValue),
                (Pinned(GarnetJournalProtocol.FormatField), format),
                (Pinned(GarnetJournalProtocol.SchemaVersionField), Pinned(GarnetJournalProtocol.SchemaVersionValue)),
                (Pinned(GarnetJournalProtocol.RecentETagsField), createdRecent),
            ], out _);
            api.SetAdd(IndexKey(ref procInput), Member(ref procInput), out _);

            WriteStatus(ref output, GarnetJournalProtocol.SuccessStatus, length);
            return;
        }

        var status = Validate(journal);
        if (status != GarnetJournalProtocol.SuccessStatus)
        {
            WriteStatus(ref output, status);
            return;
        }

        if (!expectsETag || !journal.ContentETag.SequenceEqual(expected.ReadOnlySpan))
        {
            WriteStatus(ref output, GarnetJournalProtocol.ConflictStatus);
            return;
        }

        var newLength = IsReplace ? 0 : journal.AppendLength + payload.Length;
        var newLengthValue = CreateArgSlice(GarnetJournalProtocol.FormatInt64(newLength));
        var recent = AppendRecentETag(journal.RecentETags, newETag);
        var formatChanged = !journal.Format.SequenceEqual(format.ReadOnlySpan);
        if (IsReplace)
        {
            api.DELETE(dataKey);
        }

        api.ListRightPush(dataKey, payload, out _);
        if (formatChanged)
        {
            api.HashSet(metadataKey,
            [
                (Pinned(GarnetJournalProtocol.ETagField), newETag),
                (Pinned(GarnetJournalProtocol.ContentETagField), newETag),
                (Pinned(GarnetJournalProtocol.AppendLengthField), newLengthValue),
                (Pinned(GarnetJournalProtocol.RecentETagsField), recent),
                (Pinned(GarnetJournalProtocol.FormatField), format),
            ], out _);
        }
        else
        {
            api.HashSet(metadataKey,
            [
                (Pinned(GarnetJournalProtocol.ETagField), newETag),
                (Pinned(GarnetJournalProtocol.ContentETagField), newETag),
                (Pinned(GarnetJournalProtocol.AppendLengthField), newLengthValue),
                (Pinned(GarnetJournalProtocol.RecentETagsField), recent),
            ], out _);
        }

        WriteStatus(ref output, GarnetJournalProtocol.SuccessStatus, newLength);
    }
}

internal sealed class GarnetJournalAppendProcedure : GarnetJournalWriteProcedure
{
    protected override bool IsReplace => false;
}

internal sealed class GarnetJournalReplaceProcedure : GarnetJournalWriteProcedure
{
    protected override bool IsReplace => true;
}

/// <summary>
/// Deletes a journal, leaving a tombstone which carries the deletion's ETag.
/// Arguments: keys, expected content ETag, expected to exist (0 or 1), new ETag.
/// Returns <c>[1]</c>, <c>[2]</c> if a journal expected to be absent exists, or a failure status.
/// </summary>
internal sealed class GarnetJournalDeleteProcedure : GarnetJournalProcedure
{
    protected override bool LocksData => true;

    protected override bool IsWrite => true;

    public override void Main<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
    {
        var journal = JournalRecord.Load(api, MetadataKey(ref procInput));
        if (!CheckBucket(api, ref procInput, journal, ref output))
        {
            return;
        }

        var expected = GetArg(ref procInput, FirstOperationArg);
        var expectExists = ArgEquals(GetArg(ref procInput, FirstOperationArg + 1), GarnetJournalProtocol.TrueValue);
        var newETag = GetArg(ref procInput, FirstOperationArg + 2);
        if (journal.Exists && journal.HasCommitted(newETag))
        {
            // A retry of a delete which committed.
            WriteStatus(ref output, GarnetJournalProtocol.SuccessStatus);
            return;
        }

        if (!journal.IsLive)
        {
            WriteStatus(ref output, expectExists ? GarnetJournalProtocol.ConflictStatus : GarnetJournalProtocol.SuccessStatus);
            return;
        }

        if (!expectExists)
        {
            WriteStatus(ref output, GarnetJournalProtocol.AppearedStatus);
            return;
        }

        var status = Validate(journal);
        if (status != GarnetJournalProtocol.SuccessStatus)
        {
            WriteStatus(ref output, status);
            return;
        }

        if (!journal.ContentETag.SequenceEqual(expected.ReadOnlySpan))
        {
            WriteStatus(ref output, GarnetJournalProtocol.ConflictStatus);
            return;
        }

        var metadataKey = MetadataKey(ref procInput);
        var recent = AppendRecentETag(journal.RecentETags, newETag);
        api.DELETE(DataKey(ref procInput));
        api.DELETE(metadataKey);
        api.HashSet(metadataKey,
        [
            (Pinned(GarnetJournalProtocol.DeletedField), Pinned(GarnetJournalProtocol.TrueValue)),
            (Pinned(GarnetJournalProtocol.ETagField), newETag),
            (Pinned(GarnetJournalProtocol.RecentETagsField), recent),
        ], out _);
        api.SetRemove(IndexKey(ref procInput), Member(ref procInput), out _);
        WriteStatus(ref output, GarnetJournalProtocol.SuccessStatus);
    }
}

/// <summary>
/// Conditionally updates caller-owned metadata.
/// Arguments: keys, has expected ETag (0 or 1), expected ETag, new ETag, remove count, removed names, set count, set pairs.
/// Returns <c>[1, metadata...]</c>, or <c>[0]</c> if the journal is missing or the ETag does not match.
/// </summary>
internal sealed class GarnetJournalUpdateMetadataProcedure : GarnetJournalProcedure
{
    protected override bool LocksData => false;

    protected override bool IsWrite => true;

    public override void Main<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
    {
        var metadataKey = MetadataKey(ref procInput);
        var journal = JournalRecord.Load(api, metadataKey);
        if (!CheckBucket(api, ref procInput, journal, ref output))
        {
            return;
        }

        var hasExpected = ArgEquals(GetArg(ref procInput, FirstOperationArg), GarnetJournalProtocol.TrueValue);
        var expected = GetArg(ref procInput, FirstOperationArg + 1);
        var newETag = GetArg(ref procInput, FirstOperationArg + 2);

        // A retry of an update which committed reports the journal's current metadata.
        if (journal.Exists && journal.HasCommitted(newETag))
        {
            if (journal.IsLive)
            {
                WriteMetadata(api, ref procInput, ref output, GarnetJournalProtocol.SuccessStatus);
            }
            else
            {
                WriteStatus(ref output, GarnetJournalProtocol.MissingStatus);
            }

            return;
        }

        if (!journal.IsLive)
        {
            WriteStatus(ref output, GarnetJournalProtocol.MissingStatus);
            return;
        }

        var status = Validate(journal);
        if (status != GarnetJournalProtocol.SuccessStatus)
        {
            WriteStatus(ref output, status);
            return;
        }

        if (hasExpected && !journal.ETag.SequenceEqual(expected.ReadOnlySpan))
        {
            WriteStatus(ref output, GarnetJournalProtocol.MissingStatus);
            return;
        }

        var index = FirstOperationArg + 3;
        if (!Utf8Parser.TryParse(GetArg(ref procInput, index++).ReadOnlySpan, out int removeCount, out _) || removeCount < 0)
        {
            WriteStatus(ref output, GarnetJournalProtocol.InvalidMetadataStatus);
            return;
        }

        var removeStart = index;
        index += removeCount;
        if (!Utf8Parser.TryParse(GetArg(ref procInput, index++).ReadOnlySpan, out int setCount, out _) || setCount < 0)
        {
            WriteStatus(ref output, GarnetJournalProtocol.InvalidMetadataStatus);
            return;
        }

        var setStart = index;
        var recent = AppendRecentETag(journal.RecentETags, newETag);
        var removals = new List<PinnedSpanByte>(removeCount);
        for (var i = 0; i < removeCount; i++)
        {
            var name = GetArg(ref procInput, removeStart + i);
            api.HashExists(metadataKey, name, out var exists);
            if (exists)
            {
                removals.Add(name);
            }
        }

        var sets = new List<(PinnedSpanByte, PinnedSpanByte)>(setCount + 1);
        for (var i = 0; i < setCount; i++)
        {
            var name = GetArg(ref procInput, setStart + 2 * i);
            var value = GetArg(ref procInput, setStart + 2 * i + 1);
            if (api.HashGet(metadataKey, name, out var current) != GarnetStatus.OK
                || !current.ReadOnlySpan.SequenceEqual(value.ReadOnlySpan))
            {
                sets.Add((name, value));
            }
        }

        if (removals.Count > 0 || sets.Count > 0)
        {
            if (removals.Count > 0)
            {
                api.HashDelete(metadataKey, removals.ToArray(), out _);
            }

            sets.Add((Pinned(GarnetJournalProtocol.ETagField), newETag));
            sets.Add((Pinned(GarnetJournalProtocol.RecentETagsField), recent));
            api.HashSet(metadataKey, sets.ToArray(), out _);
        }

        WriteMetadata(api, ref procInput, ref output, GarnetJournalProtocol.SuccessStatus);
    }
}

/// <summary>
/// Transitions a bucket's state in this partition.
/// Arguments: bucket key, operation (<c>open</c> or <c>seal</c>), version, source, done (0 or 1).
/// Returns <c>[1]</c>, or <c>[-1, state, version]</c> if the transition does not apply to the current state.
/// </summary>
/// <remarks>
/// The procedure takes the bucket key's exclusive lock, which waits for journal procedures holding its shared lock, so
/// a seal is ordered after every operation that observed the bucket open and before every operation that follows it.
/// </remarks>
internal sealed class GarnetJournalBucketProcedure : CustomTransactionProcedure
{
    public override TimeSpan KeyLockTimeout => TimeSpan.FromSeconds(10);

    public override bool Prepare<TGarnetReadApi>(TGarnetReadApi api, ref CustomProcedureInput procInput)
    {
        var offset = 0;
        var bucketKey = GetNextArg(ref procInput, ref offset);
        if (!bucketKey.IsValid)
        {
            return false;
        }

        AddKey(bucketKey, LockType.Exclusive, StoreType.Object);
        return true;
    }

    public override void Main<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
    {
        var offset = 0;
        var bucketKey = GetNextArg(ref procInput, ref offset);
        var operation = GetNextArg(ref procInput, ref offset);
        var version = GetNextArg(ref procInput, ref offset);
        var source = GetNextArg(ref procInput, ref offset);
        var done = GetNextArg(ref procInput, ref offset);
        var bucket = BucketRecord.Load(api, bucketKey);
        var response = new GarnetRespArrayWriter();
        if (!GarnetJournalProtocol.TryParseNonNegativeCanonical(version.ReadOnlySpan, out var requestedVersion))
        {
            response.WriteBulk(GarnetJournalProtocol.InvalidMetadataStatus);
            response.CopyTo(ref output);
            return;
        }

        var currentVersion = 0L;
        if (bucket.Exists && !GarnetJournalProtocol.TryParseNonNegativeCanonical(bucket.Version, out currentVersion))
        {
            response.WriteBulk(GarnetJournalProtocol.InvalidMetadataStatus);
            response.CopyTo(ref output);
            return;
        }

        bool applies;
        bool alreadyApplied;
        if (operation.ReadOnlySpan.SequenceEqual(GarnetJournalProtocol.OpenOperation))
        {
            alreadyApplied = bucket.IsOpen && currentVersion == requestedVersion;
            applies = !bucket.Exists;
            if (applies)
            {
                api.HashSet(bucketKey,
                [
                    (PinnedSpanByte.FromPinnedSpan(GarnetJournalProtocol.BucketStateField), PinnedSpanByte.FromPinnedSpan(GarnetJournalProtocol.OpenState)),
                    (PinnedSpanByte.FromPinnedSpan(GarnetJournalProtocol.BucketVersionField), version),
                    (PinnedSpanByte.FromPinnedSpan(GarnetJournalProtocol.BucketSourceField), source),
                    (PinnedSpanByte.FromPinnedSpan(GarnetJournalProtocol.BucketDoneField), done),
                ], out _);
            }
        }
        else if (operation.ReadOnlySpan.SequenceEqual(GarnetJournalProtocol.SealOperation))
        {
            alreadyApplied = bucket.Exists && bucket.State.SequenceEqual(GarnetJournalProtocol.SealedState) && currentVersion == requestedVersion;
            applies = bucket.IsOpen && currentVersion < requestedVersion;
            if (applies)
            {
                api.HashSet(bucketKey,
                [
                    (PinnedSpanByte.FromPinnedSpan(GarnetJournalProtocol.BucketStateField), PinnedSpanByte.FromPinnedSpan(GarnetJournalProtocol.SealedState)),
                    (PinnedSpanByte.FromPinnedSpan(GarnetJournalProtocol.BucketVersionField), version),
                ], out _);
            }
        }
        else
        {
            response.WriteBulk(GarnetJournalProtocol.InvalidMetadataStatus);
            response.CopyTo(ref output);
            return;
        }

        if (applies || alreadyApplied)
        {
            response.WriteBulk(GarnetJournalProtocol.SuccessStatus);
        }
        else
        {
            response.WriteBulk(GarnetJournalProtocol.ConflictStatus);
            response.WriteBulk(bucket.Exists ? bucket.State : GarnetJournalProtocol.AbsentState);
            response.WriteBulk(bucket.Exists ? bucket.Version : GarnetJournalProtocol.ZeroValue);
        }

        response.CopyTo(ref output);
    }
}

/// <summary>
/// Registers the journal procedures with a Garnet server.
/// </summary>
internal static class GarnetJournalProcedures
{
    /// <summary>
    /// Registers every journal procedure, in the order that assigns their persisted ids.
    /// </summary>
    /// <remarks>Call before the server starts, since recovery replays logged procedure invocations by id.</remarks>
    public static void Register(global::Garnet.GarnetServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        Func<CustomTransactionProcedure>[] factories =
        [
            static () => new GarnetJournalReadProcedure(),
            static () => new GarnetJournalGetMetadataProcedure(),
            static () => new GarnetJournalCreateProcedure(),
            static () => new GarnetJournalAppendProcedure(),
            static () => new GarnetJournalReplaceProcedure(),
            static () => new GarnetJournalDeleteProcedure(),
            static () => new GarnetJournalUpdateMetadataProcedure(),
            static () => new GarnetJournalBucketProcedure(),
        ];

        for (var i = 0; i < factories.Length; i++)
        {
            var id = server.Register.NewTransactionProc(GarnetJournalProtocol.Names[i], factories[i]);
            if (id != i)
            {
                throw new InvalidOperationException(
                    $"Garnet assigned id {id} to journal procedure '{GarnetJournalProtocol.Names[i]}'; expected {i}. Journal procedures must be registered first, in a fixed order.");
            }
        }
    }
}
