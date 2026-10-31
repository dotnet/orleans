using System.Collections.Immutable;
using System.Numerics;

namespace Orleans.Journaling.Ranges;

/// <summary>
/// A versioned range map, as stored in an <see cref="IRangeMetaStore"/>.
/// </summary>
/// <param name="Version">The map's version, at least 1. Every change to the map increments it by one.</param>
/// <param name="Map">The range map.</param>
public sealed record RangeMapSnapshot(long Version, RangeMap Map);

/// <summary>
/// The result of <see cref="IRangeMetaStore.TryWriteAsync"/>.
/// </summary>
/// <param name="Written">Whether the store wrote the new map.</param>
/// <param name="Current">The stored map after the call, or <see langword="null"/> if the store holds no map.</param>
public readonly record struct RangeMapWriteResult(bool Written, RangeMapSnapshot? Current);

/// <summary>
/// Maps buckets to the partitions which own them, and records migrations in progress and where each partition is hosted.
/// </summary>
/// <param name="Id">
/// The map's identity, fixed when the map is created: 1 to 64 characters from <c>[A-Za-z0-9._-]</c>, starting with a
/// letter or digit. Partitions record it with their bucket states, so a partition of another map rejects operations
/// routed to it through a stale endpoint.
/// </param>
/// <param name="BucketCount">The number of buckets, a power of two, fixed when the map is created.</param>
/// <param name="Ranges">Contiguous, ordered ranges which together cover buckets <c>[0, BucketCount)</c>.</param>
/// <param name="Migrations">
/// Migrations in progress. The migrations of a bucket form a chain, ordered by the versions which started them: each
/// migrates the bucket from the previous one's destination, and the latest migrates it to the bucket's owner.
/// </param>
/// <param name="Partitions">Every partition, with its hosting assignment.</param>
public sealed record RangeMap(
    string Id,
    int BucketCount,
    ImmutableArray<RangeAssignment> Ranges,
    ImmutableArray<Migration> Migrations,
    ImmutableSortedDictionary<PartitionId, PartitionHosting> Partitions)
{
    /// <summary>
    /// Creates a new, unique map identity.
    /// </summary>
    /// <returns>A map identity.</returns>
    public static string CreateId() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// Creates a map with a new identity which divides the buckets into equal contiguous ranges, one per partition, in the given order.
    /// </summary>
    /// <param name="bucketCount">The number of buckets, a power of two.</param>
    /// <param name="partitions">The partitions and their hosting assignments.</param>
    /// <returns>The range map.</returns>
    /// <exception cref="ArgumentException">The arguments do not describe a valid map.</exception>
    public static RangeMap CreateUniform(int bucketCount, IEnumerable<KeyValuePair<PartitionId, PartitionHosting>> partitions)
    {
        ArgumentNullException.ThrowIfNull(partitions);
        var list = partitions.ToList();
        if (bucketCount <= 0 || !BitOperations.IsPow2(bucketCount))
        {
            throw new ArgumentException("The bucket count must be a positive power of two.", nameof(bucketCount));
        }

        if (list.Count == 0 || list.Count > bucketCount)
        {
            throw new ArgumentException($"Between 1 and {bucketCount} partitions are required.", nameof(partitions));
        }

        var ranges = ImmutableArray.CreateBuilder<RangeAssignment>(list.Count);
        var first = 0;
        for (var i = 0; i < list.Count; i++)
        {
            var last = (int)((long)bucketCount * (i + 1) / list.Count) - 1;
            ranges.Add(new RangeAssignment(first, last, list[i].Key));
            first = last + 1;
        }

        var map = new RangeMap(CreateId(), bucketCount, ranges.MoveToImmutable(), [], ImmutableSortedDictionary.CreateRange(list));
        try
        {
            map.Validate();
        }
        catch (InvalidOperationException exception)
        {
            throw new ArgumentException(exception.Message, nameof(partitions), exception);
        }

        return map;
    }

    /// <summary>
    /// Gets the partition which owns a bucket.
    /// </summary>
    /// <param name="bucket">The bucket.</param>
    /// <returns>The owner of the range containing <paramref name="bucket"/>.</returns>
    /// <remarks>The map must be valid; see <see cref="Validate"/>.</remarks>
    public PartitionId GetOwner(int bucket) => Ranges[FindRange(bucket)].Owner;

    /// <summary>
    /// Gets the index in <see cref="Ranges"/> of the range containing a bucket.
    /// </summary>
    /// <param name="bucket">The bucket.</param>
    /// <returns>The index of the range containing <paramref name="bucket"/>.</returns>
    /// <remarks>The map must be valid; see <see cref="Validate"/>.</remarks>
    public int FindRange(int bucket)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bucket);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(bucket, BucketCount);
        var ranges = Ranges.AsSpan();
        int low = 0, high = ranges.Length - 1;
        while (low < high)
        {
            var middle = low + (high - low + 1) / 2;
            if (ranges[middle].FirstBucket <= bucket)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return low;
    }

    /// <summary>
    /// Checks the map's structural invariants.
    /// </summary>
    /// <exception cref="InvalidOperationException">The map is invalid.</exception>
    /// <remarks>
    /// A valid map has a valid id and a power-of-two bucket count; ranges which are ordered, contiguous, and cover every bucket, and
    /// whose owners are known partitions; migrations between distinct known partitions which form one chain per bucket,
    /// ending at the bucket's owner; and hosting assignments with a host, a non-negative epoch, and a storage location.
    /// </remarks>
    public void Validate()
    {
        if (!PartitionId.IsValid(Id))
        {
            throw Invalid($"its id '{Id}' is not 1 to 64 characters from [A-Za-z0-9._-] starting with a letter or digit");
        }

        if (BucketCount <= 0 || !BitOperations.IsPow2(BucketCount))
        {
            throw Invalid($"the bucket count {BucketCount} is not a positive power of two");
        }

        if (Partitions is null || Partitions.Count == 0)
        {
            throw Invalid("it has no partitions");
        }

        foreach (var (id, hosting) in Partitions)
        {
            if (id.IsDefault)
            {
                throw Invalid("a partition id is the default value");
            }

            if (hosting is null || string.IsNullOrWhiteSpace(hosting.Host) || string.IsNullOrWhiteSpace(hosting.StorageLocation)
                || hosting.Epoch < 0 || hosting.Endpoint is { } endpoint && string.IsNullOrWhiteSpace(endpoint))
            {
                throw Invalid($"partition '{id}' requires a host, a storage location, a non-negative epoch, and a non-empty endpoint if it has one");
            }
        }

        if (Ranges.IsDefaultOrEmpty)
        {
            throw Invalid("it has no ranges");
        }

        var next = 0;
        foreach (var range in Ranges)
        {
            if (range is null || range.FirstBucket != next || range.LastBucket < range.FirstBucket || range.LastBucket >= BucketCount)
            {
                throw Invalid($"its ranges must be ordered and contiguous and cover buckets [0, {BucketCount - 1}], but a range starts at {range?.FirstBucket} where bucket {next} was expected");
            }

            if (!Partitions.ContainsKey(range.Owner))
            {
                throw Invalid($"range [{range.FirstBucket}, {range.LastBucket}] is owned by unknown partition '{range.Owner}'");
            }

            if (range.AssignedAtVersion < 0)
            {
                throw Invalid($"range [{range.FirstBucket}, {range.LastBucket}] has a negative assignment version");
            }

            next = range.LastBucket + 1;
        }

        if (next != BucketCount)
        {
            throw Invalid($"its ranges cover buckets [0, {next - 1}] rather than [0, {BucketCount - 1}]");
        }

        if (Migrations.IsDefault)
        {
            throw Invalid("its migrations are the default value; use an empty array");
        }

        Dictionary<int, List<Migration>>? chains = null;
        foreach (var migration in Migrations)
        {
            if (migration is null
                || migration.FirstBucket < 0 || migration.LastBucket < migration.FirstBucket || migration.LastBucket >= BucketCount
                || migration.StartedAtVersion < 0 || !Enum.IsDefined(migration.Phase))
            {
                throw Invalid("a migration has an invalid range, version, or phase");
            }

            if (migration.Source == migration.Destination || !Partitions.ContainsKey(migration.Source) || !Partitions.ContainsKey(migration.Destination))
            {
                throw Invalid($"the migration of buckets [{migration.FirstBucket}, {migration.LastBucket}] requires distinct, known source and destination partitions");
            }

            chains ??= [];
            for (var bucket = migration.FirstBucket; bucket <= migration.LastBucket; bucket++)
            {
                if (!chains.TryGetValue(bucket, out var chain))
                {
                    chains[bucket] = chain = [];
                }

                chain.Add(migration);
            }
        }

        if (chains is not null)
        {
            foreach (var (bucket, chain) in chains)
            {
                chain.Sort(static (left, right) => left.StartedAtVersion.CompareTo(right.StartedAtVersion));
                for (var i = 1; i < chain.Count; i++)
                {
                    if (chain[i - 1].StartedAtVersion == chain[i].StartedAtVersion || chain[i - 1].Destination != chain[i].Source)
                    {
                        throw Invalid($"the migrations of bucket {bucket} do not form a chain: each must start at a later version, from the previous migration's destination");
                    }
                }

                var owner = GetOwner(bucket);
                if (chain[^1].Destination != owner)
                {
                    throw Invalid($"the latest migration of bucket {bucket} is to '{chain[^1].Destination}', but the bucket is owned by '{owner}'");
                }
            }
        }

        static InvalidOperationException Invalid(string reason) => new($"The range map is invalid: {reason}.");
    }

    /// <inheritdoc/>
    public bool Equals(RangeMap? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return other is not null
            && string.Equals(Id, other.Id, StringComparison.Ordinal)
            && BucketCount == other.BucketCount
            && Ranges.AsSpan().SequenceEqual(other.Ranges.AsSpan())
            && Migrations.AsSpan().SequenceEqual(other.Migrations.AsSpan())
            && (Partitions ?? ImmutableSortedDictionary<PartitionId, PartitionHosting>.Empty).SequenceEqual(
                other.Partitions ?? ImmutableSortedDictionary<PartitionId, PartitionHosting>.Empty);
    }

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Id, BucketCount, Ranges.IsDefault ? 0 : Ranges.Length, Partitions?.Count ?? 0);
}

/// <summary>
/// Assigns a contiguous range of buckets to a partition.
/// </summary>
/// <param name="FirstBucket">The first bucket of the range.</param>
/// <param name="LastBucket">The last bucket of the range, inclusive.</param>
/// <param name="Owner">The partition which owns the range.</param>
/// <param name="AssignedAtVersion">
/// The map version which assigned the range to <paramref name="Owner"/>: 0 for ranges assigned when the map was
/// created, otherwise the version which committed the migration into the owner. The owner opens the range's buckets at
/// this version, and reports it when it rejects an operation on a bucket it no longer accepts.
/// </param>
public sealed record RangeAssignment(int FirstBucket, int LastBucket, PartitionId Owner, long AssignedAtVersion = 0);

/// <summary>
/// A migration of a range of buckets from one partition to another.
/// </summary>
/// <remarks>
/// A migration remains in the map until its source has reclaimed the buckets' data. A bucket moves again once its
/// latest migration has reached <see cref="MigrationPhase.Opened"/>, so a bucket can have several migrations, which
/// form a chain: reads in the newest destination which find no record of a journal follow the chain toward older
/// sources until a destination whose migration is <see cref="MigrationPhase.Done"/>.
/// </remarks>
/// <param name="FirstBucket">The first bucket of the range.</param>
/// <param name="LastBucket">The last bucket of the range, inclusive.</param>
/// <param name="Source">The partition the buckets move from.</param>
/// <param name="Destination">The partition the buckets move to, which owns them in the map.</param>
/// <param name="StartedAtVersion">The map version which committed the migration.</param>
/// <param name="Phase">The migration's progress.</param>
public sealed record Migration(
    int FirstBucket,
    int LastBucket,
    PartitionId Source,
    PartitionId Destination,
    long StartedAtVersion,
    MigrationPhase Phase);

/// <summary>
/// The progress of a <see cref="Migration"/>. Each phase is recorded after the step it names has completed.
/// </summary>
public enum MigrationPhase
{
    /// <summary>The map assigns the buckets to the destination.</summary>
    Committed,

    /// <summary>The source has sealed every bucket in the range.</summary>
    Sealed,

    /// <summary>The destination has opened every bucket in the range, after the source sealed it.</summary>
    Opened,

    /// <summary>The destination holds every journal of the range; reads no longer consult the source.</summary>
    Done,

    /// <summary>
    /// No read reaches the source's copy of the range, because this migration or a later one of the same buckets is
    /// done. The source reclaims the range, and the migration is then removed from the map.
    /// </summary>
    Complete,
}

/// <summary>
/// Where a partition is hosted.
/// </summary>
/// <param name="Host">The identifier of the host which runs the partition.</param>
/// <param name="Epoch">
/// The hosting epoch, incremented whenever the partition moves to another host, so that writes from a previous host can be fenced.
/// </param>
/// <param name="StorageLocation">The location of the partition's durable storage, interpreted by its host.</param>
/// <param name="Endpoint">
/// The <c>host:port</c> endpoint at which the partition accepts connections, published by its host once the partition is
/// serving, or <see langword="null"/> before then.
/// </param>
public sealed record PartitionHosting(string Host, long Epoch, string StorageLocation, string? Endpoint = null);
