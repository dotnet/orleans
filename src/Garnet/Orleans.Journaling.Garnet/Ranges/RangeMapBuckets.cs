namespace Orleans.Journaling.Ranges;

/// <summary>One step of a bucket's migration chain.</summary>
internal readonly record struct MigrationStep(PartitionId Source, PartitionId Destination, long StartedAtVersion, MigrationPhase Phase)
{
    /// <summary>Gets a value indicating whether <paramref name="other"/> is the same migration, in any phase.</summary>
    public bool IsSameMigration(MigrationStep other)
        => Source == other.Source && Destination == other.Destination && StartedAtVersion == other.StartedAtVersion;
}

/// <summary>
/// A range map expanded to one entry per bucket, for editing ownership and migrations bucket by bucket.
/// </summary>
/// <remarks>
/// Migration phases are tracked per bucket: a range's migration reaches a phase once each of its buckets has.
/// <see cref="ToMap"/> merges adjacent buckets with equal ownership and equal migrations back into ranges.
/// </remarks>
internal sealed class RangeMapBuckets
{
    private readonly RangeMap _map;
    private readonly PartitionId[] _owners;
    private readonly long[] _assignedAt;
    private readonly List<MigrationStep>?[] _migrations;

    public RangeMapBuckets(RangeMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        _map = map;
        _owners = new PartitionId[map.BucketCount];
        _assignedAt = new long[map.BucketCount];
        _migrations = new List<MigrationStep>?[map.BucketCount];
        foreach (var range in map.Ranges)
        {
            for (var bucket = range.FirstBucket; bucket <= range.LastBucket; bucket++)
            {
                _owners[bucket] = range.Owner;
                _assignedAt[bucket] = range.AssignedAtVersion;
            }
        }

        foreach (var migration in map.Migrations)
        {
            var step = new MigrationStep(migration.Source, migration.Destination, migration.StartedAtVersion, migration.Phase);
            for (var bucket = migration.FirstBucket; bucket <= migration.LastBucket; bucket++)
            {
                (_migrations[bucket] ??= []).Add(step);
            }
        }

        foreach (var steps in _migrations)
        {
            steps?.Sort(static (left, right) => left.StartedAtVersion.CompareTo(right.StartedAtVersion));
        }
    }

    public int BucketCount => _owners.Length;

    public PartitionId GetOwner(int bucket) => _owners[bucket];

    public long GetAssignedAt(int bucket) => _assignedAt[bucket];

    /// <summary>Gets the bucket's migrations, oldest first.</summary>
    public IReadOnlyList<MigrationStep> GetMigrations(int bucket) => (IReadOnlyList<MigrationStep>?)_migrations[bucket] ?? [];

    /// <summary>Gets the bucket's latest migration, if any.</summary>
    public MigrationStep? GetLatestMigration(int bucket) => _migrations[bucket] is { Count: > 0 } steps ? steps[^1] : null;

    /// <summary>Assigns the bucket to <paramref name="destination"/> at <paramref name="version"/>, adding a committed migration from its owner.</summary>
    public void Move(int bucket, PartitionId destination, long version)
    {
        if (_owners[bucket] == destination)
        {
            throw new InvalidOperationException($"Bucket {bucket} is already owned by '{destination}'.");
        }

        (_migrations[bucket] ??= []).Add(new MigrationStep(_owners[bucket], destination, version, MigrationPhase.Committed));
        _owners[bucket] = destination;
        _assignedAt[bucket] = version;
    }

    /// <summary>Advances a migration of the bucket to <paramref name="phase"/>, if it is in the map in an earlier phase.</summary>
    /// <returns><see langword="true"/> if the map changed.</returns>
    public bool TryAdvance(int bucket, MigrationStep migration, MigrationPhase phase)
    {
        if (_migrations[bucket] is not { } steps)
        {
            return false;
        }

        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].IsSameMigration(migration) && steps[i].Phase < phase)
            {
                steps[i] = steps[i] with { Phase = phase };
                return true;
            }
        }

        return false;
    }

    /// <summary>Removes a completed migration of the bucket.</summary>
    /// <returns><see langword="true"/> if the map changed.</returns>
    public bool TryRemove(int bucket, MigrationStep migration)
    {
        if (_migrations[bucket] is not { } steps)
        {
            return false;
        }

        var index = steps.FindIndex(step => step.IsSameMigration(migration) && step.Phase == MigrationPhase.Complete);
        if (index < 0)
        {
            return false;
        }

        steps.RemoveAt(index);
        return true;
    }

    /// <summary>Creates the range map, merging adjacent buckets with equal ownership and migrations into ranges.</summary>
    public RangeMap ToMap()
    {
        var ranges = new List<RangeAssignment>();
        var first = 0;
        for (var bucket = 1; bucket <= _owners.Length; bucket++)
        {
            if (bucket == _owners.Length || _owners[bucket] != _owners[first] || _assignedAt[bucket] != _assignedAt[first])
            {
                ranges.Add(new RangeAssignment(first, bucket - 1, _owners[first], _assignedAt[first]));
                first = bucket;
            }
        }

        // Each migration covers maximal runs of adjacent buckets which have it.
        var migrations = new List<Migration>();
        var open = new Dictionary<MigrationStep, int>();
        var next = new Dictionary<MigrationStep, int>();
        for (var bucket = 0; bucket <= _owners.Length; bucket++)
        {
            next.Clear();
            if (bucket < _owners.Length && _migrations[bucket] is { } steps)
            {
                foreach (var step in steps)
                {
                    next[step] = open.TryGetValue(step, out var start) ? start : bucket;
                }
            }

            foreach (var (step, start) in open)
            {
                if (!next.ContainsKey(step))
                {
                    migrations.Add(new Migration(start, bucket - 1, step.Source, step.Destination, step.StartedAtVersion, step.Phase));
                }
            }

            (open, next) = (next, open);
        }

        migrations.Sort(static (left, right) => left.FirstBucket != right.FirstBucket
            ? left.FirstBucket.CompareTo(right.FirstBucket)
            : left.StartedAtVersion.CompareTo(right.StartedAtVersion));
        return _map with { Ranges = [.. ranges], Migrations = [.. migrations] };
    }
}
