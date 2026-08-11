using Orleans.Streams;
using TestExtensions;
using Xunit;

namespace UnitTests.StreamingTests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class StreamCheckpointComparersTests
{
    [Fact]
    public void Numeric_ArbitrarySizeOffsets_UsesNumericOrdering()
    {
        const string smaller = "99999999999999999999999999999999999999999999999999";
        const string larger = "100000000000000000000000000000000000000000000000000";

        Assert.True(StreamCheckpointComparers.Numeric.Compare(larger, smaller) > 0);
        Assert.True(StreamCheckpointComparers.Numeric.Compare(smaller, larger) < 0);
        Assert.Equal(0, StreamCheckpointComparers.Numeric.Compare($"+{larger}", larger));
    }

    [Theory]
    [InlineData("not-an-offset", "20")]
    [InlineData("20", "not-an-offset")]
    [InlineData("", "20")]
    [InlineData("20", "")]
    public void Numeric_WhenEitherOffsetIsMalformed_FailsSafeAsEqual(string left, string right)
    {
        Assert.Equal(0, StreamCheckpointComparers.Numeric.Compare(left, right));
    }
}
