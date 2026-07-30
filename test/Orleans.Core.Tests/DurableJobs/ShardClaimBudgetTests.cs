using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.DurableJobs;
using Orleans.Hosting;
using Xunit;

namespace NonSilo.Tests.DurableJobs;

[TestCategory("BVT"), TestCategory("DurableJobs")]
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
    public void ValidateConfiguration_NonPositiveShardStripeCount_Throws()
    {
        var options = Options.Create(new DurableJobsOptions
        {
            ShardStripeCount = 0
        });
        var validator = new DurableJobsOptionsValidator(
            NullLogger<DurableJobsOptionsValidator>.Instance,
            options);

        Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);
    }

    [Fact]
    public void ValidateConfiguration_NonPositiveJobStatusPollInterval_Throws()
    {
        var options = Options.Create(new DurableJobsOptions
        {
            JobStatusPollInterval = TimeSpan.Zero
        });
        var validator = new DurableJobsOptionsValidator(
            NullLogger<DurableJobsOptionsValidator>.Instance,
            options);

        Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);
    }

    [Theory]
    [InlineData(nameof(DurableJobsOptions.ShardActivationBufferPeriod))]
    [InlineData(nameof(DurableJobsOptions.MaxConcurrentJobsPerSilo))]
    [InlineData(nameof(DurableJobsOptions.OverloadBackoffDelay))]
    public void ValidateConfiguration_InvalidExecutionOption_Throws(string optionName)
    {
        var value = new DurableJobsOptions();
        switch (optionName)
        {
            case nameof(DurableJobsOptions.ShardActivationBufferPeriod):
                value.ShardActivationBufferPeriod = TimeSpan.FromTicks(-1);
                break;
            case nameof(DurableJobsOptions.MaxConcurrentJobsPerSilo):
                value.MaxConcurrentJobsPerSilo = 0;
                break;
            case nameof(DurableJobsOptions.OverloadBackoffDelay):
                value.OverloadBackoffDelay = TimeSpan.Zero;
                break;
        }

        var validator = new DurableJobsOptionsValidator(
            NullLogger<DurableJobsOptionsValidator>.Instance,
            Options.Create(value));

        var exception = Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);
        Assert.Contains(optionName, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(nameof(DurableJobsOptions.ShardActivationBufferPeriod))]
    [InlineData(nameof(DurableJobsOptions.JobStatusPollInterval))]
    [InlineData(nameof(DurableJobsOptions.OverloadBackoffDelay))]
    [InlineData(nameof(DurableJobsOptions.SlowStartInterval))]
    [InlineData(nameof(DurableJobsOptions.ShardBatchLingerDelay))]
    [InlineData(nameof(DurableJobsOptions.AdoptionFailureWindow))]
    public void ValidateConfiguration_TimerDelayBeyondRuntimeLimit_Throws(string optionName)
    {
        var value = new DurableJobsOptions();
        var tooLarge = DurableJobTimeLimits.MaximumTimerDelay.Add(TimeSpan.FromMilliseconds(1));
        switch (optionName)
        {
            case nameof(DurableJobsOptions.ShardActivationBufferPeriod):
                value.ShardActivationBufferPeriod = tooLarge;
                break;
            case nameof(DurableJobsOptions.JobStatusPollInterval):
                value.JobStatusPollInterval = tooLarge;
                break;
            case nameof(DurableJobsOptions.OverloadBackoffDelay):
                value.OverloadBackoffDelay = tooLarge;
                break;
            case nameof(DurableJobsOptions.SlowStartInterval):
                value.SlowStartInterval = tooLarge;
                break;
            case nameof(DurableJobsOptions.ShardBatchLingerDelay):
                value.ShardBatchLingerDelay = tooLarge;
                break;
            case nameof(DurableJobsOptions.AdoptionFailureWindow):
                value.AdoptionFailureWindow = tooLarge;
                break;
        }

        var validator = new DurableJobsOptionsValidator(
            NullLogger<DurableJobsOptionsValidator>.Instance,
            Options.Create(value));

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

    [Fact]
    public void DefaultRetryPolicy_AtMaximumDate_ClampsRetryTime()
    {
        var context = Substitute.For<IJobRunContext>();
        context.DequeueCount.Returns(1);

        var retryAt = new DurableJobsOptions().GetRetryTime(context, new InvalidOperationException(),
            new FixedTimeProvider(DateTimeOffset.MaxValue));

        Assert.Equal(DateTimeOffset.MaxValue, retryAt);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
