using System.Buffers.Text;
using Garnet.common;
using Garnet.server;
using Tsavorite.core;

namespace Orleans.Journaling;

/// <summary>
/// Reads a journal's record from a sealed copy of its bucket, for a pull or consolidation. Arguments: journal keys.
/// Returns <c>[1, kind, done, source, metadataValueCount, metadata..., segments...]</c>, where <c>kind</c> is
/// <c>live</c>, <c>tomb</c>, <c>absent</c>, or <c>none</c>, and <c>done</c> and <c>source</c> describe the copy's own
/// migration, so the reader knows whether to continue along the chain. Returns <c>WrongPartition</c> if the partition
/// does not hold the bucket sealed, or has reclaimed it: reclaimed data reads as an error, never as absent.
/// </summary>
/// <remarks>A sealed copy never changes until it is reclaimed, so the record read is stable.</remarks>
internal sealed class GarnetJournalPullReadProcedure : GarnetJournalProcedure
{
    protected override bool LocksData => true;

    protected override bool IsWrite => false;

    public override void Main<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
    {
        var bucket = BucketRecord.Load(api, BucketKey(ref procInput));
        if (!bucket.IsSealed || bucket.IsReclaimed)
        {
            WriteWrongPartition(bucket, ref output);
            return;
        }

        var journal = JournalRecord.Load(api, MetadataKey(ref procInput));
        var response = new GarnetRespArrayWriter();
        response.WriteBulk(GarnetJournalProtocol.SuccessStatus);
        response.WriteBulk(
            !journal.Exists ? GarnetJournalProtocol.NoneKindValue
            : journal.IsTombstone ? GarnetJournalProtocol.TombstoneKindValue
            : journal.IsAbsentMarker ? GarnetJournalProtocol.AbsentKindValue
            : GarnetJournalProtocol.LiveKindValue);
        response.WriteBulk(bucket.IsDone ? GarnetJournalProtocol.TrueValue : GarnetJournalProtocol.FalseValue);
        response.WriteBulk(bucket.Source);
        if (journal.Exists && api.HashGetAll(MetadataKey(ref procInput), out PinnedSpanByte[] metadata) == GarnetStatus.OK)
        {
            response.WriteBulk(metadata.Length);
            foreach (var value in metadata)
            {
                response.WriteBulk(value.ReadOnlySpan);
            }
        }
        else
        {
            response.WriteBulk(0);
        }

        if (journal.IsLive)
        {
            WriteSegments(api, DataKey(ref procInput), response);
        }

        response.CopyTo(ref output);
    }
}

/// <summary>
/// Installs a journal's record, read from the bucket's migration chain, if this partition holds no record of it.
/// Arguments: journal keys, the bucket version the record was read for, kind (<c>live</c>, <c>tomb</c>, <c>absent</c>,
/// or <c>none</c>), metadata value count, metadata fields and values, segment count, segments.
/// Returns <c>[1]</c> if installed, <c>[0]</c> if not needed (the partition holds a record, or the bucket's migration
/// is done), or <c>WrongPartition</c> if the partition does not hold the bucket open at that version.
/// </summary>
/// <remarks>
/// <para>
/// The install is conditional on the partition holding no record, so it never overwrites a newer write, and on the
/// bucket's version, so a record read for an earlier tenure of the bucket is never installed in a later one, whose
/// chain may hold newer data. A chain which holds no record installs as an absent marker, which reads as absent and
/// stops later reads from consulting the chain.
/// </para>
/// <para>
/// Live journals join the bucket's index; tombstones and absent markers join its marker set, from which they are
/// dropped once the bucket's migration is done (D5) and purged when the bucket is reclaimed.
/// </para>
/// </remarks>
internal sealed class GarnetJournalInstallProcedure : GarnetJournalProcedure
{
    protected override bool LocksData => true;

    protected override bool IsWrite => true;

    public override void Main<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
    {
        var bucket = BucketRecord.Load(api, BucketKey(ref procInput));
        var expectedVersion = GetArg(ref procInput, FirstOperationArg);
        if (!bucket.IsOpen || !bucket.Version.SequenceEqual(expectedVersion.ReadOnlySpan))
        {
            WriteWrongPartition(bucket, ref output);
            return;
        }

        var journal = JournalRecord.Load(api, MetadataKey(ref procInput));
        if (bucket.IsDone || journal.Exists)
        {
            WriteStatus(ref output, GarnetJournalProtocol.MissingStatus);
            return;
        }

        var kind = GetArg(ref procInput, FirstOperationArg + 1).ReadOnlySpan;
        var index = FirstOperationArg + 2;
        if (!Utf8Parser.TryParse(GetArg(ref procInput, index++).ReadOnlySpan, out int metadataCount, out _)
            || metadataCount < 0 || metadataCount % 2 != 0)
        {
            WriteStatus(ref output, GarnetJournalProtocol.InvalidMetadataStatus);
            return;
        }

        var metadataStart = index;
        index += metadataCount;
        if (!Utf8Parser.TryParse(GetArg(ref procInput, index++).ReadOnlySpan, out int segmentCount, out _) || segmentCount < 0)
        {
            WriteStatus(ref output, GarnetJournalProtocol.InvalidMetadataStatus);
            return;
        }

        var live = kind.SequenceEqual(GarnetJournalProtocol.LiveKindValue);
        var tombstone = kind.SequenceEqual(GarnetJournalProtocol.TombstoneKindValue);
        var absent = kind.SequenceEqual(GarnetJournalProtocol.AbsentKindValue) || kind.SequenceEqual(GarnetJournalProtocol.NoneKindValue);
        if (!live && !tombstone && !absent || (live || tombstone) && metadataCount == 0 || !live && segmentCount > 0)
        {
            WriteStatus(ref output, GarnetJournalProtocol.InvalidMetadataStatus);
            return;
        }

        var member = Member(ref procInput);
        if (absent)
        {
            api.HashSet(MetadataKey(ref procInput), [(Pinned(GarnetJournalProtocol.AbsentField), Pinned(GarnetJournalProtocol.TrueValue))], out _);
            api.SetAdd(MarkerKey(ref procInput), member, out _);
            WriteStatus(ref output, GarnetJournalProtocol.SuccessStatus);
            return;
        }

        var fields = new (PinnedSpanByte, PinnedSpanByte)[metadataCount / 2];
        for (var i = 0; i < fields.Length; i++)
        {
            fields[i] = (GetArg(ref procInput, metadataStart + 2 * i), GetArg(ref procInput, metadataStart + 2 * i + 1));
        }

        api.HashSet(MetadataKey(ref procInput), fields, out _);
        if (live)
        {
            if (segmentCount > 0)
            {
                var segments = new PinnedSpanByte[segmentCount];
                for (var i = 0; i < segmentCount; i++)
                {
                    segments[i] = GetArg(ref procInput, index + i);
                }

                api.ListRightPush(DataKey(ref procInput), segments, out _, whenExists: false);
            }

            api.SetAdd(IndexKey(ref procInput), member, out _);
        }
        else
        {
            api.SetAdd(MarkerKey(ref procInput), member, out _);
        }

        WriteStatus(ref output, GarnetJournalProtocol.SuccessStatus);
    }
}

/// <summary>
/// Drops absent markers, and tombstones of journals deleted at or before a cutoff, from a bucket whose migration is
/// done. Arguments: bucket key, index key, marker key, cutoff (Unix milliseconds), then the metadata key and encoded id
/// of each journal to consider. Returns <c>[1, dropped]</c>, or <c>WrongPartition</c> if the partition does not hold the
/// bucket open with its migration done.
/// </summary>
/// <remarks>
/// <para>
/// Reads stop at a bucket whose migration is done, so its markers no longer stop reads from consulting the chain. A
/// tombstone also holds the ETags which let a retried mutation recognize that it committed (D3) and names the absence
/// after its deletion (D16). Dropping it restores the empty absence, so the procedure raises the bucket's collection
/// generation past the generation stamped on each tombstone it drops, and writes conditioned on the empty absence
/// observed at an earlier generation are rejected (D25). An absent marker holds no ETags and names the empty absence
/// already.
/// </para>
/// <para>
/// The bucket's exclusive lock orders the generation with every operation on the bucket. The caller passes a cutoff
/// which retains tombstones for <see cref="GarnetJournalStorageOptions.TombstoneRetention"/>, so retries within it
/// still report their committed outcome rather than an ambiguous conflict. The cutoff affects only when tombstones are
/// dropped, never which retries apply.
/// </para>
/// </remarks>
internal sealed class GarnetJournalDropMarkerProcedure : GarnetJournalBucketSetProcedure
{
    public override bool Prepare<TGarnetReadApi>(TGarnetReadApi api, ref CustomProcedureInput procInput)
    {
        if (!LockBucketKeys(ref procInput, LockType.Exclusive, LockType.Exclusive))
        {
            return false;
        }

        var offset = 4;
        while (true)
        {
            var metadataKey = GetNextArg(ref procInput, ref offset);
            var member = GetNextArg(ref procInput, ref offset);
            if (!metadataKey.IsValid)
            {
                return true;
            }

            if (!member.IsValid)
            {
                return false;
            }

            AddKey(metadataKey, LockType.Exclusive, StoreType.Object);
        }
    }

    public override void Main<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
    {
        var offset = 0;
        var bucketKey = GetNextArg(ref procInput, ref offset);
        _ = GetNextArg(ref procInput, ref offset);
        var markerKey = GetNextArg(ref procInput, ref offset);
        var cutoff = GetNextArg(ref procInput, ref offset);
        var bucket = BucketRecord.Load(api, bucketKey);
        var response = new GarnetRespArrayWriter();
        if (!bucket.IsOpen || !bucket.IsDone)
        {
            response.WriteBulk(GarnetJournalProtocol.WrongPartitionStatus);
            response.WriteBulk(bucket.Exists ? bucket.State : GarnetJournalProtocol.AbsentState);
            response.WriteBulk(bucket.Exists ? bucket.Version : GarnetJournalProtocol.ZeroValue);
            response.CopyTo(ref output);
            return;
        }

        if (!GarnetJournalProtocol.TryParseNonNegativeCanonical(cutoff.ReadOnlySpan, out var cutoffValue))
        {
            response.WriteBulk(GarnetJournalProtocol.InvalidMetadataStatus);
            response.CopyTo(ref output);
            return;
        }

        // Decide every drop before the first write.
        var generation = bucket.CollectionGeneration;
        var drops = new List<(PinnedSpanByte MetadataKey, PinnedSpanByte Member)>();
        while (true)
        {
            var metadataKey = GetNextArg(ref procInput, ref offset);
            var member = GetNextArg(ref procInput, ref offset);
            if (!metadataKey.IsValid)
            {
                break;
            }

            var journal = JournalRecord.Load(api, metadataKey);
            if (journal.IsAbsentMarker)
            {
                drops.Add((metadataKey, member));
            }
            else if (journal.IsTombstone
                && GarnetJournalProtocol.TryParseNonNegativeCanonical(journal.DeletedAt, out var deletedAt)
                && deletedAt <= cutoffValue
                && GarnetJournalProtocol.TryParseNonNegativeCanonical(journal.DeletedGeneration, out var deletedGeneration))
            {
                drops.Add((metadataKey, member));
                generation = Math.Max(generation, deletedGeneration + 1);
            }
        }

        if (generation > bucket.CollectionGeneration)
        {
            api.HashSet(bucketKey, PinnedSpanByte.FromPinnedSpan(GarnetJournalProtocol.BucketCollectedField), CreateArgSlice(GarnetJournalProtocol.FormatInt64(generation)), out _);
        }

        foreach (var (metadataKey, member) in drops)
        {
            api.DELETE(metadataKey);
            api.SetRemove(markerKey, member, out _);
        }

        response.WriteBulk(GarnetJournalProtocol.SuccessStatus);
        response.WriteBulk(drops.Count);
        response.CopyTo(ref output);
    }
}

/// <summary>
/// Marks a bucket's migration done once the partition holds a record of every journal on the bucket's migration chain.
/// Arguments: bucket key, the bucket version, the metadata keys of the journals on the chain.
/// Returns <c>[1]</c>, <c>[-1, missingCount]</c> if the partition lacks records, or <c>WrongPartition</c> if the
/// partition does not hold the bucket open at that version.
/// </summary>
/// <remarks>
/// The bucket's exclusive lock orders this check after every operation and install on the bucket, so the recheck is
/// atomic with setting <c>done</c>. Journals the chain holds no record of read as absent either way.
/// </remarks>
internal sealed class GarnetJournalDoneProcedure : CustomTransactionProcedure
{
    public override TimeSpan KeyLockTimeout => TimeSpan.FromSeconds(10);

    public override bool Prepare<TGarnetReadApi>(TGarnetReadApi api, ref CustomProcedureInput procInput)
    {
        var offset = 0;
        var bucketKey = GetNextArg(ref procInput, ref offset);
        var version = GetNextArg(ref procInput, ref offset);
        if (!bucketKey.IsValid || !version.IsValid)
        {
            return false;
        }

        AddKey(bucketKey, LockType.Exclusive, StoreType.Object);
        for (var key = GetNextArg(ref procInput, ref offset); key.IsValid; key = GetNextArg(ref procInput, ref offset))
        {
            AddKey(key, LockType.Shared, StoreType.Object);
        }

        return true;
    }

    public override void Main<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
    {
        var offset = 0;
        var bucketKey = GetNextArg(ref procInput, ref offset);
        var version = GetNextArg(ref procInput, ref offset);
        var bucket = BucketRecord.Load(api, bucketKey);
        var response = new GarnetRespArrayWriter();
        if (!bucket.IsOpen || !bucket.Version.SequenceEqual(version.ReadOnlySpan))
        {
            response.WriteBulk(GarnetJournalProtocol.WrongPartitionStatus);
            response.WriteBulk(bucket.Exists ? bucket.State : GarnetJournalProtocol.AbsentState);
            response.WriteBulk(bucket.Exists ? bucket.Version : GarnetJournalProtocol.ZeroValue);
            response.CopyTo(ref output);
            return;
        }

        if (!bucket.IsDone)
        {
            var missing = 0;
            for (var key = GetNextArg(ref procInput, ref offset); key.IsValid; key = GetNextArg(ref procInput, ref offset))
            {
                if (api.HashLength(key, out var count) != GarnetStatus.OK || count == 0)
                {
                    missing++;
                }
            }

            if (missing > 0)
            {
                response.WriteBulk(GarnetJournalProtocol.ConflictStatus);
                response.WriteBulk(missing);
                response.CopyTo(ref output);
                return;
            }

            api.HashSet(bucketKey, PinnedSpanByte.FromPinnedSpan(GarnetJournalProtocol.BucketDoneField), PinnedSpanByte.FromPinnedSpan(GarnetJournalProtocol.TrueValue), out _);
        }

        response.WriteBulk(GarnetJournalProtocol.SuccessStatus);
        response.CopyTo(ref output);
    }
}

/// <summary>
/// Lists a sealed copy of a bucket, as one link of a migration chain. Arguments: bucket key, index key, marker key.
/// Returns <c>[1, done, source, members...]</c>, the encoded ids of every journal the copy holds a record of (live
/// journals, tombstones, and absent markers), or <c>WrongPartition</c> if the partition does not hold the bucket sealed
/// or has reclaimed it.
/// </summary>
internal sealed class GarnetJournalLinkProcedure : GarnetJournalBucketSetProcedure
{
    public override bool Prepare<TGarnetReadApi>(TGarnetReadApi api, ref CustomProcedureInput procInput)
        => LockBucketKeys(ref procInput, LockType.Shared, LockType.Shared);

    public override void Main<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
    {
        var offset = 0;
        var bucketKey = GetNextArg(ref procInput, ref offset);
        var indexKey = GetNextArg(ref procInput, ref offset);
        var markerKey = GetNextArg(ref procInput, ref offset);
        var bucket = BucketRecord.Load(api, bucketKey);
        var response = new GarnetRespArrayWriter();
        if (!bucket.IsSealed || bucket.IsReclaimed)
        {
            response.WriteBulk(GarnetJournalProtocol.WrongPartitionStatus);
            response.WriteBulk(bucket.Exists ? bucket.State : GarnetJournalProtocol.AbsentState);
            response.WriteBulk(bucket.Exists ? bucket.Version : GarnetJournalProtocol.ZeroValue);
            response.CopyTo(ref output);
            return;
        }

        response.WriteBulk(GarnetJournalProtocol.SuccessStatus);
        response.WriteBulk(bucket.IsDone ? GarnetJournalProtocol.TrueValue : GarnetJournalProtocol.FalseValue);
        response.WriteBulk(bucket.Source);
        WriteMembers(api, indexKey, response);
        WriteMembers(api, markerKey, response);
        response.CopyTo(ref output);
    }
}

/// <summary>
/// Deletes journals from a reclaimed copy of a bucket. Arguments: bucket key, index key, marker key, the version which
/// sealed the copy, then the metadata key, data key, and encoded id of each journal. Returns <c>[1]</c>, or
/// <c>WrongPartition</c> if the partition does not hold the bucket sealed at that version and reclaimed.
/// </summary>
/// <remarks>
/// Every read of a reclaimed copy fails, so deleting its journals in batches is not observable. The bucket reopens
/// only once its index and marker set are empty (D11). Each batch names the tenure it purges, so a delayed or
/// duplicated batch applies to no later tenure of the bucket, whatever its state.
/// </remarks>
internal sealed class GarnetJournalPurgeProcedure : GarnetJournalBucketSetProcedure
{
    public override bool Prepare<TGarnetReadApi>(TGarnetReadApi api, ref CustomProcedureInput procInput)
    {
        if (!LockBucketKeys(ref procInput, LockType.Shared, LockType.Exclusive))
        {
            return false;
        }

        var offset = 4;
        while (true)
        {
            var metadataKey = GetNextArg(ref procInput, ref offset);
            var dataKey = GetNextArg(ref procInput, ref offset);
            var member = GetNextArg(ref procInput, ref offset);
            if (!metadataKey.IsValid)
            {
                return true;
            }

            if (!dataKey.IsValid || !member.IsValid)
            {
                return false;
            }

            AddKey(metadataKey, LockType.Exclusive, StoreType.Object);
            AddKey(dataKey, LockType.Exclusive, StoreType.Object);
        }
    }

    public override void Main<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
    {
        var offset = 0;
        var bucketKey = GetNextArg(ref procInput, ref offset);
        var indexKey = GetNextArg(ref procInput, ref offset);
        var markerKey = GetNextArg(ref procInput, ref offset);
        var sealVersion = GetNextArg(ref procInput, ref offset);
        var bucket = BucketRecord.Load(api, bucketKey);
        var response = new GarnetRespArrayWriter();
        if (!bucket.IsSealed || !bucket.IsReclaimed || !bucket.Version.SequenceEqual(sealVersion.ReadOnlySpan))
        {
            response.WriteBulk(GarnetJournalProtocol.WrongPartitionStatus);
            response.WriteBulk(bucket.Exists ? bucket.State : GarnetJournalProtocol.AbsentState);
            response.WriteBulk(bucket.Exists ? bucket.Version : GarnetJournalProtocol.ZeroValue);
            response.CopyTo(ref output);
            return;
        }

        while (true)
        {
            var metadataKey = GetNextArg(ref procInput, ref offset);
            var dataKey = GetNextArg(ref procInput, ref offset);
            var member = GetNextArg(ref procInput, ref offset);
            if (!metadataKey.IsValid)
            {
                break;
            }

            api.DELETE(metadataKey);
            api.DELETE(dataKey);
            api.SetRemove(indexKey, member, out _);
            api.SetRemove(markerKey, member, out _);
        }

        response.WriteBulk(GarnetJournalProtocol.SuccessStatus);
        response.CopyTo(ref output);
    }
}

/// <summary>
/// Reads a bucket's state in this partition. Arguments: bucket key, index key, marker key.
/// Returns <c>[1, state, version, source, done, reclaimed, indexCount, markerCount, collected]</c>, where <c>state</c> is
/// <c>absent</c>, <c>open</c>, or <c>sealed</c>, and <c>collected</c> is the collection generation.
/// </summary>
internal sealed class GarnetJournalBucketInfoProcedure : GarnetJournalBucketSetProcedure
{
    public override bool Prepare<TGarnetReadApi>(TGarnetReadApi api, ref CustomProcedureInput procInput)
        => LockBucketKeys(ref procInput, LockType.Shared, LockType.Shared);

    public override void Main<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
    {
        var offset = 0;
        var bucketKey = GetNextArg(ref procInput, ref offset);
        var indexKey = GetNextArg(ref procInput, ref offset);
        var markerKey = GetNextArg(ref procInput, ref offset);
        var bucket = BucketRecord.Load(api, bucketKey);
        api.SetLength(indexKey, out var indexCount);
        api.SetLength(markerKey, out var markerCount);
        var response = new GarnetRespArrayWriter();
        response.WriteBulk(GarnetJournalProtocol.SuccessStatus);
        response.WriteBulk(bucket.Exists ? bucket.State : GarnetJournalProtocol.AbsentState);
        response.WriteBulk(bucket.Exists ? bucket.Version : GarnetJournalProtocol.ZeroValue);
        response.WriteBulk(bucket.Exists ? bucket.Source : "-"u8);
        response.WriteBulk(bucket.IsDone ? GarnetJournalProtocol.TrueValue : GarnetJournalProtocol.FalseValue);
        response.WriteBulk(bucket.IsReclaimed ? GarnetJournalProtocol.TrueValue : GarnetJournalProtocol.FalseValue);
        response.WriteBulk(indexCount);
        response.WriteBulk(markerCount);
        response.WriteBulk(bucket.CollectionGeneration);
        response.CopyTo(ref output);
    }
}

/// <summary>Base class for procedures whose first arguments are a bucket key, its index key, and its marker key.</summary>
internal abstract class GarnetJournalBucketSetProcedure : CustomTransactionProcedure
{
    public override TimeSpan KeyLockTimeout => TimeSpan.FromSeconds(10);

    protected bool LockBucketKeys(ref CustomProcedureInput procInput, LockType bucketLock, LockType setLock)
    {
        var offset = 0;
        var bucketKey = GetNextArg(ref procInput, ref offset);
        var indexKey = GetNextArg(ref procInput, ref offset);
        var markerKey = GetNextArg(ref procInput, ref offset);
        if (!bucketKey.IsValid || !indexKey.IsValid || !markerKey.IsValid)
        {
            return false;
        }

        AddKey(bucketKey, bucketLock, StoreType.Object);
        AddKey(indexKey, setLock, StoreType.Object);
        AddKey(markerKey, setLock, StoreType.Object);
        return true;
    }

    protected static void WriteMembers<TGarnetApi>(TGarnetApi api, PinnedSpanByte key, GarnetRespArrayWriter response)
        where TGarnetApi : IGarnetReadApi
    {
        if (api.SetMembers(key, out PinnedSpanByte[] members) == GarnetStatus.OK)
        {
            foreach (var member in members)
            {
                response.WriteBulk(member.ReadOnlySpan);
            }
        }
    }
}
