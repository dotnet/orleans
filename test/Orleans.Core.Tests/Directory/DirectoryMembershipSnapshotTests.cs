using System.Collections.Immutable;
using Orleans.Runtime.GrainDirectory;
using CsCheck;
using Xunit;

namespace NonSilo.Tests.Directory;

/// <summary>
/// Tests for directory membership snapshot functionality including range ownership and ring coverage validation.
/// </summary>
[TestCategory("BVT")]
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
            return Gen.Int[1, DirectoryMembershipSnapshot.PartitionsPerSilo * 2].SelectMany(partitionCount =>
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
                uint sum = 0;
                var allRanges = new List<RingRange>();
                foreach (var member in snapshot.Members)
                {
                    Assert.Equal(snapshot.GetMemberRanges(member).Sum(range => range.Size), snapshot.GetMemberRangesByPartition(member).Sum(range => range.Size));
                    foreach (var range in snapshot.GetMemberRanges(member))
                    {
                        allRanges.Add(range);
                        sum += range.Size;
                    }
                }


                Assert.Equal(uint.MaxValue, sum);

                var allRangesCollection = RingRangeCollection.Create(allRanges);

                Assert.Equal(uint.MaxValue, allRangesCollection.Size);
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
                uint sum = 0;
                var allRanges = new List<RingRange>();
                foreach (var member in snapshot.Members)
                {
                    foreach (var range in snapshot.GetMemberRangesByPartition(member))
                    {
                        allRanges.Add(range);
                        sum += range.Size;
                    }
                }

                Assert.Equal(uint.MaxValue, sum);
                var allRangesCollection = RingRangeCollection.Create(allRanges);
                Assert.Equal(uint.MaxValue, allRangesCollection.Size);
                Assert.Equal(100f, allRangesCollection.SizePercent);
                Assert.False(allRangesCollection.IsEmpty);
                Assert.False(allRangesCollection.IsDefault);
                Assert.True(allRangesCollection.IsFull);
            });
    }
}
