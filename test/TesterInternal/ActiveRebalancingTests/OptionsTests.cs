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
        Assert.Equal(10_000u, ActiveRebalancingOptions.DEFAULT_TOP_HEAVIEST_COMMUNICATION_LINKS);
        Assert.Equal(TimeSpan.FromMinutes(1), ActiveRebalancingOptions.DEFAULT_MINUMUM_REBALANCING_DUE_TIME);
        Assert.Equal(TimeSpan.FromMinutes(2), ActiveRebalancingOptions.DEFAULT_MAXIMUM_REBALANCING_DUE_TIME);
        Assert.Equal(TimeSpan.FromMinutes(2), ActiveRebalancingOptions.DEFAULT_REBALANCING_PERIOD);
        Assert.Equal(TimeSpan.FromMinutes(1), ActiveRebalancingOptions.DEFAULT_RECOVERY_PERIOD);
    }

    [Theory]
    [InlineData(0, 1, 1, 2, 1)]
    [InlineData(1, 0, 1, 2, 1)]
    [InlineData(1, 1, 0, 2, 1)]
    [InlineData(1, 1, 1, 0, 1)]
    [InlineData(1, 1, 1, 2, 0)]
    [InlineData(1, 2, 1, 2, 1)]
    [InlineData(1, 2, 1, 1, 2)]
    public void InvalidOptionsShouldThrow(
        uint topHeaviestCommunicationLinks,
        int minimumRebalancingDueTimeMinutes,
        int maximumRebalancingDueTimeMinutes,
        int rebalancingPeriodMinutes,
        int recoveryPeriodMinutes)
    {
        var options = new ActiveRebalancingOptions
        {
            TopHeaviestCommunicationLinks = topHeaviestCommunicationLinks,
            MinimumRebalancingDueTime = TimeSpan.FromMinutes(minimumRebalancingDueTimeMinutes),
            MaximumRebalancingDueTime = TimeSpan.FromMinutes(maximumRebalancingDueTimeMinutes),
            RebalancingPeriod = TimeSpan.FromMinutes(rebalancingPeriodMinutes),
            RecoveryPeriod = TimeSpan.FromMinutes(recoveryPeriodMinutes)
        };

        var validator = new ActiveRebalancingOptionsValidator(Options.Create(options));
        Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);
    }
}