using Orleans.Runtime;
using TestExtensions;
using Xunit;

namespace UnitTests.General;

public class CoarseStopwatchTests
{
    [TestSuite("BVT")]
    [TestProvider("None")]
    [Theory, TestCategory("BVT")]
    [InlineData(0, 0, true)]
    [InlineData(123, 123, true)]
    [InlineData(-123, -123, true)]
    [InlineData(123, 124, false)]
    [InlineData(-123, -124, false)]
    [InlineData(123, -123, false)]
    public void Equality_UsesStoredTimestampAcrossInterfacesAndOperators(long firstTimestamp, long secondTimestamp, bool equal)
    {
        var first = CoarseStopwatch.FromTimestamp(firstTimestamp);
        var second = CoarseStopwatch.FromTimestamp(secondTimestamp);
        IEquatable<CoarseStopwatch> equatable = first;

        Assert.Equal(equal, equatable.Equals(second));
        Assert.Equal(equal, first.Equals((object)second));
        Assert.Equal(equal, first == second);
        Assert.Equal(!equal, first != second);
        Assert.Equal(equal, new HashSet<CoarseStopwatch> { first }.Contains(second));
    }
}
