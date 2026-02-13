using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Orleans.Configuration;
using Xunit;

namespace UnitTests.ActivationRepartitioningTests;

/// <summary>
/// Tests for activation repartitioner configuration options validation.
/// </summary>
[TestCategory("Functional"), TestCategory("ActivationRepartitioning")]
public class OptionsTests
{
    [Fact]
    public void ConstantsShouldNotChange()
    {
        Assert.True(ActivationRepartitionerOptions.DEFAULT_ANCHORING_FILTER_ENABLED);
        Assert.Equal(0.01d, ActivationRepartitionerOptions.DEFAULT_PROBABILISTIC_FILTERING_MAX_ALLOWED_ERROR);
        Assert.Equal(10_000, ActivationRepartitionerOptions.DEFAULT_MAX_EDGE_COUNT);
        Assert.Equal(TimeSpan.FromMinutes(1), ActivationRepartitionerOptions.DEFAULT_MINUMUM_ROUND_PERIOD);
        Assert.Equal(TimeSpan.FromMinutes(2), ActivationRepartitionerOptions.DEFAULT_MAXIMUM_ROUND_PERIOD);
        Assert.Equal(TimeSpan.FromMinutes(1), ActivationRepartitionerOptions.DEFAULT_RECOVERY_PERIOD);
    }

    [Theory]
    [InlineData(0, 1, 1, 1, 1, 0.01d)]
    [InlineData(1, 0, 1, 1, 1, 0.01d)]
    [InlineData(1, 1, 0, 1, 1, 0.01d)]
    [InlineData(1, 1, 1, 0, 1, 0.01d)]
    [InlineData(1, 1, 1, 1, -1, 0.01d)]
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
        var options = new ActivationRepartitionerOptions
        {
            MaxEdgeCount = topHeaviestCommunicationLinks,
            MinRoundPeriod = TimeSpan.FromMinutes(minRebalancingPeriodMinutes),
            MaxRoundPeriod = TimeSpan.FromMinutes(maxRebalancingPeriodMinutes),
            RecoveryPeriod = TimeSpan.FromMinutes(recoveryPeriodMinutes),
            MaxUnprocessedEdges = maxUnprocessedEdges,
            ProbabilisticFilteringMaxAllowedErrorRate = probabilisticFilteringMaxAllowedErrorRate
        };

        var validator = new ActivationRepartitionerOptionsValidator(Options.Create(options));
        Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);
    }
}