using System.Collections.Immutable;
using Orleans.Configuration;
using Orleans.Runtime.GrainDirectory;
using CsCheck;
using Xunit;

namespace NonSilo.Tests.Directory;

/// <summary>
/// Tests for directory membership snapshot functionality including range ownership and ring coverage validation.
/// </summary>
[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("GrainDirectory")]
public sealed class DirectoryMembershipSnapshotTests
{
    private static readonly Gen<ClusterMembershipSnapshot> GenClusterMembershipSnapshot = Gen.Select(Gen.UInt, Gen.Enum<SiloStatus>(), (hash, status) => (hash, status))
        .Array[Gen.Int[1, 30]].Select((tuple) =>
    {
        var dict = ImmutableDictionary.CreateBuilder<SiloAddress, ClusterMember>();
        var port = 1;
        foreach (var item in tuple)
        {
            var (hash, status) = item;
            var addr = SiloAddress.New(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port++), (int)hash);
            dict.Add(addr, new ClusterMember(addr, status, $"Silo_{hash}"));
        }

        return new ClusterMembershipSnapshot(dict.ToImmutable(), new(1));
    });

    private sealed record DirectoryMembershipSnapshotTestCase(DirectoryMembershipSnapshot Snapshot, uint[][] HashesByMember);

    private static readonly Gen<DirectoryMembershipSnapshotTestCase> GenDirectoryMembershipSnapshotTestCase =
        GenClusterMembershipSnapshot.SelectMany(snapshot =>
        {
            var activeMemberCount = snapshot.Members.Count(static member => member.Value.Status == SiloStatus.Active);
            return Gen.Int[1, GrainDirectoryOptions.DEFAULT_PARTITIONS_PER_SILO * 2].SelectMany(partitionCount =>
                Gen.UInt.Array[partitionCount].Array[activeMemberCount].Select(hashes =>
                {
                    var i = 0;
                    return new DirectoryMembershipSnapshotTestCase(
                        new DirectoryMembershipSnapshot(snapshot, null!, partitionCount, (_, _) => hashes[i++]),
                        hashes);
                }));
        });

    private static readonly Gen<DirectoryMembershipSnapshot> GenDirectoryMembershipSnapshot =
        GenDirectoryMembershipSnapshotTestCase.Select(static testCase => testCase.Snapshot);

    [Fact]
    public void GetOwnerTest()
    {
        // As long as the cluster has at least one member, we should be able to find an owner.
        Gen.Select(GenDirectoryMembershipSnapshot, Gen.UInt)
            .Sample((snapshot, hash) => Assert.Equal(snapshot.Members.Length > 0, snapshot.TryGetOwner(hash, out var owner, out _)));
    }

    [Fact]
    public void MembersDoNotIntersectTest()
    {
        // Member ranges should not intersect.
        GenDirectoryMembershipSnapshot.Where(s => s.Members.Length > 0)
            .Sample(snapshot =>
            {
                foreach (var range in snapshot.RangeOwners)
                {
                    foreach (var otherRange in snapshot.RangeOwners)
                    {
                        if (range == otherRange)
                        {
                            continue;
                        }

                        Assert.False(range.Range.Intersects(otherRange.Range));
                    }
                }
            });
    }

    [Fact]
    public void GetRangeReturnsRangeForRequestedPartition()
    {
        GenDirectoryMembershipSnapshotTestCase.Where(testCase => testCase.Snapshot.Members.Length > 0)
            .Sample(testCase =>
            {
                var snapshot = testCase.Snapshot;

                for (var memberIndex = 0; memberIndex < snapshot.Members.Length; memberIndex++)
                {
                    var member = snapshot.Members[memberIndex];
                    for (var partitionIndex = 0; partitionIndex < snapshot.PartitionCount; partitionIndex++)
                    {
                        var expectedRange = GetExpectedRange(testCase.HashesByMember, memberIndex, partitionIndex);
                        Assert.Equal(expectedRange, snapshot.GetRange(member, partitionIndex));
                    }
                }
            });
    }

    [Fact]
    public void GetRangeReturnsEmptyForPartitionMissingFromSnapshot()
    {
        var member = SiloAddress.New(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 1), 1);

        Assert.Equal(RingRange.Empty, DirectoryMembershipSnapshot.Default.GetRange(member, 2));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(30)]
    public void DefaultPartitionBoundariesPreserveCurrentMapping(int partitionCount)
    {
        var member = SiloAddress.New(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 11111), 1);
        var options = new GrainDirectoryOptions();
        var expected = partitionCount == 1
            ? [unchecked((uint)member.GetConsistentHashCode())]
            : member.GetUniformHashCodes(partitionCount);

        Assert.Equal(expected, options.GetPartitionBoundaries(member, partitionCount));
    }

    [Fact]
    public void LegacyPartitionBoundariesPreserveCachedHashOrder()
    {
        var member = SiloAddress.New(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 11112), 1);
        uint[] generated = [300, 100, 200];
        member.InternalSetUniformHashCodes(generated);

        var boundaries = GrainDirectoryOptions.GetLegacyPartitionBoundaries(member, generated.Length);

        Assert.Equal([100u, 200u, 300u], boundaries);
        Assert.Equal([300u, 100u, 200u], member.GetUniformHashCodes(generated.Length));
        Assert.NotSame(generated, boundaries);
    }

    [Fact]
    public void LegacyPartitionBoundariesMatchOrleans10_1PartitionRanges()
    {
        const int partitionCount = 30;
        var members = Enumerable.Range(0, 3)
            .Select(index => SiloAddress.New(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 11120 + index), 1))
            .Order()
            .ToArray();
        var membership = new ClusterMembershipSnapshot(
            members.ToImmutableDictionary(member => member, member => new ClusterMember(member, SiloStatus.Active, member.ToString())),
            new(1));
        var options = new GrainDirectoryOptions
        {
            PartitionsPerSilo = partitionCount,
            GetPartitionBoundaries = GrainDirectoryOptions.GetLegacyPartitionBoundaries
        };
        var snapshot = new DirectoryMembershipSnapshot(membership, null!, options.PartitionsPerSilo, options.GetPartitionBoundaries);
        var legacyHashes = members.Select(member => member.GetUniformHashCodes(partitionCount).Order().ToArray()).ToArray();

        Assert.Equal(members, snapshot.Members);
        Assert.Equal(members.Length * partitionCount, snapshot.RangeOwners.Count);
        for (var memberIndex = 0; memberIndex < members.Length; memberIndex++)
        {
            for (var partitionIndex = 0; partitionIndex < partitionCount; partitionIndex++)
            {
                var expectedRange = GetExpectedRange(legacyHashes, memberIndex, partitionIndex);
                Assert.Equal(expectedRange, snapshot.GetRange(members[memberIndex], partitionIndex));
                Assert.True(snapshot.TryGetOwner(expectedRange.End, out var owner, out _));
                Assert.Equal(members[memberIndex], owner);
                Assert.Contains((expectedRange, memberIndex, partitionIndex), snapshot.RangeOwners);
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public void PartitionBoundariesRejectIncorrectCount(int returnedCount)
    {
        var member = SiloAddress.New(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 11113), 1);
        var membership = new ClusterMembershipSnapshot(
            ImmutableDictionary<SiloAddress, ClusterMember>.Empty.Add(member, new ClusterMember(member, SiloStatus.Active, "Silo")),
            new(1));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new DirectoryMembershipSnapshot(membership, null!, 3, (_, _) => new uint[returnedCount]));

        Assert.Contains("exactly 3 boundaries", exception.Message);
        Assert.Contains(member.ToString(), exception.Message);
    }

    [Fact]
    public void PartitionBoundariesRejectNullResult()
    {
        var member = SiloAddress.New(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 11114), 1);
        var membership = new ClusterMembershipSnapshot(
            ImmutableDictionary<SiloAddress, ClusterMember>.Empty.Add(member, new ClusterMember(member, SiloStatus.Active, "Silo")),
            new(1));

        Assert.Throws<InvalidOperationException>(() => new DirectoryMembershipSnapshot(membership, null!, 3, (_, _) => null!));
    }

    [Fact]
    public void PartitionBoundariesRejectNullFunction()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new DirectoryMembershipService(null!, null!, null!, 3, null!));
    }

    private static RingRange GetExpectedRange(uint[][] hashesByMember, int memberIndex, int partitionIndex)
    {
        var boundaries = new List<(uint Hash, int MemberIndex, int PartitionIndex)>();
        for (var i = 0; i < hashesByMember.Length; i++)
        {
            var hashes = hashesByMember[i];
            for (var j = 0; j < hashes.Length; j++)
            {
                boundaries.Add((hashes[j], i, j));
            }
        }

        boundaries.Sort(static (left, right) =>
        {
            var hashCompare = left.Hash.CompareTo(right.Hash);
            if (hashCompare != 0)
            {
                return hashCompare;
            }

            var partitionCompare = left.PartitionIndex.CompareTo(right.PartitionIndex);
            if (partitionCompare != 0)
            {
                return partitionCompare;
            }

            return left.MemberIndex.CompareTo(right.MemberIndex);
        });

        for (var i = 1; i < boundaries.Count;)
        {
            if (boundaries[i].Hash == boundaries[i - 1].Hash)
            {
                boundaries.RemoveAt(i);
            }
            else
            {
                i++;
            }
        }

        var boundaryIndex = boundaries.FindIndex(boundary => boundary.MemberIndex == memberIndex && boundary.PartitionIndex == partitionIndex);
        if (boundaryIndex < 0)
        {
            return RingRange.Empty;
        }

        if (boundaries.Count == 1)
        {
            return RingRange.Full;
        }

        var current = boundaries[boundaryIndex];
        var next = boundaries[(boundaryIndex + 1) % boundaries.Count];
        return RingRange.Create(current.Hash, next.Hash);
    }

    [Fact]
    public void ViewCoversRingTest()
    {
        // The union of all member ranges should cover the entire ring.
        GenDirectoryMembershipSnapshot.Where(s => s.Members.Length > 0)
            .Sample(snapshot =>
            {
                ulong sum = 0;
                var allRanges = new List<RingRange>();
                foreach (var member in snapshot.Members)
                {
                    Assert.Equal(
                        snapshot.GetMemberRanges(member).Aggregate(0UL, static (sum, range) => sum + range.Size),
                        snapshot.GetMemberRangesByPartition(member).Aggregate(0UL, static (sum, range) => sum + range.Size));
                    foreach (var range in snapshot.GetMemberRanges(member))
                    {
                        allRanges.Add(range);
                        sum += range.Size;
                    }
                }

                Assert.Equal(1UL << 32, sum);

                var allRangesCollection = RingRangeCollection.Create(allRanges);

                Assert.Equal(1UL << 32, allRangesCollection.Size);
                Assert.Equal(100f, allRangesCollection.SizePercent);
                Assert.False(allRangesCollection.IsEmpty);
                Assert.False(allRangesCollection.IsDefault);
                Assert.True(allRangesCollection.IsFull);
            });
    }

    [Fact]
    public void MemberRangesCoverRingTest()
    {
        // The union of all member ranges should cover the entire ring.
        GenDirectoryMembershipSnapshot.Where(s => s.Members.Length > 0)
            .Sample(snapshot =>
            {
                ulong sum = 0;
                var allRanges = new List<RingRange>();
                foreach (var member in snapshot.Members)
                {
                    foreach (var range in snapshot.GetMemberRangesByPartition(member))
                    {
                        allRanges.Add(range);
                        sum += range.Size;
                    }
                }

                Assert.Equal(1UL << 32, sum);
                var allRangesCollection = RingRangeCollection.Create(allRanges);
                Assert.Equal(1UL << 32, allRangesCollection.Size);
                Assert.Equal(100f, allRangesCollection.SizePercent);
                Assert.False(allRangesCollection.IsEmpty);
                Assert.False(allRangesCollection.IsDefault);
                Assert.True(allRangesCollection.IsFull);
            });
    }
}
