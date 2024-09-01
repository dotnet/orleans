using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Xunit;

namespace UnitTests.ActivationRebalancingTests;

[TestCategory("Functional"), TestCategory("ActivationRebalancing")]
public class OptionsTests
{
    [Fact]
    public void ConstantsShouldNotChange()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), ActivationRebalancerOptions.DEFAULT_REBALANCER_DUE_TIME);
        Assert.Equal(TimeSpan.FromSeconds(10), ActivationRebalancerOptions.DEFAULT_SESSION_CYCLE_PERIOD);
        Assert.Equal(TimeSpan.FromSeconds(30), ActivationRebalancerOptions.DEFAULT_FAILED_SESSION_DELAY);
        Assert.Equal(3, ActivationRebalancerOptions.DEFAULT_MAX_STALE_CYCLES);
        Assert.Equal(0.001f, ActivationRebalancerOptions.DEFAULT_ENTROPY_QUANTUM);
        Assert.Equal(0.01f, ActivationRebalancerOptions.DEFAULT_MAX_ENTROPY_DEVIATION);
        Assert.Equal(0.1f, ActivationRebalancerOptions.DEFAULT_CYCLE_NUMBER_WEIGHT);
        Assert.Equal(0.1f, ActivationRebalancerOptions.DEFAULT_SILO_NUMBER_WEIGHT);
    }

    [Theory]
    [InlineData(4, 10, 0.1, 0.5, 0.5, 0.5, 2, 5)]
    [InlineData(10, 10, 0.1, 0.5, 0.5, 0.5, 2, 1)]  
    [InlineData(10, 10, 0, 0.5, 0.5, 0.5, 2, 1)]    
    [InlineData(10, 10, 0.1, -0.001, 0.5, 0.5, 2, 1)] 
    [InlineData(10, 10, 0.1, 0.011, 0.5, 0.5, 2, 1)]  
    [InlineData(10, 10, 0.1, 0.001, -0.1, 0.5, 2, 1)] 
    [InlineData(10, 10, 0.1, 0.001, 1.1, 0.5, 2, 1)]  
    [InlineData(10, 10, 0.1, 0.001, 0.5, 0, 2, 1)]
    [InlineData(10, 10, 0.1, 0.001, 0.5, 1.1, 2, 1)]  
    [InlineData(10, 10, 0.1, 0.001, 0.5, 0.5, 0, 1)]  
    [InlineData(10, 10, 0.1, 0.001, 0.5, 0.5, 1.1, 1)]
    public void InvalidOptionsShouldThrow(
        double sessionCyclePeriodSeconds,
        double publisherRefreshTimeSeconds,
        int maxStaleCycles,
        double entropyQuantum,
        double maxEntropyDeviation,
        double cycleNumberWeight,
        double siloNumberWeight,
        double failedSessionDelaySeconds)
    {
        var publisherOptions = new DeploymentLoadPublisherOptions
        {
            DeploymentLoadPublisherRefreshTime = TimeSpan.FromSeconds(publisherRefreshTimeSeconds)
        };

        var options = new ActivationRebalancerOptions
        {
            SessionCyclePeriod = TimeSpan.FromSeconds(sessionCyclePeriodSeconds),
            MaxStaleCycles = maxStaleCycles,
            EntropyQuantum = (float)entropyQuantum,
            MaxEntropyDeviation = (float)maxEntropyDeviation,
            CycleNumberWeight = (float)cycleNumberWeight,
            SiloNumberWeight = (float)siloNumberWeight,
            FailedSessionDelay = TimeSpan.FromSeconds(failedSessionDelaySeconds)
        };

        var validator = new ActivationRebalancerOptionsValidator(Options.Create(options), Options.Create(publisherOptions));
        Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);
    }
}
