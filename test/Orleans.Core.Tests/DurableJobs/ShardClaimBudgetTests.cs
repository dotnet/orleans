using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.DurableJobs;
using Orleans.Hosting;
using Xunit;

namespace NonSilo.Tests.DurableJobs;

[TestCategory("BVT"), TestCategory("DurableJobs")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableJobs")]
public class ShardClaimBudgetTests
{
    private static readonly TimeSpan RampUpDuration = TimeSpan.FromMinutes(5);
    private const int InitialBudget = 2;
    private const int MaxBudget = 20;

    [Fact]
    public void ComputeClaimBudget_AtStartup_ReturnsInitialBudget()
    {
        var budget = LocalDurableJobManager.ComputeClaimBudget(
            rampUpDuration: RampUpDuration,
            initialBudget: InitialBudget,
            maxBudget: MaxBudget,
            elapsed: TimeSpan.Zero,
            totalClaimedShards: 0);

        Assert.Equal(InitialBudget, budget);
    }

    [Fact]
    public void ComputeClaimBudget_AtMidpoint_ReturnsInterpolatedBudget()
    {
        var midpoint = TimeSpan.FromTicks(RampUpDuration.Ticks / 2);

        var budget = LocalDurableJobManager.ComputeClaimBudget(
            rampUpDuration: RampUpDuration,
            initialBudget: InitialBudget,
            maxBudget: MaxBudget,
            elapsed: midpoint,
            totalClaimedShards: 0);

        // At midpoint: 2 + (int)(0.5 * (20 - 2)) = 2 + 9 = 11
        Assert.Equal(11, budget);
    }

    [Fact]
    public void ComputeClaimBudget_JustBeforeEnd_ReturnsNearMaxBudget()
    {
        var nearEnd = RampUpDuration - TimeSpan.FromMilliseconds(1);

        var budget = LocalDurableJobManager.ComputeClaimBudget(
            rampUpDuration: RampUpDuration,
            initialBudget: InitialBudget,
            maxBudget: MaxBudget,
            elapsed: nearEnd,
            totalClaimedShards: 0);

        // Should be very close to MaxBudget but computed via truncation
        Assert.True(budget >= MaxBudget - 1);
        Assert.True(budget <= MaxBudget);
    }

    [Fact]
    public void ComputeClaimBudget_AfterRampUp_ReturnsUnlimited()
    {
        var budget = LocalDurableJobManager.ComputeClaimBudget(
            rampUpDuration: RampUpDuration,
            initialBudget: InitialBudget,
            maxBudget: MaxBudget,
            elapsed: RampUpDuration,
            totalClaimedShards: 0);

        Assert.Equal(int.MaxValue, budget);
    }

    [Fact]
    public void ComputeClaimBudget_WellPastRampUp_ReturnsUnlimited()
    {
        var budget = LocalDurableJobManager.ComputeClaimBudget(
            rampUpDuration: RampUpDuration,
            initialBudget: InitialBudget,
            maxBudget: MaxBudget,
            elapsed: TimeSpan.FromHours(1),
            totalClaimedShards: 0);

        Assert.Equal(int.MaxValue, budget);
    }

    [Fact]
    public void ComputeClaimBudget_Disabled_ReturnsUnlimited()
    {
        var budget = LocalDurableJobManager.ComputeClaimBudget(
            rampUpDuration: TimeSpan.Zero,
            initialBudget: InitialBudget,
            maxBudget: MaxBudget,
            elapsed: TimeSpan.Zero,
            totalClaimedShards: 0);

        Assert.Equal(int.MaxValue, budget);
    }

    [Fact]
    public void ComputeClaimBudget_SubtractsPreviousClaims()
    {
        var budget = LocalDurableJobManager.ComputeClaimBudget(
            rampUpDuration: RampUpDuration,
            initialBudget: InitialBudget,
            maxBudget: MaxBudget,
            elapsed: TimeSpan.Zero,
            totalClaimedShards: 1);

        // 2 - 1 = 1
        Assert.Equal(1, budget);
    }

    [Fact]
    public void ComputeClaimBudget_ClaimsExceedBudget_ReturnsZero()
    {
        var budget = LocalDurableJobManager.ComputeClaimBudget(
            rampUpDuration: RampUpDuration,
            initialBudget: InitialBudget,
            maxBudget: MaxBudget,
            elapsed: TimeSpan.Zero,
            totalClaimedShards: 10);

        Assert.Equal(0, budget);
    }

    [Fact]
    public void ComputeClaimBudget_LinearProgressionOverTime()
    {
        var previousBudget = 0;
        for (var i = 0; i <= 10; i++)
        {
            var fraction = i / 10.0;
            var elapsed = TimeSpan.FromTicks((long)(RampUpDuration.Ticks * fraction));

            var budget = LocalDurableJobManager.ComputeClaimBudget(
                rampUpDuration: RampUpDuration,
                initialBudget: InitialBudget,
                maxBudget: MaxBudget,
                elapsed: elapsed,
                totalClaimedShards: 0);

            if (budget < int.MaxValue)
            {
                Assert.True(budget >= previousBudget, $"Budget should be non-decreasing: was {previousBudget} at step {i - 1}, now {budget} at step {i}");
                previousBudget = budget;
            }
        }
    }

    [Fact]
    public void ValidateConfiguration_NegativeShardClaimInitialBudget_Throws()
    {
        var options = Options.Create(new DurableJobsOptions
        {
            ShardClaimInitialBudget = -1
        });
        var validator = new DurableJobsOptionsValidator(
            NullLogger<DurableJobsOptionsValidator>.Instance,
            options);

        Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);
    }

    [Fact]
    public void ValidateConfiguration_MaxBudgetLessThanInitial_Throws()
    {
        var options = Options.Create(new DurableJobsOptions
        {
            ShardClaimInitialBudget = 10,
            ShardClaimMaxBudget = 5
        });
        var validator = new DurableJobsOptionsValidator(
            NullLogger<DurableJobsOptionsValidator>.Instance,
            options);

        Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);
    }

    [Fact]
    public void ValidateConfiguration_NegativeRampUpDuration_Throws()
    {
        var options = Options.Create(new DurableJobsOptions
        {
            ShardClaimRampUpDuration = TimeSpan.FromSeconds(-1)
        });
        var validator = new DurableJobsOptionsValidator(
            NullLogger<DurableJobsOptionsValidator>.Instance,
            options);

        Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);
    }

    [Fact]
    public void ValidateConfiguration_ValidShardClaimOptions_DoesNotThrow()
    {
        var options = Options.Create(new DurableJobsOptions
        {
            ShardClaimInitialBudget = 2,
            ShardClaimMaxBudget = 20,
            ShardClaimRampUpDuration = TimeSpan.FromMinutes(5)
        });
        var validator = new DurableJobsOptionsValidator(
            NullLogger<DurableJobsOptionsValidator>.Instance,
            options);

        validator.ValidateConfiguration();
    }

    [Fact]
    public void ValidateConfiguration_ZeroRampUpDuration_DoesNotThrow()
    {
        var options = Options.Create(new DurableJobsOptions
        {
            ShardClaimRampUpDuration = TimeSpan.Zero
        });
        var validator = new DurableJobsOptionsValidator(
            NullLogger<DurableJobsOptionsValidator>.Instance,
            options);

        validator.ValidateConfiguration();
    }
}
