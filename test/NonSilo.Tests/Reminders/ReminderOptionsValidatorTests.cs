using System;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Runtime;
using Xunit;

namespace NonSilo.Tests.Reminders;

[TestCategory("Reminders")]
public class ReminderOptionsValidatorTests
{
    [Fact]
    public void ValidateConfiguration_AcceptsValidAdaptiveOptions()
    {
        var options = new ReminderOptions
        {
            MinimumReminderPeriod = TimeSpan.FromMinutes(1),
            InitializationTimeout = TimeSpan.FromSeconds(30),
            LookAheadWindow = TimeSpan.FromMinutes(3),
            PollInterval = TimeSpan.FromSeconds(5),
            BaseBucketSize = 1,
        };

        var validator = new ReminderOptionsValidator(NullLogger<ReminderOptionsValidator>.Instance, Options.Create(options));

        validator.ValidateConfiguration();
    }

    [Fact]
    public void ValidateConfiguration_RejectsNegativeMinimumPeriod()
    {
        var validator = CreateValidator(new ReminderOptions { MinimumReminderPeriod = TimeSpan.FromSeconds(-1) });

        Assert.Throws<OrleansConfigurationException>(() => validator.ValidateConfiguration());
    }

    [Fact]
    public void ValidateConfiguration_RejectsNonPositiveInitializationTimeout()
    {
        var validator = CreateValidator(new ReminderOptions { InitializationTimeout = TimeSpan.Zero });

        Assert.Throws<OrleansConfigurationException>(() => validator.ValidateConfiguration());
    }

    [Fact]
    public void ValidateConfiguration_RejectsNonPositiveLookAheadWindow()
    {
        var validator = CreateValidator(new ReminderOptions { LookAheadWindow = TimeSpan.Zero });

        Assert.Throws<OrleansConfigurationException>(() => validator.ValidateConfiguration());
    }

    [Fact]
    public void ValidateConfiguration_RejectsNonPositivePollInterval()
    {
        var validator = CreateValidator(new ReminderOptions { PollInterval = TimeSpan.Zero });

        Assert.Throws<OrleansConfigurationException>(() => validator.ValidateConfiguration());
    }

    [Fact]
    public void ValidateConfiguration_RejectsZeroBaseBucketSize()
    {
        var validator = CreateValidator(new ReminderOptions { BaseBucketSize = 0 });

        Assert.Throws<OrleansConfigurationException>(() => validator.ValidateConfiguration());
    }

    private static ReminderOptionsValidator CreateValidator(ReminderOptions options)
    {
        return new ReminderOptionsValidator(NullLogger<ReminderOptionsValidator>.Instance, Options.Create(options));
    }
}
