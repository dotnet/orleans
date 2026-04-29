#nullable enable
using System.Linq;
using Orleans.Runtime.GrainDirectory;
using TestExtensions;
using Xunit;

namespace UnitTests.GrainDirectory;

[TestCategory("BVT"), TestCategory("Directory")]
public sealed class GrainDirectoryPartitionTests
{
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
