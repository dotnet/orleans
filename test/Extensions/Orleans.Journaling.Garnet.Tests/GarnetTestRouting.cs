using System.Collections.Immutable;
using System.Net;
using Orleans.Journaling.Ranges;

namespace Orleans.Journaling.Garnet.Tests;

/// <summary>
/// Helpers for tests which route journal operations over explicitly constructed range maps.
/// </summary>
internal static class GarnetTestRouting
{
    public static readonly PartitionId P0 = new("p0");

    /// <summary>Initializes a partition which owns every bucket, and creates a router for it.</summary>
    public static async Task<GarnetJournalRouter> CreateSinglePartitionRouterAsync(IPEndPoint endPoint, TimeSpan operationTimeout, CancellationToken cancellationToken)
    {
        var map = RangeMap.CreateUniform(
            GarnetJournalStorageOptions.DEFAULT_BUCKET_COUNT,
            [new(P0, new PartitionHosting("test", 0, "p0", GarnetJournalPartitionManager.FormatEndpoint(endPoint)))]);
        map = map with { Id = "test-map" };
        await GarnetJournalPartitionManager.InitializeAsync(endPoint, P0, map, cancellationToken);
        var store = new InMemoryRangeMetaStore();
        var snapshot = (await store.TryWriteAsync(map, 0, cancellationToken)).Current!;
        return new GarnetJournalRouter(store, snapshot, operationTimeout);
    }

    /// <summary>Returns <paramref name="map"/> with <paramref name="bucket"/> assigned to <paramref name="owner"/> at <paramref name="version"/>.</summary>
    public static RangeMap AssignBucket(RangeMap map, int bucket, PartitionId owner, long version)
    {
        var assignments = new (PartitionId Owner, long Version)[map.BucketCount];
        foreach (var range in map.Ranges)
        {
            for (var b = range.FirstBucket; b <= range.LastBucket; b++)
            {
                assignments[b] = (range.Owner, range.AssignedAtVersion);
            }
        }

        assignments[bucket] = (owner, version);
        var ranges = ImmutableArray.CreateBuilder<RangeAssignment>();
        var first = 0;
        for (var b = 1; b <= map.BucketCount; b++)
        {
            if (b == map.BucketCount || assignments[b] != assignments[first])
            {
                ranges.Add(new RangeAssignment(first, b - 1, assignments[first].Owner, assignments[first].Version));
                first = b;
            }
        }

        return map with { Ranges = ranges.ToImmutable() };
    }
}
