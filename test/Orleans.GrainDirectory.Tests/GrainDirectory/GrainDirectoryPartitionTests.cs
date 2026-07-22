#nullable enable
using System.Collections.Immutable;
using System.Linq;
using Orleans.Runtime.GrainDirectory;
using TestExtensions;
using Xunit;

namespace UnitTests.GrainDirectory;

[TestCategory("BVT"), TestCategory("Directory")]
public sealed class GrainDirectoryPartitionTests
{
    private static readonly SiloAddress TestSiloAddress = SiloAddress.FromParsableString("127.0.0.1:11111@123");

    [Theory]
    [InlineData(SiloStatus.Active, true)]
    [InlineData(SiloStatus.Joining, true)]
    [InlineData(SiloStatus.ShuttingDown, true)]
    [InlineData(SiloStatus.Stopping, false)]
    [InlineData(SiloStatus.Dead, false)]
    public void CanInvokeClusterMember_RequiresAvailableStatus(SiloStatus status, bool expected)
    {
        var members = ImmutableDictionary<SiloAddress, ClusterMember>.Empty.Add(
            TestSiloAddress,
            new ClusterMember(TestSiloAddress, status, "TestSilo"));
        var snapshot = new ClusterMembershipSnapshot(members, new MembershipVersion(1));

        Assert.Equal(expected, DistributedGrainDirectory.CanInvokeClusterMember(snapshot, TestSiloAddress));
    }

    [Fact]
    public void GetSnapshotTransferRanges_ReturnsOnlyPreviousOwnerIntersections()
    {
        AssertRanges(
            previousOwnerRange: RingRange.Create(20, 70),
            addedRange: RingRange.Create(50, 100),
            RingRange.Create(50, 70));

        AssertRanges(
            previousOwnerRange: RingRange.Create(10, 40),
            addedRange: RingRange.Create(30, 20),
            RingRange.Create(10, 20),
            RingRange.Create(30, 40));

        AssertRanges(
            previousOwnerRange: RingRange.Full,
            addedRange: RingRange.Create(5, 15),
            RingRange.Create(5, 15));

        AssertRanges(
            previousOwnerRange: RingRange.Create(5, 15),
            addedRange: RingRange.Full,
            RingRange.Create(5, 15));

        AssertRanges(
            previousOwnerRange: RingRange.Create(10, 20),
            addedRange: RingRange.Create(30, 40));
    }

    private static void AssertRanges(RingRange previousOwnerRange, RingRange addedRange, params RingRange[] expected)
    {
        var actual = GrainDirectoryPartition.GetSnapshotTransferRanges(previousOwnerRange, addedRange).ToArray();

        Assert.Equal(expected, actual);
    }
}
