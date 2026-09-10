#nullable enable
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using AdvancedReminderOptions = Orleans.AdvancedReminders.ReminderOptions;
using AdvancedReminderOptionsValidator = Orleans.AdvancedReminders.ReminderOptionsValidator;

namespace UnitTests.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders")]
public class ReminderOptionsValidatorTests
{
    [Fact]
    public void CleanupPolicies_AreDisabledByDefault()
    {
        var options = new AdvancedReminderOptions();

        Assert.False(options.DeleteReminderWhenGrainTypeIsUnavailable);
        Assert.Null(options.MaximumDeliveryAttempts);
    }

    [Fact]
    public void CleanupPolicies_CanBeConfiguredIndependently()
    {
        var unavailableTypeCleanupOnly = new AdvancedReminderOptions
        {
            DeleteReminderWhenGrainTypeIsUnavailable = true,
        };
        var failedDeliveryCleanupOnly = new AdvancedReminderOptions
        {
            MaximumDeliveryAttempts = 3,
        };

        Assert.True(unavailableTypeCleanupOnly.DeleteReminderWhenGrainTypeIsUnavailable);
        Assert.Null(unavailableTypeCleanupOnly.MaximumDeliveryAttempts);
        Assert.False(failedDeliveryCleanupOnly.DeleteReminderWhenGrainTypeIsUnavailable);
        Assert.Equal(3, failedDeliveryCleanupOnly.MaximumDeliveryAttempts);
    }

    [Fact]
    public void ValidateConfiguration_AcceptsValidOptions()
    {
        var options = new AdvancedReminderOptions
        {
            MinimumReminderPeriod = TimeSpan.FromMinutes(1),
            InitializationTimeout = TimeSpan.FromSeconds(30),
            MissedReminderGracePeriod = TimeSpan.FromSeconds(5),
            MaximumDeliveryAttempts = 3,
        };

        var validator = new AdvancedReminderOptionsValidator(NullLogger<AdvancedReminderOptionsValidator>.Instance, Options.Create(options));

        validator.ValidateConfiguration();
    }

    [Fact]
    public void ValidateConfiguration_RejectsNegativeMinimumPeriod()
    {
        var validator = CreateValidator(new AdvancedReminderOptions { MinimumReminderPeriod = TimeSpan.FromSeconds(-1) });

        Assert.Throws<OrleansConfigurationException>(() => validator.ValidateConfiguration());
    }

    [Fact]
    public void ValidateConfiguration_RejectsNonPositiveInitializationTimeout()
    {
        var validator = CreateValidator(new AdvancedReminderOptions { InitializationTimeout = TimeSpan.Zero });

        Assert.Throws<OrleansConfigurationException>(() => validator.ValidateConfiguration());
    }

    [Fact]
    public void ValidateConfiguration_RejectsNonPositiveMissedReminderGracePeriod()
    {
        var validator = CreateValidator(new AdvancedReminderOptions { MissedReminderGracePeriod = TimeSpan.Zero });

        Assert.Throws<OrleansConfigurationException>(() => validator.ValidateConfiguration());
    }

    [Theory]
    [InlineData(nameof(AdvancedReminderOptions.InitializationTimeout))]
    public void ValidateConfiguration_RejectsTimerDelayBeyondRuntimeLimit(string optionName)
    {
        var options = new AdvancedReminderOptions();
        var tooLarge = TimeSpan.FromMilliseconds(uint.MaxValue);
        switch (optionName)
        {
            case nameof(AdvancedReminderOptions.InitializationTimeout):
                options.InitializationTimeout = tooLarge;
                break;
        }

        Assert.Throws<OrleansConfigurationException>(() => CreateValidator(options).ValidateConfiguration());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void ValidateConfiguration_RejectsNonPositiveMaximumDeliveryAttempts(int maximumDeliveryAttempts)
    {
        var validator = CreateValidator(new AdvancedReminderOptions { MaximumDeliveryAttempts = maximumDeliveryAttempts });

        Assert.Throws<OrleansConfigurationException>(() => validator.ValidateConfiguration());
    }

    private static AdvancedReminderOptionsValidator CreateValidator(AdvancedReminderOptions options)
        => new(NullLogger<AdvancedReminderOptionsValidator>.Instance, Options.Create(options));
}
