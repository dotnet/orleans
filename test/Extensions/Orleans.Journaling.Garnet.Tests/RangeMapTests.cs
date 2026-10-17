using System.Collections.Immutable;
using Orleans.Journaling.Ranges;

namespace Orleans.Journaling.Garnet.Tests;

/// <summary>
/// Tests range map validation, bucket routing, and serialization.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class RangeMapTests
{
    private const string MapId = "map";
    private static readonly PartitionId A = new("a");
    private static readonly PartitionId B = new("b");
    private static readonly PartitionId C = new("c");

    private static ImmutableSortedDictionary<PartitionId, PartitionHosting> Partitions(params PartitionId[] ids)
        => ids.ToImmutableSortedDictionary(id => id, id => new PartitionHosting($"host-{id}", 0, id.Value));

    [Theory]
    [InlineData(1, 1)]
    [InlineData(16, 3)]
    [InlineData(4096, 5)]
    [InlineData(64, 64)]
    public void CreateUniform_CoversEveryBucketWithBalancedContiguousRanges(int bucketCount, int partitionCount)
    {
        var ids = Enumerable.Range(0, partitionCount).Select(index => new PartitionId($"p{index}")).ToArray();
        var map = RangeMap.CreateUniform(bucketCount, ids.Select(id => KeyValuePair.Create(id, new PartitionHosting("host", 0, id.Value))));

        map.Validate();
        Assert.Equal(ids, map.Ranges.Select(range => range.Owner));
        var sizes = map.Ranges.Select(range => range.LastBucket - range.FirstBucket + 1).ToArray();
        Assert.Equal(bucketCount, sizes.Sum());
        Assert.True(sizes.Max() - sizes.Min() <= 1);
        Assert.All(map.Ranges, range => Assert.Equal(0, range.AssignedAtVersion));
    }

    [Fact]
    public void GetOwner_MatchesTheRangeContainingEachBucket()
    {
        var map = new RangeMap(
            MapId,
            32,
            [new(0, 0, A), new(1, 9, B, 3), new(10, 10, A, 4), new(11, 30, C), new(31, 31, B, 7)],
            [],
            Partitions(A, B, C));
        map.Validate();

        for (var bucket = 0; bucket < map.BucketCount; bucket++)
        {
            var expected = map.Ranges.Single(range => range.FirstBucket <= bucket && bucket <= range.LastBucket);
            Assert.Equal(expected.Owner, map.GetOwner(bucket));
            Assert.Equal(map.Ranges.IndexOf(expected), map.FindRange(bucket));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => map.GetOwner(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.GetOwner(32));
    }

    private static readonly Dictionary<string, RangeMap> InvalidMapsByReason = new()
    {
        ["map id"] = new RangeMap("-", 4, [new(0, 3, A)], [], Partitions(A)),
        ["bucket count"] = new RangeMap(MapId, 12, [new(0, 11, A)], [], Partitions(A)),
        ["no partitions"] = new RangeMap(MapId, 4, [new(0, 3, A)], [], ImmutableSortedDictionary<PartitionId, PartitionHosting>.Empty),
        ["gap"] = new RangeMap(MapId, 4, [new(0, 1, A), new(3, 3, A)], [], Partitions(A)),
        ["overlap"] = new RangeMap(MapId, 4, [new(0, 2, A), new(2, 3, A)], [], Partitions(A)),
        ["short"] = new RangeMap(MapId, 4, [new(0, 2, A)], [], Partitions(A)),
        ["unknown owner"] = new RangeMap(MapId, 4, [new(0, 3, B)], [], Partitions(A)),
        ["negative version"] = new RangeMap(MapId, 4, [new(0, 3, A, -1)], [], Partitions(A)),
        ["default migrations"] = new RangeMap(MapId, 4, [new(0, 3, A)], default, Partitions(A)),
        ["missing host"] = new RangeMap(MapId, 4, [new(0, 3, A)], [], ImmutableSortedDictionary<PartitionId, PartitionHosting>.Empty.Add(A, new PartitionHosting("", 0, "a"))),
        ["empty endpoint"] = new RangeMap(MapId, 4, [new(0, 3, A)], [], ImmutableSortedDictionary<PartitionId, PartitionHosting>.Empty.Add(A, new PartitionHosting("h", 0, "a", " "))),
        ["migration to non-owner"] = new RangeMap(MapId, 4, [new(0, 1, A), new(2, 3, B, 2)], [new(1, 2, A, B, 2, MigrationPhase.Committed)], Partitions(A, B)),
        ["migration to self"] = new RangeMap(MapId, 4, [new(0, 3, A)], [new(1, 2, A, A, 2, MigrationPhase.Committed)], Partitions(A)),
        ["unchained migrations"] = new RangeMap(MapId, 4, [new(0, 3, B, 2)], [new(0, 1, A, B, 2, MigrationPhase.Sealed), new(1, 2, C, B, 2, MigrationPhase.Committed)], Partitions(A, B, C)),
        ["chain not ending at owner"] = new RangeMap(MapId, 4, [new(0, 3, B, 2)], [new(0, 0, A, B, 2, MigrationPhase.Opened), new(0, 0, B, C, 3, MigrationPhase.Committed)], Partitions(A, B, C)),
        ["chain out of order"] = new RangeMap(MapId, 4, [new(0, 3, C, 3)], [new(0, 0, A, B, 3, MigrationPhase.Opened), new(0, 0, B, C, 3, MigrationPhase.Committed)], Partitions(A, B, C)),
        ["undefined phase"] = new RangeMap(MapId, 4, [new(0, 3, B, 2)], [new(0, 1, A, B, 2, (MigrationPhase)17)], Partitions(A, B)),
    };

    public static TheoryData<string> InvalidMaps => [.. InvalidMapsByReason.Keys];

    [Theory]
    [MemberData(nameof(InvalidMaps))]
    public void Validate_RejectsInvalidMaps(string reason)
    {
        Assert.Throws<InvalidOperationException>(InvalidMapsByReason[reason].Validate);
    }

    [Fact]
    public void Validate_AcceptsMigrationsIntoTheirDestinations()
    {
        var map = new RangeMap(
            MapId,
            8,
            [new(0, 3, A), new(4, 5, B, 2), new(6, 7, C, 3)],
            [new(4, 5, A, B, 2, MigrationPhase.Opened), new(6, 7, B, C, 3, MigrationPhase.Committed)],
            Partitions(A, B, C));
        map.Validate();

        // Bucket 5 then moves on to C before its first migration is done: its migrations chain A -> B -> C.
        var chained = map with
        {
            Ranges = [new(0, 3, A), new(4, 4, B, 2), new(5, 7, C, 4)],
            Migrations = [.. map.Migrations, new(5, 5, B, C, 4, MigrationPhase.Committed)],
        };
        chained.Validate();
    }

    [Theory]
    [InlineData("")]
    [InlineData("-")]
    [InlineData(".hidden")]
    [InlineData("a/b")]
    [InlineData("a b")]
    [InlineData("ü")]
    public void PartitionId_RejectsInvalidValues(string value)
    {
        Assert.False(PartitionId.IsValid(value));
        Assert.Throws<ArgumentException>(() => new PartitionId(value));
        Assert.Throws<ArgumentException>(() => new PartitionId(new string('a', PartitionId.MaxLength + 1)));
    }

    [Fact]
    public void Json_RoundTripsSnapshots()
    {
        var map = new RangeMap(
            MapId,
            8,
            [new(0, 3, A), new(4, 5, B, 2), new(6, 7, C, 3)],
            [new(4, 5, A, B, 2, MigrationPhase.Done), new(6, 7, B, C, 3, MigrationPhase.Committed)],
            Partitions(A, B, C).SetItem(B, new PartitionHosting("host-b", 4, "storage/b", "[::1]:6380")));
        var snapshot = new RangeMapSnapshot(9, map);

        var bytes = RangeMapJsonSerializer.Serialize(snapshot);
        var copy = RangeMapJsonSerializer.Deserialize(bytes);

        Assert.Equal(snapshot, copy);
        Assert.Equal(bytes, RangeMapJsonSerializer.Serialize(copy));
        Assert.NotEqual(snapshot, copy with { Map = copy.Map with { Migrations = [] } });
        Assert.NotEqual(snapshot, copy with { Map = copy.Map with { Id = "other" } });
        Assert.Equal(snapshot, RangeMapJsonSerializer.Deserialize(RangeMapJsonSerializer.Serialize(snapshot, writeId: "w")));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"format":1,"version":1,"bucketCount":1,"ranges":[{"first":0,"last":0,"owner":"a","assignedAt":0}],"migrations":[],"partitions":[{"id":"a","host":"h","epoch":0,"storage":"a"}]}""")]
    [InlineData("""{"format":2,"version":1,"id":"m","bucketCount":1,"ranges":[],"migrations":[],"partitions":[]}""")]
    [InlineData("""{"format":1,"version":0,"id":"m","bucketCount":1,"ranges":[{"first":0,"last":0,"owner":"a","assignedAt":0}],"migrations":[],"partitions":[{"id":"a","host":"h","epoch":0,"storage":"a"}]}""")]
    [InlineData("""{"format":1,"version":1,"id":"m","bucketCount":2,"ranges":[{"first":0,"last":0,"owner":"a","assignedAt":0}],"migrations":[],"partitions":[{"id":"a","host":"h","epoch":0,"storage":"a"}]}""")]
    [InlineData("""{"format":1,"version":1,"id":"m","bucketCount":1,"ranges":[{"first":0,"last":0,"owner":"a","assignedAt":0}],"migrations":[],"partitions":[{"id":"a","host":"h","epoch":0,"storage":"a"},{"id":"a","host":"h","epoch":0,"storage":"a"}]}""")]
    [InlineData("""{"format":1,"version":1,"id":"m","bucketCount":1,"ranges":[{"first":0,"last":0,"owner":"-","assignedAt":0}],"migrations":[],"partitions":[{"id":"a","host":"h","epoch":0,"storage":"a"}]}""")]
    public void Json_RejectsInvalidDocuments(string json)
    {
        Assert.Throws<InvalidDataException>(() => RangeMapJsonSerializer.Deserialize(System.Text.Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void Buckets_EditMigrationChainsAndMergeThemIntoRanges()
    {
        var map = new RangeMap(MapId, 8, [new(0, 3, A), new(4, 7, B)], [], Partitions(A, B, C));
        var editor = new RangeMapBuckets(map);
        foreach (var bucket in new[] { 1, 2, 3 })
        {
            editor.Move(bucket, C, 2);
        }

        // Bucket 2 moves on to B before its first migration completes, so it has a chain A -> C -> B.
        var first = editor.GetLatestMigration(2)!.Value;
        Assert.True(editor.TryAdvance(2, first, MigrationPhase.Opened));
        Assert.False(editor.TryAdvance(2, first, MigrationPhase.Sealed));
        editor.Move(2, B, 3);
        var moved = editor.ToMap();
        moved.Validate();
        Assert.Equal(
            [new(0, 0, A), new(1, 1, C, 2), new(2, 2, B, 3), new(3, 3, C, 2), new(4, 7, B)],
            moved.Ranges);
        Assert.Equal(
            [new(1, 1, A, C, 2, MigrationPhase.Committed), new(2, 2, A, C, 2, MigrationPhase.Opened), new(2, 2, C, B, 3, MigrationPhase.Committed), new(3, 3, A, C, 2, MigrationPhase.Committed)],
            moved.Migrations);

        // Migrations complete and are removed oldest first; adjacent buckets with equal migrations merge.
        editor = new RangeMapBuckets(moved);
        Assert.False(editor.TryRemove(2, first));
        Assert.True(editor.TryAdvance(2, first, MigrationPhase.Complete));
        Assert.True(editor.TryRemove(2, first));
        Assert.True(editor.TryAdvance(2, editor.GetLatestMigration(2)!.Value, MigrationPhase.Complete));
        Assert.True(editor.TryRemove(2, editor.GetLatestMigration(2)!.Value));
        foreach (var bucket in new[] { 1, 3 })
        {
            Assert.True(editor.TryAdvance(bucket, editor.GetLatestMigration(bucket)!.Value, MigrationPhase.Done));
        }

        var completed = editor.ToMap();
        completed.Validate();
        Assert.Equal([new(1, 1, A, C, 2, MigrationPhase.Done), new(3, 3, A, C, 2, MigrationPhase.Done)], completed.Migrations);
    }
}
