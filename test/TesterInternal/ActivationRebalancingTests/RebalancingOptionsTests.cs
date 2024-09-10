using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Xunit;

namespace UnitTests.ActivationRebalancingTests;

[TestCategory("Functional"), TestCategory("ActivationRebalancing")]
public class RebalancingOptionsTests
{
    [Fact]
    public void ConstantsShouldNotChange()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), ActivationRebalancerOptions.DEFAULT_REBALANCER_DUE_TIME);
        Assert.Equal(TimeSpan.FromSeconds(15), ActivationRebalancerOptions.DEFAULT_SESSION_CYCLE_PERIOD);
        Assert.Equal(3, ActivationRebalancerOptions.DEFAULT_MAX_STALE_CYCLES);
        Assert.Equal(0.001d, ActivationRebalancerOptions.DEFAULT_ENTROPY_QUANTUM);
        Assert.Equal(0.01d, ActivationRebalancerOptions.DEFAULT_ALLOWED_ENTROPY_DEVIATION);
        Assert.Equal(0.1d, ActivationRebalancerOptions.DEFAULT_CYCLE_NUMBER_WEIGHT);
        Assert.Equal(0.1d, ActivationRebalancerOptions.DEFAULT_SILO_NUMBER_WEIGHT);
    }

    [Theory]
    [InlineData(999, 1, 0.1, 0.5, 0.5, 0.5, 2)]
    [InlineData(1000, 1, 0.1, 0.5, 0.5, 0.5, 2)]  
    [InlineData(1000, 1, 0, 0.5, 0.5, 0.5, 2)]    
    [InlineData(1000, 1, 0.1, -0.001, 0.5, 0.5, 2)] 
    [InlineData(1000, 1, 0.1, 0.011, 0.5, 0.5, 2)]  
    [InlineData(1000, 1, 0.1, 0.001, -0.1, 0.5, 1.1)] 
    [InlineData(1000, 1, 0.1, 0.001, 1.1, 0.5, 2)]  
    [InlineData(1000, 1, 0.1, 0.001, 0.5, 0, 2)]
    [InlineData(1000, 1, 0.1, 0.001, 0.5, 1.1, 2)]  
    [InlineData(1000, 1, 0.1, 0.001, 0.5, 0.5, -0.1)]  
    [InlineData(1000, 1, 0.1, 0.001, 0.5, 0.5, 1.1)]
    public void InvalidOptionsShouldThrow(
        double sessionCyclePeriodMilliseconds,
        double publisherRefreshTimeSeconds,
        int maxStaleCycles,
        double entropyQuantum,
        double allowedEntropyDeviation,
        double cycleNumberWeight,
        double siloNumberWeight)
    {
        var publisherOptions = new DeploymentLoadPublisherOptions
        {
            DeploymentLoadPublisherRefreshTime = TimeSpan.FromSeconds(publisherRefreshTimeSeconds)
        };

        var options = new ActivationRebalancerOptions
        {
            SessionCyclePeriod = TimeSpan.FromMilliseconds(sessionCyclePeriodMilliseconds),
            MaxStaleCycles = maxStaleCycles,
            EntropyQuantum = entropyQuantum,
            AllowedEntropyDeviation = allowedEntropyDeviation,
            CycleNumberWeight = cycleNumberWeight,
            SiloNumberWeight = siloNumberWeight
        };

        var validator = new ActivationRebalancerOptionsValidator(Options.Create(options), Options.Create(publisherOptions));
        Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);
    }
}
