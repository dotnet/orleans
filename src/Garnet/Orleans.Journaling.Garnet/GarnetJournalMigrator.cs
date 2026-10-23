using System.Globalization;
using Orleans.Journaling.Ranges;
using StackExchange.Redis;

namespace Orleans.Journaling;

/// <summary>
/// Runs the migration procedures: pulls, consolidation, bucket transitions, reclamation, and marker cleanup.
/// </summary>
/// <remarks>
/// <para>
/// A bucket's migration chain starts at the partition from which its current owner opened it, and continues through
/// each partition's own source while that partition's migration of the bucket was not done. Every partition on the
/// chain holds the bucket sealed, so the chain's records never change until they are reclaimed, and a read of a
/// reclaimed copy fails rather than reading as absent.
/// </para>
/// <para>
/// Every method is safe to run concurrently with itself and to repeat: installs are conditional, transitions report
/// success if already applied, and marking a migration done rechecks its condition.
/// </para>
/// </remarks>
internal sealed class GarnetJournalMigrator(GarnetJournalRouter router)
{
    /// <summary>The default bound on the number of partitions a migration chain spans.</summary>
    public const int DefaultMaxChainLength = 64;

    private const int DropBatchSize = 256;

    /// <summary>
    /// Gets or sets the maximum number of partitions a read follows along a bucket's migration chain. The coordinator
    /// refuses a move which would lengthen a chain beyond it, so every chain stays traversable.
    /// </summary>
    public int MaxChainLength { get; set; } = DefaultMaxChainLength;
    private const int PurgeBatchSize = 256;
    private const int ConsolidationConcurrency = 16;

    private string MapId => router.MapId;

    /// <summary>
    /// Pulls a journal into <paramref name="destination"/>, which answered <c>NeedPull(source, version)</c> for it.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if the destination now holds a record of the journal or has completed its migration;
    /// <see langword="false"/> if the pull could not complete, for example because a partition on the chain is
    /// unavailable or the destination's tenure of the bucket ended, in which case the caller retries.
    /// </returns>
    public async Task<bool> TryPullAsync(GarnetJournalKeySet keys, PartitionId destination, RedisResult[] needPull, CancellationToken cancellationToken)
    {
        if (needPull.Length < 3 || needPull[1].ToString() is not { Length: > 0 } source || needPull[2].ToString() is not { Length: > 0 } version)
        {
            throw new InvalidOperationException("A Garnet journal procedure returned a malformed NeedPull response.");
        }

        if (source == GarnetJournalProtocol.NoSource)
        {
            throw new InvalidOperationException($"Garnet journal partition '{destination}' holds bucket {keys.Bucket} open with an incomplete migration but no source.");
        }

        var record = await TryReadChainAsync(keys, new PartitionId(source), cancellationToken).ConfigureAwait(false);
        return record is not null && await TryInstallAsync(keys, destination, version, record, cancellationToken).ConfigureAwait(false) is not null;
    }

    /// <summary>
    /// Reads a journal's record from a migration chain which starts at <paramref name="source"/>, newest first.
    /// </summary>
    /// <returns>
    /// The first record found, or <see cref="ChainRecord.None"/> if the chain holds none; <see langword="null"/> if a
    /// partition on the chain was unreachable or no longer holds its sealed copy.
    /// </returns>
    public async Task<ChainRecord?> TryReadChainAsync(GarnetJournalKeySet keys, PartitionId source, CancellationToken cancellationToken)
    {
        var args = new object[GarnetJournalKeySet.Count];
        keys.WriteTo(args);
        var partition = source;
        for (var length = 0; length < MaxChainLength; length++)
        {
            var result = await router.TryExecuteAtAsync(partition, GarnetJournalProtocol.PullRead, args, cancellationToken).ConfigureAwait(false);
            if (result is null || result.Length == 0 || GarnetJournalRouter.GetStatus(result) != GarnetJournalProtocol.SuccessStatus)
            {
                return null;
            }

            var kind = result[1].ToString();
            var done = result[2].ToString() == "1";
            var next = result[3].ToString();
            var metadataCount = int.Parse(result[4].ToString(), NumberStyles.None, CultureInfo.InvariantCulture);
            if (kind != GarnetJournalProtocol.NoneKind)
            {
                var metadata = new RedisValue[metadataCount];
                for (var i = 0; i < metadataCount; i++)
                {
                    metadata[i] = (RedisValue)result[5 + i];
                }

                var segments = new RedisValue[result.Length - 5 - metadataCount];
                for (var i = 0; i < segments.Length; i++)
                {
                    segments[i] = (RedisValue)result[5 + metadataCount + i];
                }

                return new ChainRecord(kind, metadata, segments);
            }

            if (done || next == GarnetJournalProtocol.NoSource)
            {
                return ChainRecord.None;
            }

            partition = new PartitionId(next);
        }

        throw new InvalidOperationException($"The migration chain of Garnet journal bucket {keys.Bucket} is longer than {MaxChainLength} partitions.");
    }

    /// <summary>
    /// Installs <paramref name="record"/> in <paramref name="destination"/> if it holds no record of the journal and
    /// holds the bucket open at <paramref name="version"/> with its migration incomplete.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if installed, <see langword="false"/> if not needed, or <see langword="null"/> if the
    /// destination does not hold the bucket open at that version or was unreachable.
    /// </returns>
    public async Task<bool?> TryInstallAsync(GarnetJournalKeySet keys, PartitionId destination, string version, ChainRecord record, CancellationToken cancellationToken)
    {
        var args = new object[GarnetJournalKeySet.Count + 4 + record.Metadata.Length + record.Segments.Length];
        keys.WriteTo(args);
        var index = GarnetJournalKeySet.Count;
        args[index++] = version;
        args[index++] = record.Kind;
        args[index++] = record.Metadata.Length.ToString(CultureInfo.InvariantCulture);
        foreach (var value in record.Metadata)
        {
            args[index++] = value;
        }

        args[index++] = record.Segments.Length.ToString(CultureInfo.InvariantCulture);
        foreach (var value in record.Segments)
        {
            args[index++] = value;
        }

        var result = await router.TryExecuteAtAsync(destination, GarnetJournalProtocol.Install, args, cancellationToken).ConfigureAwait(false);
        if (result is null || result.Length == 0)
        {
            return null;
        }

        return GarnetJournalRouter.GetStatus(result) switch
        {
            GarnetJournalProtocol.SuccessStatus => true,
            GarnetJournalProtocol.MissingStatus => false,
            GarnetJournalProtocol.WrongPartitionStatus => null,
            var status => throw new InvalidOperationException($"Installing a Garnet journal in partition '{destination}' failed with status {status}."),
        };
    }

    /// <summary>
    /// Lists every journal with a record on the migration chain which starts at <paramref name="source"/>: the union of
    /// each sealed copy's index and marker set, following sources while the copy's own migration was not done.
    /// </summary>
    /// <returns>The encoded ids, or <see langword="null"/> if a partition on the chain was unreachable or no longer holds its sealed copy.</returns>
    public async Task<HashSet<RedisValue>?> TryListChainAsync(int bucket, PartitionId source, CancellationToken cancellationToken)
    {
        var members = new HashSet<RedisValue>();
        var args = GarnetJournalKeySet.GetBucketArgs(MapId, bucket);
        var partition = source;
        for (var length = 0; length < MaxChainLength; length++)
        {
            var result = await router.TryExecuteAtAsync(partition, GarnetJournalProtocol.Link, args, cancellationToken).ConfigureAwait(false);
            if (result is null || result.Length == 0 || GarnetJournalRouter.GetStatus(result) != GarnetJournalProtocol.SuccessStatus)
            {
                return null;
            }

            for (var i = 3; i < result.Length; i++)
            {
                members.Add((RedisValue)result[i]);
            }

            var next = result[2].ToString();
            if (result[1].ToString() == "1" || next == GarnetJournalProtocol.NoSource)
            {
                return members;
            }

            partition = new PartitionId(next);
        }

        throw new InvalidOperationException($"The migration chain of Garnet journal bucket {bucket} is longer than {MaxChainLength} partitions.");
    }

    /// <summary>
    /// Consolidates <paramref name="bucket"/> into <paramref name="destination"/>: installs every journal on its migration
    /// chain which the destination holds no record of, then marks the migration done, rechecking that every journal is present.
    /// </summary>
    /// <returns>The outcome, and the number of journals installed.</returns>
    public async Task<(ConsolidationResult Result, int Installed)> ConsolidateAsync(int bucket, PartitionId destination, CancellationToken cancellationToken)
    {
        var state = await GetBucketAsync(destination, bucket, cancellationToken).ConfigureAwait(false);
        if (state.State != GarnetJournalProtocol.OpenStateName)
        {
            return (ConsolidationResult.NotOpen, 0);
        }

        if (state.Done)
        {
            return (ConsolidationResult.Done, 0);
        }

        var source = new PartitionId(state.Source);
        var members = await TryListChainAsync(bucket, source, cancellationToken).ConfigureAwait(false);
        if (members is null)
        {
            return (ConsolidationResult.Retry, 0);
        }

        var version = state.Version.ToString(CultureInfo.InvariantCulture);
        var all = members.Select(member => new GarnetJournalKeySet(MapId, bucket, (byte[])member!)).ToList();

        // Skip journals the destination already holds, for example because a pull installed them; the done check below
        // rechecks every journal under the bucket's lock.
        var missing = new List<GarnetJournalKeySet>();
        foreach (var batch in all.Chunk(PurgeBatchSize))
        {
            var exists = await Task.WhenAll(batch.Select(keys => router.ExecuteAtAsync(destination, "EXISTS", [keys.MetadataKey], cancellationToken).AsTask())).ConfigureAwait(false);
            for (var i = 0; i < batch.Length; i++)
            {
                if (exists[i].Length == 0 || (long)exists[i][0] == 0)
                {
                    missing.Add(batch[i]);
                }
            }
        }

        var installed = 0;
        var failed = false;
        await Parallel.ForEachAsync(
            missing,
            new ParallelOptions { MaxDegreeOfParallelism = ConsolidationConcurrency, CancellationToken = cancellationToken },
            async (keys, token) =>
            {
                var record = await TryReadChainAsync(keys, source, token).ConfigureAwait(false);
                var result = record is null ? null : await TryInstallAsync(keys, destination, version, record, token).ConfigureAwait(false);
                if (result is null)
                {
                    Volatile.Write(ref failed, true);
                }
                else if (result.Value)
                {
                    Interlocked.Increment(ref installed);
                }
            }).ConfigureAwait(false);
        if (failed)
        {
            return (ConsolidationResult.Retry, installed);
        }

        var done = await TryMarkDoneAsync(destination, bucket, version, all, cancellationToken).ConfigureAwait(false);
        return (done == true ? ConsolidationResult.Done : ConsolidationResult.Retry, installed);
    }

    /// <summary>Marks <paramref name="destination"/>'s migration of <paramref name="bucket"/> done if it holds a record of every journal in <paramref name="journals"/>.</summary>
    /// <returns><see langword="true"/> if done; <see langword="false"/> if records are missing; <see langword="null"/> if the destination does not hold the bucket open at <paramref name="version"/>.</returns>
    public async Task<bool?> TryMarkDoneAsync(PartitionId destination, int bucket, string version, IReadOnlyCollection<GarnetJournalKeySet> journals, CancellationToken cancellationToken)
    {
        var args = new object[2 + journals.Count];
        args[0] = (RedisKey)GarnetJournalKeys.GetBucketKey(MapId, bucket);
        args[1] = version;
        var index = 2;
        foreach (var keys in journals)
        {
            args[index++] = keys.MetadataKey;
        }

        var result = await router.ExecuteAtAsync(destination, GarnetJournalProtocol.Done, args, cancellationToken).ConfigureAwait(false);
        return GarnetJournalRouter.GetStatus(result) switch
        {
            GarnetJournalProtocol.SuccessStatus => true,
            GarnetJournalProtocol.ConflictStatus => false,
            _ => null,
        };
    }

    /// <summary>Reads a bucket's state in a partition.</summary>
    public async Task<BucketState> GetBucketAsync(PartitionId partition, int bucket, CancellationToken cancellationToken)
    {
        var result = await router.ExecuteAtAsync(partition, GarnetJournalProtocol.BucketInfo, GarnetJournalKeySet.GetBucketArgs(MapId, bucket), cancellationToken).ConfigureAwait(false);
        return ParseBucketState(result);
    }

    private static BucketState ParseBucketState(RedisResult[] result)
    {
        return new BucketState(
            result[1].ToString(),
            long.Parse(result[2].ToString(), NumberStyles.None, CultureInfo.InvariantCulture),
            result[3].ToString(),
            result[4].ToString() == "1",
            result[5].ToString() == "1",
            (long)result[6],
            (long)result[7],
            long.Parse(result[8].ToString(), NumberStyles.None, CultureInfo.InvariantCulture));
    }

    /// <summary>Reads a bucket's state in a partition with one attempt.</summary>
    /// <returns>The state, or <see langword="null"/> if the partition is unavailable.</returns>
    public async Task<BucketState?> TryGetBucketAsync(PartitionId partition, int bucket, CancellationToken cancellationToken)
    {
        try
        {
            var result = await router.TryExecuteAtAsync(partition, GarnetJournalProtocol.BucketInfo, GarnetJournalKeySet.GetBucketArgs(MapId, bucket), cancellationToken).ConfigureAwait(false);
            return result is null ? null : ParseBucketState(result);
        }
        catch (Exception exception) when (GarnetJournalRouter.IsTransient(exception))
        {
            return null;
        }
    }

    /// <summary>Applies a bucket transition (<c>open</c>, <c>seal</c>, or <c>reclaim</c>) in a partition.</summary>
    /// <param name="collectionGeneration">For <c>open</c>, the source's collection generation, which the bucket inherits (D25).</param>
    /// <returns><see langword="true"/> if the transition applied or had already been applied.</returns>
    public async Task<bool> TransitionAsync(PartitionId partition, int bucket, string operation, long version, PartitionId? source, CancellationToken cancellationToken, long collectionGeneration = 0)
    {
        var args = GarnetJournalKeySet.GetBucketArgs(
            MapId,
            bucket,
            operation,
            version.ToString(CultureInfo.InvariantCulture),
            source?.Value ?? GarnetJournalProtocol.NoSource,
            source is null ? "1" : "0",
            collectionGeneration.ToString(CultureInfo.InvariantCulture));
        var result = await router.ExecuteAtAsync(partition, GarnetJournalProtocol.Bucket, args, cancellationToken).ConfigureAwait(false);
        return GarnetJournalRouter.GetStatus(result) == GarnetJournalProtocol.SuccessStatus;
    }

    /// <summary>
    /// Reclaims a partition's copy of a bucket, sealed at <paramref name="sealVersion"/>: marks it reclaimed, so every
    /// later read of it fails, then deletes its journals, tombstones, and absent markers.
    /// </summary>
    /// <returns>
    /// The number of records purged, or -1 if the partition does not hold the bucket sealed at that version, which
    /// means that copy was reclaimed and the partition has since opened the bucket again.
    /// </returns>
    public async Task<int> ReclaimAsync(PartitionId partition, int bucket, long sealVersion, CancellationToken cancellationToken)
    {
        if (!await TransitionAsync(partition, bucket, GarnetJournalProtocol.BucketReclaimOperation, sealVersion, source: null, cancellationToken).ConfigureAwait(false))
        {
            return -1;
        }

        var purged = 0;
        while (true)
        {
            var members = new HashSet<RedisValue>();
            foreach (var key in new[] { GarnetJournalKeys.GetIndexKey(bucket), GarnetJournalKeys.GetMarkerKey(bucket) })
            {
                foreach (var member in await router.ExecuteAtAsync(partition, "SMEMBERS", [(RedisKey)key], cancellationToken).ConfigureAwait(false))
                {
                    members.Add((RedisValue)member);
                }
            }

            if (members.Count == 0)
            {
                return purged;
            }

            foreach (var batch in members.Chunk(PurgeBatchSize))
            {
                if (!await TryPurgeAsync(partition, bucket, sealVersion, batch, cancellationToken).ConfigureAwait(false))
                {
                    return -1;
                }

                purged += batch.Length;
            }
        }
    }

    /// <summary>
    /// Deletes a batch of journals, given by encoded id, from a partition's reclaimed copy of a bucket sealed at
    /// <paramref name="sealVersion"/>. The batch names that version, so a delayed batch purges no later tenure (D47).
    /// </summary>
    /// <returns><see langword="true"/> if the batch applied; <see langword="false"/> if the partition does not hold the bucket sealed at that version and reclaimed.</returns>
    public async Task<bool> TryPurgeAsync(PartitionId partition, int bucket, long sealVersion, IReadOnlyList<RedisValue> members, CancellationToken cancellationToken)
    {
        var args = new object[4 + members.Count * 3];
        GarnetJournalKeySet.GetBucketArgs(MapId, bucket).CopyTo(args, 0);
        args[3] = sealVersion.ToString(CultureInfo.InvariantCulture);
        for (var i = 0; i < members.Count; i++)
        {
            var keys = new GarnetJournalKeySet(MapId, bucket, (byte[])members[i]!);
            args[4 + 3 * i] = keys.MetadataKey;
            args[5 + 3 * i] = keys.DataKey;
            args[6 + 3 * i] = members[i];
        }

        var result = await router.ExecuteAtAsync(partition, GarnetJournalProtocol.Purge, args, cancellationToken).ConfigureAwait(false);
        return GarnetJournalRouter.GetStatus(result) == GarnetJournalProtocol.SuccessStatus;
    }

    /// <summary>
    /// Drops absent markers, and tombstones of journals deleted at or before <paramref name="cutoff"/>, from a bucket
    /// whose migration is done in <paramref name="partition"/>, in batches, raising the bucket's collection horizon.
    /// </summary>
    /// <returns>The number of markers dropped.</returns>
    public async Task<int> DropMarkersAsync(PartitionId partition, int bucket, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var members = await router.ExecuteAtAsync(partition, "SMEMBERS", [(RedisKey)GarnetJournalKeys.GetMarkerKey(bucket)], cancellationToken).ConfigureAwait(false);
        var dropped = 0;
        var cutoffValue = Math.Max(0, cutoff.ToUnixTimeMilliseconds()).ToString(CultureInfo.InvariantCulture);
        foreach (var batch in members.Chunk(DropBatchSize))
        {
            var args = new object[4 + batch.Length * 2];
            GarnetJournalKeySet.GetBucketArgs(MapId, bucket).CopyTo(args, 0);
            args[3] = cutoffValue;
            for (var i = 0; i < batch.Length; i++)
            {
                var keys = new GarnetJournalKeySet(MapId, bucket, (byte[])batch[i]!);
                args[4 + 2 * i] = keys.MetadataKey;
                args[5 + 2 * i] = (RedisValue)batch[i];
            }

            var result = await router.ExecuteAtAsync(partition, GarnetJournalProtocol.DropMarker, args, cancellationToken).ConfigureAwait(false);
            if (GarnetJournalRouter.GetStatus(result) != GarnetJournalProtocol.SuccessStatus)
            {
                // The bucket left this partition, or its migration is not done.
                break;
            }

            dropped += (int)result[1];
        }

        return dropped;
    }
}

/// <summary>A journal's record read from a migration chain.</summary>
/// <param name="Kind">The record kind: <c>live</c>, <c>tomb</c>, <c>absent</c>, or <c>none</c>.</param>
/// <param name="Metadata">The record's metadata fields and values.</param>
/// <param name="Segments">A live journal's data segments.</param>
internal sealed record ChainRecord(string Kind, RedisValue[] Metadata, RedisValue[] Segments)
{
    /// <summary>The chain holds no record of the journal.</summary>
    public static ChainRecord None { get; } = new(GarnetJournalProtocol.NoneKind, [], []);
}

/// <summary>A bucket's state in a partition.</summary>
/// <param name="CollectionGeneration">The bucket's collection generation (D25).</param>
internal readonly record struct BucketState(string State, long Version, string Source, bool Done, bool Reclaimed, long IndexCount, long MarkerCount, long CollectionGeneration = 0)
{
    /// <summary>Gets a value indicating whether the partition holds no copy of the bucket which a read can reach: it is absent or reclaimed (D11).</summary>
    public bool IsAbsentOrReclaimed => State == GarnetJournalProtocol.AbsentStateName || Reclaimed;
}

/// <summary>The outcome of consolidating a bucket.</summary>
internal enum ConsolidationResult
{
    /// <summary>The bucket's migration is done.</summary>
    Done,

    /// <summary>The partition does not hold the bucket open.</summary>
    NotOpen,

    /// <summary>A partition was unavailable or a chain copy was gone; retry later.</summary>
    Retry,
}
