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
/// journal data key, the bucket index key, the bucket marker key, and the encoded journal id. Every procedure locks the bucket state key, so
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
    private const int MarkerKeyArg = 4;
    private const int MemberArg = 5;
    protected const int FirstOperationArg = 6;

    // Locks are held only while Main runs in memory; AOF commit waits happen after the locks are released.
    public override TimeSpan KeyLockTimeout => TimeSpan.FromSeconds(10);

    /// <summary>Gets the partition's load counters, which record every operation on an open bucket.</summary>
    public GarnetJournalLoad? Load { get; init; }

    protected abstract bool LocksData { get; }

    protected abstract bool IsWrite { get; }

    public override bool Prepare<TGarnetReadApi>(TGarnetReadApi api, ref CustomProcedureInput procInput)
    {
        var offset = 0;
        var bucketKey = GetNextArg(ref procInput, ref offset);
        var metadataKey = GetNextArg(ref procInput, ref offset);
        var dataKey = GetNextArg(ref procInput, ref offset);
        var indexKey = GetNextArg(ref procInput, ref offset);
        var markerKey = GetNextArg(ref procInput, ref offset);
        if (!bucketKey.IsValid || !metadataKey.IsValid || !dataKey.IsValid || !indexKey.IsValid || !markerKey.IsValid)
        {
            return false;
        }

        // Every procedure, reads included, locks the metadata key exclusively, so Garnet logs it to the AOF and its
        // reply waits for a commit, which fails once a later hosting tenure has fenced the partition's storage. A
        // fenced host therefore serves no reads, which could otherwise miss writes the later tenure acknowledged (D32).
        var type = IsWrite ? LockType.Exclusive : LockType.Shared;
        AddKey(bucketKey, LockType.Shared, StoreType.Object);
        AddKey(metadataKey, LockType.Exclusive, StoreType.Object);
        if (LocksData)
        {
            AddKey(dataKey, type, StoreType.Object);
            if (IsWrite)
            {
                AddKey(indexKey, LockType.Exclusive, StoreType.Object);
                AddKey(markerKey, LockType.Exclusive, StoreType.Object);
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

    protected static PinnedSpanByte MarkerKey(ref CustomProcedureInput procInput) => GetArg(ref procInput, MarkerKeyArg);

    protected static PinnedSpanByte Member(ref CustomProcedureInput procInput) => GetArg(ref procInput, MemberArg);

    /// <summary>Writes <c>WrongPartition(state, version)</c> for a bucket this partition does not hold as required.</summary>
    protected static void WriteWrongPartition(in BucketRecord bucket, ref MemoryResult<byte> output)
    {
        var response = new GarnetRespArrayWriter();
        response.WriteBulk(GarnetJournalProtocol.WrongPartitionStatus);
        response.WriteBulk(bucket.Exists ? bucket.State : GarnetJournalProtocol.AbsentState);
        response.WriteBulk(bucket.Exists ? bucket.Version : GarnetJournalProtocol.ZeroValue);
        response.CopyTo(ref output);
    }

    /// <summary>
    /// Checks that the bucket is open in this partition and, for journals absent from it, that its migration is complete.
    /// </summary>
    /// <returns><see langword="true"/> if the operation may proceed; otherwise, the response has been written.</returns>
    protected bool CheckBucket<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, in JournalRecord journal, ref MemoryResult<byte> output)
        where TGarnetApi : IGarnetReadApi
    {
        var bucket = BucketRecord.Load(api, BucketKey(ref procInput));
        if (!bucket.IsOpen)
        {
            WriteWrongPartition(bucket, ref output);
            return false;
        }

        Load?.Record(BucketKey(ref procInput).ReadOnlySpan);

        // Journals the partition holds, including deletion tombstones and absent markers, are authoritative. Others
        // may still be on the bucket's migration chain until migration completes, unless the chain's key filters
        // report that no partition on it holds a record of the journal (D36): then the journal is in the empty
        // absence at the bucket's collection generation, exactly as the absent marker a pull would install.
        if (!journal.Exists && !bucket.IsDone && !GarnetJournalKeyFilter.ExcludesAll(bucket.Chain, Member(ref procInput).ReadOnlySpan))
        {
            WriteNeedPull(bucket, ref output);
            return false;
        }

        return true;
    }

    /// <summary>Writes <c>NeedPull(source, version)</c>: the source to pull from, for the bucket's tenure at <c>version</c>.</summary>
    protected static void WriteNeedPull(in BucketRecord bucket, ref MemoryResult<byte> output)
    {
        var response = new GarnetRespArrayWriter();
        response.WriteBulk(GarnetJournalProtocol.NeedPullStatus);
        response.WriteBulk(bucket.Source);
        response.WriteBulk(bucket.Version);
        response.CopyTo(ref output);
    }

    /// <summary>Gets the bucket's collection generation (see <see cref="IsCollectedSince"/>).</summary>
    protected static long GetCollectionGeneration<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput)
        where TGarnetApi : IGarnetReadApi
        => api.HashGet(BucketKey(ref procInput), Pinned(GarnetJournalProtocol.BucketCollectedField), out var value) == GarnetStatus.OK
            && GarnetJournalProtocol.TryParseNonNegativeCanonical(value.ReadOnlySpan, out var generation)
            ? generation
            : 0;

    /// <summary>
    /// Gets a value indicating whether the journal is in the empty absence and the bucket has dropped, since the caller
    /// observed that absence at <paramref name="observedGeneration"/>, a tombstone of a deletion stamped at or after it.
    /// </summary>
    /// <remarks>
    /// Dropping a tombstone discards the ETags which let a retried mutation recognize that it committed, and restores
    /// the empty absence, which a retried create is conditioned on. The partition stamps each tombstone with the
    /// bucket's collection generation, and dropping it raises the generation past the stamp; generations only grow, and
    /// every later opener of the bucket inherits its source's. A mutation which committed before a deletion observed its
    /// absence before it, at a generation no later than the deletion's stamp, so once the tombstone is dropped the
    /// retry is rejected, however long it was delayed (D25). Generations are assigned by the partitions holding the
    /// bucket, so the comparison depends on no client's clock. A fresh observation of the empty absence reads the
    /// current generation and is accepted.
    /// </remarks>
    protected static bool IsCollectedSince<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, in JournalRecord journal, long observedGeneration)
        where TGarnetApi : IGarnetReadApi
        => journal.AbsenceETag.IsEmpty && observedGeneration < GetCollectionGeneration(api, ref procInput);

    protected static void WriteStatus(ref MemoryResult<byte> output, int status)
    {
        var response = new GarnetRespArrayWriter();
        response.WriteBulk(status);
        response.CopyTo(ref output);
    }

    /// <summary>
    /// Writes the missing status followed by the journal's absence ETag and the bucket's collection generation, which a
    /// write conditioned on this absence carries.
    /// </summary>
    protected static void WriteMissing<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output, in JournalRecord journal)
        where TGarnetApi : IGarnetReadApi
    {
        var response = new GarnetRespArrayWriter();
        response.WriteBulk(GarnetJournalProtocol.MissingStatus);
        response.WriteBulk(journal.AbsenceETag);
        response.WriteBulk(GetCollectionGeneration(api, ref procInput));
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

    /// <summary>Removes a tombstone or absent marker and any stale data before the journal is created.</summary>
    protected static void ClearForCreate<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, in JournalRecord journal)
        where TGarnetApi : IGarnetApi
    {
        if (journal.IsMarker)
        {
            api.DELETE(MetadataKey(ref procInput));
            api.SetRemove(MarkerKey(ref procInput), Member(ref procInput), out _);
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

    /// <summary>Appends the journal's data segments, one bulk string per append, to <paramref name="response"/>.</summary>
    protected static void WriteSegments<TGarnetApi>(TGarnetApi api, PinnedSpanByte dataKey, GarnetRespArrayWriter response)
        where TGarnetApi : IGarnetReadApi
    {
        // RespInputHeader stores the object type in its first byte and the object operation in its second.
        var header = new RespInputHeader(GarnetObjectType.List);
        header.SetHeader((ushort)((byte)GarnetObjectType.List | ((byte)ListOperation.LRANGE << 8)), 0);
        var input = new ObjectInput(header, 0, -1);
        var listOutput = new ObjectOutput();
        try
        {
            if (api.ListRange(dataKey, ref input, ref listOutput) == GarnetStatus.OK)
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
    }

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

    /// <summary>Gets a value indicating whether the partition has reclaimed its sealed copy of the bucket.</summary>
    public bool IsReclaimed { get; init; }

    /// <summary>Gets the bucket's collection generation (see <see cref="GarnetJournalProcedure.IsCollectedSince"/>).</summary>
    public long CollectionGeneration { get; init; }

    /// <summary>Gets the key filters a sealed copy wrote for its destination, or empty if it wrote none.</summary>
    public ReadOnlySpan<byte> Filter { get; init; }

    /// <summary>Gets the key filters of the chain an open bucket's incomplete migration reads from, or empty if unknown.</summary>
    public ReadOnlySpan<byte> Chain { get; init; }
    public bool IsOpen => Exists && State.SequenceEqual(GarnetJournalProtocol.OpenState);
    public bool IsSealed => Exists && State.SequenceEqual(GarnetJournalProtocol.SealedState);

    public static BucketRecord Load<TGarnetApi>(TGarnetApi api, PinnedSpanByte key)
        where TGarnetApi : IGarnetReadApi
    {
        if (api.HashGetAll(key, out PinnedSpanByte[] values) != GarnetStatus.OK || values.Length == 0)
        {
            return default;
        }

        ReadOnlySpan<byte> state = default, version = default, source = default, done = default, reclaimed = default, collected = default, filter = default, chain = default;
        for (var i = 0; i + 1 < values.Length; i += 2)
        {
            var field = values[i].ReadOnlySpan;
            var value = values[i + 1].ReadOnlySpan;
            if (field.SequenceEqual(GarnetJournalProtocol.BucketStateField)) state = value;
            else if (field.SequenceEqual(GarnetJournalProtocol.BucketVersionField)) version = value;
            else if (field.SequenceEqual(GarnetJournalProtocol.BucketSourceField)) source = value;
            else if (field.SequenceEqual(GarnetJournalProtocol.BucketDoneField)) done = value;
            else if (field.SequenceEqual(GarnetJournalProtocol.BucketReclaimedField)) reclaimed = value;
            else if (field.SequenceEqual(GarnetJournalProtocol.BucketCollectedField)) collected = value;
            else if (field.SequenceEqual(GarnetJournalProtocol.BucketFilterField)) filter = value;
            else if (field.SequenceEqual(GarnetJournalProtocol.BucketChainField)) chain = value;
        }

        return new()
        {
            Exists = true,
            State = state,
            Version = version,
            Source = source,
            IsDone = done.SequenceEqual(GarnetJournalProtocol.TrueValue),
            IsReclaimed = reclaimed.SequenceEqual(GarnetJournalProtocol.TrueValue),
            CollectionGeneration = GarnetJournalProtocol.TryParseNonNegativeCanonical(collected, out var generation) ? generation : 0,
            Filter = filter,
            Chain = chain,
        };
    }
}

/// <summary>The provider-owned metadata of a journal in a partition.</summary>
internal readonly ref struct JournalRecord
{
    /// <summary>Gets a value indicating whether the partition holds a record of the journal: the journal, its deletion tombstone, or an absent marker.</summary>
    public bool Exists { get; init; }
    public bool IsTombstone { get; init; }

    /// <summary>Gets a value indicating whether the record is an absent marker: a pull found no record of the journal on the bucket's migration chain.</summary>
    public bool IsAbsentMarker { get; init; }
    public bool IsMarker => IsTombstone || IsAbsentMarker;
    public bool IsLive => Exists && !IsMarker;
    public ReadOnlySpan<byte> ETag { get; init; }
    public ReadOnlySpan<byte> ContentETag { get; init; }
    public ReadOnlySpan<byte> Format { get; init; }
    public ReadOnlySpan<byte> SchemaVersion { get; init; }
    public long AppendLength { get; init; }
    public bool HasValidAppendLength { get; init; }

    /// <summary>Gets the time of a tombstone's deletion, in Unix milliseconds by the deleting client's clock.</summary>
    public ReadOnlySpan<byte> DeletedAt { get; init; }

    /// <summary>Gets the bucket's collection generation when the tombstone's deletion committed (D25).</summary>
    public ReadOnlySpan<byte> DeletedGeneration { get; init; }

    /// <summary>Gets the newline-separated ETags of the journal's most recent mutations, including its deletion.</summary>
    public ReadOnlySpan<byte> RecentETags { get; init; }

    /// <summary>
    /// Gets the identity of the journal's absence: its tombstone's ETag, or empty if the partition holds no record or
    /// an absent marker.
    /// </summary>
    /// <remarks>
    /// Each deletion writes a fresh ETag, so an absence never recurs while retries can arrive. A write conditioned on
    /// the absence it observed therefore cannot apply again after a later creation and deletion.
    /// </remarks>
    public ReadOnlySpan<byte> AbsenceETag => IsTombstone ? ETag : default;

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

        ReadOnlySpan<byte> eTag = default, contentETag = default, format = default, schemaVersion = default, appendLength = default, recent = default, deletedAt = default, deletedGeneration = default;
        var deleted = false;
        var absent = false;
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
            else if (field.SequenceEqual(GarnetJournalProtocol.DeletedAtField)) deletedAt = value;
            else if (field.SequenceEqual(GarnetJournalProtocol.DeletedGenerationField)) deletedGeneration = value;
            else if (field.SequenceEqual(GarnetJournalProtocol.AbsentField)) absent = true;
        }

        var validLength = GarnetJournalProtocol.TryParseNonNegativeCanonical(appendLength, out var length);
        return new()
        {
            Exists = true,
            IsTombstone = deleted,
            IsAbsentMarker = absent,
            ETag = eTag,
            ContentETag = contentETag,
            Format = format,
            SchemaVersion = schemaVersion,
            AppendLength = length,
            HasValidAppendLength = validLength,
            RecentETags = recent,
            DeletedAt = deletedAt,
            DeletedGeneration = deletedGeneration,
        };
    }
}

/// <summary>
/// Reads a journal's metadata and data. Arguments: keys.
/// Returns <c>[1, metadataValueCount, metadata..., segments...]</c>, or <c>[0, absenceETag]</c> if the journal is absent.
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
            WriteMissing(api, ref procInput, ref output, journal);
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

        WriteSegments(api, DataKey(ref procInput), response);
        response.CopyTo(ref output);
    }
}

/// <summary>
/// Reads a journal's metadata. Arguments: keys. Returns <c>[1, metadata...]</c>, or <c>[0, absenceETag]</c> if the journal is absent.
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
            WriteMissing(api, ref procInput, ref output, journal);
            return;
        }

        WriteMetadata(api, ref procInput, ref output, GarnetJournalProtocol.SuccessStatus);
    }
}

/// <summary>
/// Creates a journal if it does not exist. Arguments: keys, new ETag, format, expected absence ETag, the collection
/// generation observed with that absence, property count, properties.
/// Returns <c>[1, metadata...]</c> if created, <c>[0, metadata...]</c> if it already existed, or <c>[-1]</c> if it is
/// absent with another absence ETag than expected (it was created and deleted since the caller observed it), or the
/// bucket has collected a tombstone since the caller observed the empty absence (see
/// <see cref="GarnetJournalProcedure.IsCollectedSince"/>).
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
        var expectedAbsence = GetArg(ref procInput, FirstOperationArg + 2);
        var observedGeneration = GetArg(ref procInput, FirstOperationArg + 3);
        var countArg = GetArg(ref procInput, FirstOperationArg + 4);
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

        if (!GarnetJournalProtocol.TryParseNonNegativeCanonical(observedGeneration.ReadOnlySpan, out var observedGenerationValue))
        {
            WriteStatus(ref output, GarnetJournalProtocol.InvalidMetadataStatus);
            return;
        }

        if (!journal.AbsenceETag.SequenceEqual(expectedAbsence.ReadOnlySpan)
            || IsCollectedSince(api, ref procInput, journal, observedGenerationValue))
        {
            WriteStatus(ref output, GarnetJournalProtocol.ConflictStatus);
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
            fields[6 + i] = (GetArg(ref procInput, FirstOperationArg + 5 + 2 * i), GetArg(ref procInput, FirstOperationArg + 6 + 2 * i));
        }

        ClearForCreate(api, ref procInput, journal);
        api.HashSet(MetadataKey(ref procInput), fields, out _);
        api.SetAdd(IndexKey(ref procInput), Member(ref procInput), out _);
        WriteMetadata(api, ref procInput, ref output, GarnetJournalProtocol.SuccessStatus);
    }
}

/// <summary>
/// Appends to or replaces a journal, creating it if it is expected to be absent.
/// Arguments: keys, expectation (<c>1</c>: the journal's content ETag matches; <c>0</c>: the journal is absent with the
/// expected absence ETag), expected ETag, new ETag, payload, format, the collection generation observed with the
/// absence (see <see cref="GarnetJournalProcedure.IsCollectedSince"/>).
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
        var observedGeneration = GetArg(ref procInput, FirstOperationArg + 5);
        var dataKey = DataKey(ref procInput);
        var metadataKey = MetadataKey(ref procInput);
        if (!GarnetJournalProtocol.TryParseNonNegativeCanonical(observedGeneration.ReadOnlySpan, out var observedGenerationValue))
        {
            WriteStatus(ref output, GarnetJournalProtocol.InvalidMetadataStatus);
            return;
        }

        // A retry of a write which committed reports its success instead of applying it again.
        if (journal.Exists && journal.HasCommitted(newETag))
        {
            WriteStatus(ref output, GarnetJournalProtocol.SuccessStatus, journal.IsLive ? journal.AppendLength : 0);
            return;
        }

        if (!journal.IsLive)
        {
            // A write which creates the journal names the absence it observed: its tombstone's ETag, or no record.
            if (expectsETag
                || !journal.AbsenceETag.SequenceEqual(expected.ReadOnlySpan)
                || IsCollectedSince(api, ref procInput, journal, observedGenerationValue))
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
/// Deletes a journal, leaving a tombstone which carries the deletion's ETag, its time, and the bucket's collection
/// generation when it committed (D25).
/// Arguments: keys, expected content ETag, expected to exist (0 or 1), new ETag, deletion time (Unix milliseconds by the
/// client's clock, which bounds only how long the tombstone is retained).
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
        var deletedAt = GetArg(ref procInput, FirstOperationArg + 3);
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

        if (!GarnetJournalProtocol.TryParseNonNegativeCanonical(deletedAt.ReadOnlySpan, out _))
        {
            WriteStatus(ref output, GarnetJournalProtocol.InvalidMetadataStatus);
            return;
        }

        var metadataKey = MetadataKey(ref procInput);
        var recent = AppendRecentETag(journal.RecentETags, newETag);
        var generation = CreateArgSlice(GarnetJournalProtocol.FormatInt64(GetCollectionGeneration(api, ref procInput)));
        api.DELETE(DataKey(ref procInput));
        api.DELETE(metadataKey);
        api.HashSet(metadataKey,
        [
            (Pinned(GarnetJournalProtocol.DeletedField), Pinned(GarnetJournalProtocol.TrueValue)),
            (Pinned(GarnetJournalProtocol.ETagField), newETag),
            (Pinned(GarnetJournalProtocol.RecentETagsField), recent),
            (Pinned(GarnetJournalProtocol.DeletedAtField), deletedAt),
            (Pinned(GarnetJournalProtocol.DeletedGenerationField), generation),
        ], out _);
        api.SetRemove(IndexKey(ref procInput), Member(ref procInput), out _);
        api.SetAdd(MarkerKey(ref procInput), Member(ref procInput), out _);
        WriteStatus(ref output, GarnetJournalProtocol.SuccessStatus);
    }
}

/// <summary>
/// Conditionally updates caller-owned metadata.
/// Arguments: keys, has expected ETag (0 or 1), expected ETag, new ETag, remove count, removed names, set count, set pairs.
/// Returns <c>[1, metadata...]</c>, <c>[0]</c> if the journal is missing, or <c>[-1]</c> if the ETag does not match.
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
            WriteStatus(ref output, GarnetJournalProtocol.ConflictStatus);
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
        else
        {
            // A no-op keeps the metadata ETag but records the request, so a retry after another writer's update reports
            // this update's success instead of applying it again.
            api.HashSet(metadataKey, Pinned(GarnetJournalProtocol.RecentETagsField), recent, out _);
        }

        WriteMetadata(api, ref procInput, ref output, GarnetJournalProtocol.SuccessStatus);
    }
}

/// <summary>
/// Lists the encoded ids of the journals in a bucket. Arguments: bucket key, index key.
/// Returns <c>[1, members...]</c>, <c>WrongPartition</c> if the bucket is not open, or <c>NeedPull</c> if its migration is incomplete.
/// </summary>
/// <remarks>
/// The bucket's index is updated in the same transaction as every create and delete, so the listing is one snapshot
/// of the journals in the bucket. While the bucket's migration is incomplete, its source may hold more journals.
/// </remarks>
internal sealed class GarnetJournalIndexProcedure : CustomTransactionProcedure
{
    public override TimeSpan KeyLockTimeout => TimeSpan.FromSeconds(10);

    public override bool Prepare<TGarnetReadApi>(TGarnetReadApi api, ref CustomProcedureInput procInput)
    {
        var offset = 0;
        var bucketKey = GetNextArg(ref procInput, ref offset);
        var indexKey = GetNextArg(ref procInput, ref offset);
        if (!bucketKey.IsValid || !indexKey.IsValid)
        {
            return false;
        }

        AddKey(bucketKey, LockType.Shared, StoreType.Object);

        // The exclusive lock makes Garnet log the listing, so its reply waits for a fenced AOF commit (D32).
        AddKey(indexKey, LockType.Exclusive, StoreType.Object);
        return true;
    }

    public override void Main<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
    {
        var offset = 0;
        var bucketKey = GetNextArg(ref procInput, ref offset);
        var indexKey = GetNextArg(ref procInput, ref offset);
        var bucket = BucketRecord.Load(api, bucketKey);
        var response = new GarnetRespArrayWriter();
        if (!bucket.IsOpen)
        {
            response.WriteBulk(GarnetJournalProtocol.WrongPartitionStatus);
            response.WriteBulk(bucket.Exists ? bucket.State : GarnetJournalProtocol.AbsentState);
            response.WriteBulk(bucket.Exists ? bucket.Version : GarnetJournalProtocol.ZeroValue);
        }
        else if (!bucket.IsDone)
        {
            response.WriteBulk(GarnetJournalProtocol.NeedPullStatus);
            response.WriteBulk(bucket.Source);
            response.WriteBulk(bucket.Version);
        }
        else
        {
            response.WriteBulk(GarnetJournalProtocol.SuccessStatus);
            if (api.SetMembers(indexKey, out PinnedSpanByte[] members) == GarnetStatus.OK)
            {
                foreach (var member in members)
                {
                    response.WriteBulk(member.ReadOnlySpan);
                }
            }
        }

        response.CopyTo(ref output);
    }
}

/// <summary>
/// Transitions a bucket's state in this partition.
/// Arguments: bucket key, index key, marker key, operation (<c>open</c>, <c>seal</c>, or <c>reclaim</c>), version, source,
/// done (0 or 1), the collection generation the bucket opens with (the source's; <c>-</c> or <c>0</c> for none), and
/// optionally, for <c>seal</c>, <c>1</c> to write key filters, or for <c>open</c>, the key filters of the chain the
/// bucket opens from (D36).
/// Returns <c>[1]</c>, or <c>[-1, state, version, reclaimed]</c> if the transition does not apply to the current state.
/// </summary>
/// <remarks>
/// <para>
/// The procedure takes the bucket key's exclusive lock, which waits for journal procedures holding its shared lock, so
/// a seal is ordered after every operation that observed the bucket open and before every operation that follows it.
/// </para>
/// <para>
/// <c>open</c> opens a bucket the partition holds no copy of: an absent bucket, or a reclaimed one, at a later version,
/// whose journals have all been purged (the reopen rule, D11). <c>seal</c> seals an open bucket at a later version and,
/// if requested, writes key filters over the journals it holds a record of (its index and marker set: the migration
/// inventory, D42), followed by its own chain's filters while its migration is not done; it writes none if those are
/// unknown. <c>reclaim</c> marks the copy sealed at <c>version</c> as reclaimed (D42): from then on, every read of it
/// fails, and its journals may be purged. Each transition reports success if it has already been applied, so retries
/// and duplicate coordinators are harmless.
/// </para>
/// </remarks>
internal sealed class GarnetJournalBucketProcedure : CustomTransactionProcedure
{
    public override TimeSpan KeyLockTimeout => TimeSpan.FromSeconds(10);

    public override bool Prepare<TGarnetReadApi>(TGarnetReadApi api, ref CustomProcedureInput procInput)
    {
        var offset = 0;
        var bucketKey = GetNextArg(ref procInput, ref offset);
        var indexKey = GetNextArg(ref procInput, ref offset);
        var markerKey = GetNextArg(ref procInput, ref offset);
        if (!bucketKey.IsValid || !indexKey.IsValid || !markerKey.IsValid)
        {
            return false;
        }

        AddKey(bucketKey, LockType.Exclusive, StoreType.Object);
        AddKey(indexKey, LockType.Shared, StoreType.Object);
        AddKey(markerKey, LockType.Shared, StoreType.Object);
        return true;
    }

    public override void Main<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
    {
        var offset = 0;
        var bucketKey = GetNextArg(ref procInput, ref offset);
        var indexKey = GetNextArg(ref procInput, ref offset);
        var markerKey = GetNextArg(ref procInput, ref offset);
        var operation = GetNextArg(ref procInput, ref offset);
        var version = GetNextArg(ref procInput, ref offset);
        var source = GetNextArg(ref procInput, ref offset);
        var done = GetNextArg(ref procInput, ref offset);
        var collected = GetNextArg(ref procInput, ref offset);
        var extra = GetNextArg(ref procInput, ref offset);
        var bucket = BucketRecord.Load(api, bucketKey);
        var response = new GarnetRespArrayWriter();
        if (!GarnetJournalProtocol.TryParseNonNegativeCanonical(version.ReadOnlySpan, out var requestedVersion)
            || collected.IsValid && !collected.ReadOnlySpan.SequenceEqual("-"u8) && !GarnetJournalProtocol.TryParseNonNegativeCanonical(collected.ReadOnlySpan, out _))
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
            applies = !bucket.Exists
                || bucket.IsSealed && bucket.IsReclaimed && currentVersion < requestedVersion
                    && IsEmpty(api, indexKey) && IsEmpty(api, markerKey);
            if (applies)
            {
                if (bucket.Exists)
                {
                    api.DELETE(bucketKey);
                }

                api.HashSet(bucketKey,
                [
                    (PinnedSpanByte.FromPinnedSpan(GarnetJournalProtocol.BucketStateField), PinnedSpanByte.FromPinnedSpan(GarnetJournalProtocol.OpenState)),
                    (PinnedSpanByte.FromPinnedSpan(GarnetJournalProtocol.BucketVersionField), version),
                    (PinnedSpanByte.FromPinnedSpan(GarnetJournalProtocol.BucketSourceField), source),
                    (PinnedSpanByte.FromPinnedSpan(GarnetJournalProtocol.BucketDoneField), done),
                ], out _);

                // The bucket inherits its source's collection generation, so generations only grow along the bucket's
                // history: retries rejected there are rejected here too.
                if (collected.IsValid && !collected.ReadOnlySpan.SequenceEqual("-"u8))
                {
                    api.HashSet(bucketKey, PinnedSpanByte.FromPinnedSpan(GarnetJournalProtocol.BucketCollectedField), collected, out _);
                }

                // A bucket opened with its migration incomplete keeps its chain's key filters, which the sealed source
                // wrote and which never change (D36).
                if (extra.IsValid && extra.Length > 0 && !done.ReadOnlySpan.SequenceEqual(GarnetJournalProtocol.TrueValue))
                {
                    api.HashSet(bucketKey, PinnedSpanByte.FromPinnedSpan(GarnetJournalProtocol.BucketChainField), extra, out _);
                }
            }
        }
        else if (operation.ReadOnlySpan.SequenceEqual(GarnetJournalProtocol.SealOperation))
        {
            alreadyApplied = bucket.IsSealed && currentVersion == requestedVersion;
            applies = bucket.IsOpen && currentVersion < requestedVersion;
            if (applies)
            {
                // Filters are written in the seal's transaction, so they cover every record the sealed copy will ever hold.
                var filter = extra.IsValid && extra.ReadOnlySpan.SequenceEqual(GarnetJournalProtocol.TrueValue)
                    && (bucket.IsDone || !bucket.Chain.IsEmpty)
                    ? CreateFilter(api, indexKey, markerKey, bucket.IsDone ? default : bucket.Chain)
                    : null;
                api.HashSet(bucketKey,
                [
                    (PinnedSpanByte.FromPinnedSpan(GarnetJournalProtocol.BucketStateField), PinnedSpanByte.FromPinnedSpan(GarnetJournalProtocol.SealedState)),
                    (PinnedSpanByte.FromPinnedSpan(GarnetJournalProtocol.BucketVersionField), version),
                ], out _);
                if (filter is not null)
                {
                    api.HashSet(bucketKey, PinnedSpanByte.FromPinnedSpan(GarnetJournalProtocol.BucketFilterField), CreateArgSlice(filter), out _);
                }
            }
        }
        else if (operation.ReadOnlySpan.SequenceEqual(GarnetJournalProtocol.ReclaimOperation))
        {
            // Only the tenure sealed at the requested version is reclaimed, so a stale request cannot reclaim a later tenure.
            alreadyApplied = bucket.IsSealed && bucket.IsReclaimed && currentVersion == requestedVersion;
            applies = bucket.IsSealed && !bucket.IsReclaimed && currentVersion == requestedVersion;
            if (applies)
            {
                api.HashSet(bucketKey,
                [
                    (PinnedSpanByte.FromPinnedSpan(GarnetJournalProtocol.BucketReclaimedField), PinnedSpanByte.FromPinnedSpan(GarnetJournalProtocol.TrueValue)),
                ], out _);
                if (!bucket.Filter.IsEmpty)
                {
                    api.HashDelete(bucketKey, PinnedSpanByte.FromPinnedSpan(GarnetJournalProtocol.BucketFilterField), out _);
                }
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
            response.WriteBulk(bucket.IsReclaimed ? GarnetJournalProtocol.TrueValue : GarnetJournalProtocol.FalseValue);
        }

        response.CopyTo(ref output);
    }

    /// <summary>
    /// Creates the key filters a sealed copy writes: one over every journal it holds a record of, followed by its own
    /// chain's filters, <paramref name="chain"/>, if its migration is not done.
    /// </summary>
    private static byte[] CreateFilter<TGarnetApi>(TGarnetApi api, PinnedSpanByte indexKey, PinnedSpanByte markerKey, ReadOnlySpan<byte> chain)
        where TGarnetApi : IGarnetReadApi
    {
        var live = api.SetMembers(indexKey, out PinnedSpanByte[] index) == GarnetStatus.OK ? index : [];
        var markers = api.SetMembers(markerKey, out PinnedSpanByte[] marked) == GarnetStatus.OK ? marked : [];
        var count = live.Length + markers.Length;
        var filter = new byte[GarnetJournalKeyFilter.GetEncodedLength(count) + chain.Length];
        var bits = GarnetJournalKeyFilter.Begin(filter, count);
        foreach (var member in live)
        {
            GarnetJournalKeyFilter.Add(bits, member.ReadOnlySpan);
        }

        foreach (var member in markers)
        {
            GarnetJournalKeyFilter.Add(bits, member.ReadOnlySpan);
        }

        chain.CopyTo(filter.AsSpan(filter.Length - chain.Length));
        return filter;
    }

    private static bool IsEmpty<TGarnetApi>(TGarnetApi api, PinnedSpanByte key)
        where TGarnetApi : IGarnetReadApi
        => api.SetLength(key, out var count) != GarnetStatus.OK || count == 0;
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
    /// <param name="server">The server.</param>
    /// <param name="load">The partition's load counters, which the journal procedures update and <see cref="GarnetJournalProtocol.Load"/> reports.</param>
    public static void Register(global::Garnet.GarnetServer server, GarnetJournalLoad load)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(load);
        Func<CustomTransactionProcedure>[] factories =
        [
            () => new GarnetJournalReadProcedure { Load = load },
            () => new GarnetJournalGetMetadataProcedure { Load = load },
            () => new GarnetJournalCreateProcedure { Load = load },
            () => new GarnetJournalAppendProcedure { Load = load },
            () => new GarnetJournalReplaceProcedure { Load = load },
            () => new GarnetJournalDeleteProcedure { Load = load },
            () => new GarnetJournalUpdateMetadataProcedure { Load = load },
            static () => new GarnetJournalBucketProcedure(),
            static () => new GarnetJournalIndexProcedure(),
            static () => new GarnetJournalPullReadProcedure(),
            static () => new GarnetJournalInstallProcedure(),
            static () => new GarnetJournalDoneProcedure(),
            static () => new GarnetJournalLinkProcedure(),
            static () => new GarnetJournalPurgeProcedure(),
            static () => new GarnetJournalDropMarkerProcedure(),
            static () => new GarnetJournalBucketInfoProcedure(),
        ];

        if (factories.Length != GarnetJournalProtocol.Names.Length)
        {
            throw new InvalidOperationException("Every journal procedure name requires a procedure.");
        }

        for (var i = 0; i < factories.Length; i++)
        {
            var id = server.Register.NewTransactionProc(GarnetJournalProtocol.Names[i], factories[i]);
            if (id != i)
            {
                throw new InvalidOperationException(
                    $"Garnet assigned id {id} to journal procedure '{GarnetJournalProtocol.Names[i]}'; expected {i}. Journal procedures must be registered first, in a fixed order.");
            }
        }

        // The load report is not logged, so its id is not persisted.
        server.Register.NewProcedure(GarnetJournalProtocol.Load, () => new GarnetJournalLoadProcedure(load));
    }
}
