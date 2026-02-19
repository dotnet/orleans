using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Xunit;

namespace UnitTests.ActivationRebalancingTests;

/// <summary>
/// Tests for activation rebalancer configuration options validation.
/// </summary>
[TestCategory("Functional"), TestCategory("ActivationRebalancing")]
public class RebalancingOptionsTests
{
    [Fact]
    public void ConstantsShouldNotChange()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), ActivationRebalancerOptions.DEFAULT_REBALANCER_DUE_TIME);
        Assert.Equal(TimeSpan.FromSeconds(15), ActivationRebalancerOptions.DEFAULT_SESSION_CYCLE_PERIOD);
        Assert.Equal(3, ActivationRebalancerOptions.DEFAULT_MAX_STAGNANT_CYCLES);
        Assert.Equal(0.0001d, ActivationRebalancerOptions.DEFAULT_ENTROPY_QUANTUM);
        Assert.Equal(0.0001d, ActivationRebalancerOptions.DEFAULT_ALLOWED_ENTROPY_DEVIATION);
        Assert.Equal(0.1d, ActivationRebalancerOptions.DEFAULT_CYCLE_NUMBER_WEIGHT);
        Assert.Equal(0.1d, ActivationRebalancerOptions.DEFAULT_SILO_NUMBER_WEIGHT);
        Assert.Equal(0.1d, ActivationRebalancerOptions.MAX_SCALED_ENTROPY_DEVIATION);
        Assert.Equal(10_000, ActivationRebalancerOptions.DEFAULT_SCALED_ENTROPY_DEVIATION_ACTIVATION_THRESHOLD);
        Assert.Equal(int.MaxValue, ActivationRebalancerOptions.DEFAULT_ACTIVATION_MIGRATION_COUNT_LIMIT);
        Assert.True(ActivationRebalancerOptions.DEFAULT_SCALE_ALLOWED_ENTROPY_DEVIATION);
    }

    [Theory]
    [InlineData(1000, 1, 0, 0.2, 0.2, 0, -0.1, 0, 999)]
    [InlineData(2000, 2, -1, 0.05, 0.05, 0.5, 1.1, 10, 500)]
    [InlineData(1000, 1, 2, 0, 0.05, 0.5, 0.5, 10, 999)]    
    [InlineData(1000, 1, 2, 0.05, 0, 0.5, 0.5, 10, 999)]    
    [InlineData(1000, 1, 2, 0.05, 0.05, -0.1, 0.5, 10, 999)]
    [InlineData(1000, 1, 2, 0.05, 0.05, 0.5, 1.1, 10, 999)] 
    [InlineData(1000, 1, 2, 0.05, 0.05, 0.5, 0.5, 0, 999)]  
    [InlineData(1000, 1, 2, 0.05, 0.05, 0.5, 0.5, 10, 999)] 

    public void InvalidOptionsShouldThrow(
        int sessionCyclePeriodMilliseconds,
        int publisherRefreshTimeSeconds,
        int maxStagnantCycles,
        double entropyQuantum,
        double allowedEntropyDeviation,
        double cycleNumberWeight,
        double siloNumberWeight,
        int activationMigrationCountLimit,
        int scaledEntropyDeviationActivationThreshold)
    {
        var publisherOptions = new DeploymentLoadPublisherOptions
        {
            DeploymentLoadPublisherRefreshTime = TimeSpan.FromSeconds(publisherRefreshTimeSeconds)
        };

        var options = new ActivationRebalancerOptions
        {
            SessionCyclePeriod = TimeSpan.FromMilliseconds(sessionCyclePeriodMilliseconds),
            MaxStagnantCycles = maxStagnantCycles,
            EntropyQuantum = entropyQuantum,
            AllowedEntropyDeviation = allowedEntropyDeviation,
            CycleNumberWeight = cycleNumberWeight,
            SiloNumberWeight = siloNumberWeight,
            ActivationMigrationCountLimit = activationMigrationCountLimit,
            ScaledEntropyDeviationActivationThreshold = scaledEntropyDeviationActivationThreshold
        };

        var validator = new ActivationRebalancerOptionsValidator(Options.Create(options), Options.Create(publisherOptions));
        Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);
    }
}
