using System.Collections.Immutable;
using Orleans.Runtime.GrainDirectory;
using CsCheck;
using Xunit;
using Orleans.Configuration;

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

    private static readonly Gen<DirectoryMembershipSnapshot> GenDirectoryMembershipSnapshot =
        GenClusterMembershipSnapshot.SelectMany(snapshot => Gen.UInt.Array[ConsistentRingOptions.DEFAULT_NUM_VIRTUAL_RING_BUCKETS].Array[snapshot.Members.Count].Select(hashes => 
    {
        var i = 0;
        return new DirectoryMembershipSnapshot(snapshot, null!, (_, _) => hashes[i++]);
    }));

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
