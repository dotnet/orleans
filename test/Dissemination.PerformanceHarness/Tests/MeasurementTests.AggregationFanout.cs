using Xunit;

namespace Orleans.Dissemination.PerformanceHarness;

public sealed partial class MeasurementTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(4, 4)]
    [InlineData(32, 8)]
    public void AggregationFanoutReportsTheCandidateRoutingClamp(int members, int expected)
    {
        var result = AggregationFanout.Read(new ModernOverlay(), true, members,
            () => throw new InvalidOperationException("A modern aggregation tree must use its own fanout."));
        Assert.Equal(new(expected, "Overlay.AggregationFanOutFactor"), result);
    }

    [Fact]
    public void AggregationFanoutIdentifiesLegacyCapabilityAndPreservesMembershipRouting()
    {
        Assert.Equal(new(7, "LegacyAggregationMembershipFanout"), AggregationFanout.Read(new object(), true, 32, () => 7));
        Assert.Equal(new(5, "MembershipFanout"), AggregationFanout.Read(new ModernOverlay(), false, 32, () => 5));
    }

    [Fact]
    public void AggregationFanoutRejectsIncompatibleCandidateCapability()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => AggregationFanout.Read(new IncompatibleOverlay(), true, 32, () => 7));
        Assert.Contains("public int", exception.Message, StringComparison.Ordinal);
        Assert.Contains("AggregationFanOutFactor", exception.Message, StringComparison.Ordinal);
    }

    private sealed class ModernOverlay
    {
        public int AggregationFanOutFactor => 8;
    }

    private sealed class IncompatibleOverlay
    {
        public string AggregationFanOutFactor => "8";
    }
}
