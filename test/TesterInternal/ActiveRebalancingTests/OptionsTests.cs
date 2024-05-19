using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Orleans.Configuration;
using Xunit;

namespace UnitTests.ActiveRebalancingTests;

[TestCategory("Functional"), TestCategory("ActiveRebalancing")]
public class OptionsTests
{
    [Fact]
    public void ConstantsShouldNotChange()
    {
        Assert.True(ActiveRebalancingOptions.DEFAULT_ANCHORING_FILTER_ENABLED);
        Assert.Equal(0.01d, ActiveRebalancingOptions.DEFAULT_PROBABILISTIC_FILTERING_MAX_ALLOWED_ERROR);
        Assert.Equal(10_000, ActiveRebalancingOptions.DEFAULT_MAX_EDGE_COUNT);
        Assert.Equal(TimeSpan.FromMinutes(1), ActiveRebalancingOptions.DEFAULT_MINUMUM_REBALANCING_PERIOD);
        Assert.Equal(TimeSpan.FromMinutes(2), ActiveRebalancingOptions.DEFAULT_MAXIMUM_REBALANCING_PERIOD);
        Assert.Equal(TimeSpan.FromMinutes(1), ActiveRebalancingOptions.DEFAULT_RECOVERY_PERIOD);
    }

    [Theory]
    [InlineData(0, 1, 1, 1, 1, 0.01d)]
    [InlineData(1, 0, 1, 1, 1, 0.01d)]
    [InlineData(1, 1, 0, 1, 1, 0.01d)]
    [InlineData(1, 1, 1, 0, 1, 0.01d)]
    [InlineData(1, 1, 1, 1, 0, 0.01d)]
    [InlineData(1, 1, 2, 1, 1, 0.01d)]
    [InlineData(1, 1, 2, 1, 2, 0.01d)]
    [InlineData(1, 1, 2, 1, 2, 0.1d)]
    public void InvalidOptionsShouldThrow(
        int topHeaviestCommunicationLinks,
        int maxUnprocessedEdges,
        int minRebalancingPeriodMinutes,
        int maxRebalancingPeriodMinutes,
        int recoveryPeriodMinutes,
        double probabilisticFilteringMaxAllowedErrorRate)
    {
        var options = new ActiveRebalancingOptions
        {
            MaxEdgeCount = topHeaviestCommunicationLinks,
            MinRebalancingPeriod = TimeSpan.FromMinutes(minRebalancingPeriodMinutes),
            MaxRebalancingPeriod = TimeSpan.FromMinutes(maxRebalancingPeriodMinutes),
            RecoveryPeriod = TimeSpan.FromMinutes(recoveryPeriodMinutes),
            MaxUnprocessedEdges = maxUnprocessedEdges,
            ProbabilisticFilteringMaxAllowedErrorRate = probabilisticFilteringMaxAllowedErrorRate
        };

        var validator = new ActiveRebalancingOptionsValidator(Options.Create(options));
        Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);
    }
}