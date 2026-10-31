using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Orleans.Journaling.Ranges;

namespace Orleans.Journaling;

/// <summary>
/// Moves buckets between partitions to balance load, following Slicer's weighted-move algorithm as Roux adapts it.
/// </summary>
/// <remarks>
/// <para>
/// Each round samples every partition's per-bucket operation counts (<see cref="GarnetJournalProtocol.Load"/>) and
/// derives each bucket's rate from the previous sample. A round acts only while the range map holds no migrations, so a
/// bucket moves only after its previous migration completed (chains form only from failure-driven moves, D11), and only
/// while imbalance, the most loaded partition's load over the mean, exceeds the threshold and the cluster's load is at
/// least the minimum. It then:
/// </para>
/// <list type="number">
/// <item><description>merges each cold range, shortest first, into the less loaded of its neighbors' owners, moving at most 1% of the buckets;</description></item>
/// <item><description>
/// repeatedly moves the run of buckets at either end of one of the most loaded partition's ranges to the least loaded
/// partition, choosing the run with the greatest reduction in the pair's maximum load per unit of churn (one plus the
/// bucket's journal count, since every moved journal is pulled or consolidated), until imbalance is within half the
/// threshold's margin or the round has moved <see cref="GarnetJournalBalancerSettings.MaxMoveFraction"/> of the buckets.
/// When no end run improves balance within the budget, it considers individual interior buckets.
/// </description></item>
/// </list>
/// <para>
/// Moving the end of a range splits the range; moving a whole range next to the destination's merges them. Buckets are
/// fixed when the map is created, so a bucket hotter than the margin between two partitions cannot be split further.
/// The round commits every move in one view by compare-and-swap (<see cref="GarnetJournalMigrationCoordinator.TryMoveAsync(IReadOnlyCollection{ValueTuple{int, PartitionId}}, bool, CancellationToken)"/>),
/// and every migration then follows the handover protocol. A duplicated balancer is harmless: the first round to
/// commit leaves migrations in the map, which stop the other.
/// </para>
/// </remarks>
internal sealed class GarnetJournalBalancer(
    GarnetJournalRouter router,
    GarnetJournalMigrationCoordinator coordinator,
    GarnetJournalBalancerSettings settings,
    ILogger? logger = null)
{
    private const double MergeFraction = 0.01;
    private const double ColdFraction = 0.01;

    private Dictionary<(PartitionId Partition, int Bucket), long>? _previous;
    private long _previousTimestamp;

    /// <summary>Gets the balancer's settings.</summary>
    public GarnetJournalBalancerSettings Settings => settings;

    /// <summary>Samples load and, if a previous sample exists and the map is idle and imbalanced, commits one round of moves.</summary>
    /// <returns>The moves committed, or an empty list.</returns>
    public async Task<IReadOnlyList<(int Bucket, PartitionId Destination)>> RunOnceAsync(CancellationToken cancellationToken)
    {
        var view = await router.RefreshAsync(cancellationToken).ConfigureAwait(false);
        var map = view.Map;
        if (map.Migrations.Length > 0)
        {
            return [];
        }

        var sample = await TrySampleAsync(map, cancellationToken).ConfigureAwait(false);
        var now = Stopwatch.GetTimestamp();
        if (sample is null)
        {
            _previous = null;
            return [];
        }

        var (counts, journals) = sample.Value;
        var previous = _previous;
        var elapsed = Stopwatch.GetElapsedTime(_previousTimestamp, now).TotalSeconds;
        _previous = counts;
        _previousTimestamp = now;
        if (previous is null || elapsed <= 0)
        {
            return [];
        }

        var owners = new PartitionId[map.BucketCount];
        var load = new double[map.BucketCount];
        foreach (var range in map.Ranges)
        {
            for (var bucket = range.FirstBucket; bucket <= range.LastBucket; bucket++)
            {
                owners[bucket] = range.Owner;
                load[bucket] = Rate(previous, range.Owner, bucket, counts[(range.Owner, bucket)], elapsed);
            }
        }

        // Only partitions which serve can receive buckets.
        var moves = Plan(owners, load, journals, [.. map.Partitions.Where(entry => entry.Value.Endpoint is not null).Select(entry => entry.Key)], settings);
        if (moves.Count == 0)
        {
            return [];
        }

        var result = await coordinator.TryMoveAsync(moves, requireIdle: true, cancellationToken).ConfigureAwait(false);
        if (result.Status != MoveStatus.Committed)
        {
            logger?.LogDebug("Garnet journal balancing round did not commit: {Status}.", result.Status);
            return [];
        }

        logger?.LogInformation(
            "Garnet journal balancer moved {Count} buckets in range map version {Version}: {Moves}.",
            moves.Count,
            result.View.Version,
            string.Join(", ", moves.GroupBy(move => move.Destination).Select(group => $"{group.Count()} to {group.Key}")));
        return moves;
    }

    /// <summary>
    /// Derives a bucket's operation rate at its owner from the owner's cumulative count in this sample and the previous one.
    /// </summary>
    /// <remarks>
    /// A partition's counters persist while it runs, across migrations, and each sample reads only the buckets their
    /// owners hold. If the owner did not hold the bucket at the previous sample, its count may include an earlier
    /// tenure's traffic, for example after the bucket moved away and back, so this sample only sets the baseline. An
    /// owner which restarted since the previous sample counts from zero.
    /// </remarks>
    internal static double Rate(IReadOnlyDictionary<(PartitionId Partition, int Bucket), long> previous, PartitionId owner, int bucket, long count, double elapsed)
    {
        if (!previous.TryGetValue((owner, bucket), out var earlier))
        {
            return 0;
        }

        return (count - (earlier <= count ? earlier : 0)) / elapsed;
    }

    /// <summary>Reads every partition's cumulative operation counts and journal counts for the buckets it owns.</summary>
    /// <returns>The samples, or <see langword="null"/> if a partition is unavailable.</returns>
    private async Task<(Dictionary<(PartitionId, int), long> Counts, long[] Journals)?> TrySampleAsync(RangeMap map, CancellationToken cancellationToken)
    {
        var counts = new Dictionary<(PartitionId, int), long>();
        var journals = new long[map.BucketCount];
        foreach (var owned in map.Ranges.GroupBy(range => range.Owner))
        {
            var ranges = owned.ToList();
            var args = new object[ranges.Count * 2];
            for (var i = 0; i < ranges.Count; i++)
            {
                args[2 * i] = ranges[i].FirstBucket.ToString(CultureInfo.InvariantCulture);
                args[2 * i + 1] = ranges[i].LastBucket.ToString(CultureInfo.InvariantCulture);
            }

            StackExchange.Redis.RedisResult[]? result;
            try
            {
                result = await router.TryExecuteAtAsync(owned.Key, GarnetJournalProtocol.Load, args, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (GarnetJournalRouter.IsTransient(exception))
            {
                result = null;
            }

            if (result is null || GarnetJournalRouter.GetStatus(result) != GarnetJournalProtocol.SuccessStatus)
            {
                return null;
            }

            var index = 1;
            foreach (var range in ranges)
            {
                for (var bucket = range.FirstBucket; bucket <= range.LastBucket; bucket++)
                {
                    counts[(owned.Key, bucket)] = (long)result[index++];
                    journals[bucket] = (long)result[index++];
                }
            }
        }

        return (counts, journals);
    }

    /// <summary>
    /// Plans one balancing round.
    /// </summary>
    /// <param name="owners">Each bucket's owner.</param>
    /// <param name="load">Each bucket's load, in operations per second.</param>
    /// <param name="journals">Each bucket's journal count, which weights the churn of moving it.</param>
    /// <param name="partitions">The partitions which may own buckets, including those which own none.</param>
    /// <param name="settings">The balancer's settings.</param>
    /// <returns>The buckets to move, each with its destination.</returns>
    internal static List<(int Bucket, PartitionId Destination)> Plan(
        PartitionId[] owners,
        double[] load,
        long[] journals,
        IReadOnlyCollection<PartitionId> partitions,
        GarnetJournalBalancerSettings settings)
    {
        var bucketCount = owners.Length;
        var owner = (PartitionId[])owners.Clone();
        var partitionLoad = partitions.ToDictionary(partition => partition, _ => 0.0);
        for (var bucket = 0; bucket < bucketCount; bucket++)
        {
            if (!partitionLoad.ContainsKey(owner[bucket]))
            {
                return [];
            }

            partitionLoad[owner[bucket]] += load[bucket];
        }

        var total = partitionLoad.Values.Sum();
        var mean = total / partitionLoad.Count;
        if (partitionLoad.Count < 2 || total < settings.MinimumLoad || partitionLoad.Values.Max() <= settings.Threshold * mean)
        {
            return [];
        }

        var budget = Math.Max(1, (int)(settings.MaxMoveFraction * bucketCount));
        var mergeBudget = Math.Min(budget, (int)(MergeFraction * bucketCount));
        var coldLimit = ColdFraction * mean;

        // 1. Merge cold ranges into a neighbor's owner, shortest first.
        while (mergeBudget > 0)
        {
            var runs = Runs(owner);
            (int First, int Last, PartitionId Destination)? merge = null;
            foreach (var run in runs.OrderBy(run => run.Last - run.First).ThenBy(run => run.First))
            {
                var length = run.Last - run.First + 1;
                if (length > mergeBudget || runs.Count < 2 || Sum(load, run.First, run.Last) > coldLimit)
                {
                    continue;
                }

                PartitionId? left = run.First > 0 ? owner[run.First - 1] : null;
                PartitionId? right = run.Last < bucketCount - 1 ? owner[run.Last + 1] : null;
                var destination = left is null ? right!.Value
                    : right is null ? left.Value
                    : partitionLoad[right.Value] < partitionLoad[left.Value] ? right.Value : left.Value;
                merge = (run.First, run.Last, destination);
                break;
            }

            if (merge is not { } chosen)
            {
                break;
            }

            Apply(chosen.First, chosen.Last, chosen.Destination);
            mergeBudget -= chosen.Last - chosen.First + 1;
        }

        // 2. Move the runs with the greatest reduction in imbalance per unit of churn from the most to the least loaded partition.
        var target = (1 + (settings.Threshold - 1) / 2) * mean;
        while (budget > 0)
        {
            var hot = partitionLoad.MaxBy(entry => entry.Value).Key;
            var cold = partitionLoad.MinBy(entry => entry.Value).Key;
            if (partitionLoad[hot] <= target || hot == cold)
            {
                break;
            }

            (int First, int Last, double Score, bool Adjacent, double Gain)? best = null;
            foreach (var run in Runs(owner).Where(run => run.Owner == hot))
            {
                Consider(run.First, run.Last, forward: true);
                Consider(run.First, run.Last, forward: false);
            }

            // Cold edges can hide movable load beyond the budget. Split out an interior bucket to make progress.
            if (best is null)
            {
                for (var bucket = 0; bucket < bucketCount; bucket++)
                {
                    if (owner[bucket] == hot)
                    {
                        Consider(bucket, bucket, forward: true);
                    }
                }
            }

            if (best is not { } move)
            {
                break;
            }

            Apply(move.First, move.Last, cold);

            // Among runs with equal reduction per unit of churn, prefer one next to the destination's range, which merges
            // with it, then the larger.
            void Consider(int first, int last, bool forward)
            {
                double moved = 0, churn = 0;
                for (var k = 0; k <= last - first && k < budget; k++)
                {
                    var bucket = forward ? first + k : last - k;
                    moved += load[bucket];
                    churn += 1 + journals[bucket];
                    var gain = partitionLoad[hot] - Math.Max(partitionLoad[hot] - moved, partitionLoad[cold] + moved);
                    if (gain <= 0)
                    {
                        continue;
                    }

                    var score = gain / churn;
                    var (start, end) = forward ? (first, first + k) : (last - k, last);
                    var adjacent = start > 0 && owner[start - 1] == cold || end < bucketCount - 1 && owner[end + 1] == cold;
                    if (best is not { } current
                        || score > current.Score * (1 + 1e-9)
                        || score >= current.Score * (1 - 1e-9) && (adjacent && !current.Adjacent || adjacent == current.Adjacent && gain > current.Gain))
                    {
                        best = (start, end, score, adjacent, gain);
                    }
                }
            }
        }

        var moves = new List<(int Bucket, PartitionId Destination)>();
        for (var bucket = 0; bucket < bucketCount; bucket++)
        {
            if (owner[bucket] != owners[bucket])
            {
                moves.Add((bucket, owner[bucket]));
            }
        }

        return moves;

        void Apply(int first, int last, PartitionId destination)
        {
            for (var bucket = first; bucket <= last; bucket++)
            {
                partitionLoad[owner[bucket]] -= load[bucket];
                partitionLoad[destination] += load[bucket];
                owner[bucket] = destination;

                // A bucket which returns to its owner within the round does not move.
                budget -= owners[bucket] == destination ? -1 : 1;
            }
        }
    }

    private static double Sum(double[] values, int first, int last)
    {
        var sum = 0.0;
        for (var i = first; i <= last; i++)
        {
            sum += values[i];
        }

        return sum;
    }

    private static List<(int First, int Last, PartitionId Owner)> Runs(PartitionId[] owner)
    {
        var runs = new List<(int, int, PartitionId)>();
        var first = 0;
        for (var bucket = 1; bucket <= owner.Length; bucket++)
        {
            if (bucket == owner.Length || owner[bucket] != owner[first])
            {
                runs.Add((first, bucket - 1, owner[first]));
                first = bucket;
            }
        }

        return runs;
    }

    /// <summary>Gets a value indicating whether this host runs the balancer: it hosts the partition with the lowest id.</summary>
    /// <remarks>
    /// The rule needs no election: every host evaluates it on its own view, and a wrong or duplicated verdict while
    /// hosting changes is harmless, since a round commits only on an idle map.
    /// </remarks>
    public static bool IsBalancer(RangeMap map, Func<PartitionId, bool> isHosted) => isHosted(map.Partitions.Keys.First());
}

/// <summary>Settings for <see cref="GarnetJournalBalancer"/>.</summary>
/// <param name="Threshold">The imbalance, the most loaded partition's load over the mean, above which a round moves buckets.</param>
/// <param name="MinimumLoad">The cluster's load, in operations per second, below which no round moves buckets.</param>
/// <param name="MaxMoveFraction">The largest fraction of the buckets a round moves.</param>
internal sealed record GarnetJournalBalancerSettings(double Threshold, double MinimumLoad, double MaxMoveFraction)
{
    public static GarnetJournalBalancerSettings From(GarnetJournalStorageOptions options)
        => new(options.BalancingThreshold, options.BalancingMinimumLoad, options.BalancingMaxMoveFraction);
}
