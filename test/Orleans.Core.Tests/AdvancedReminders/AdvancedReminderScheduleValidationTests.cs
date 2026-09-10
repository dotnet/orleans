#nullable enable
using Orleans.AdvancedReminders;
using Orleans.AdvancedReminders.Runtime;
using Orleans.AdvancedReminders.Runtime.ReminderService;
using Xunit;
using AdvancedReminderOptions = Orleans.AdvancedReminders.ReminderOptions;
using ReminderEntry = Orleans.AdvancedReminders.ReminderEntry;

namespace UnitTests.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders")]
public class AdvancedReminderScheduleValidationTests : AdvancedReminderServiceTestBase
{
    [Fact]
    [Trait("Category", "Stress")]
    public void ValidateCronSchedule_OneMillionEquivalentRegistrationsReuseBoundedValidationResult()
    {
        ReminderValidation.ClearCronValidationCache();
        var options = new AdvancedReminderOptions { MinimumReminderPeriod = TimeSpan.FromMinutes(30) };
        var now = new DateTime(2026, 8, 16, 0, 0, 0, DateTimeKind.Utc);

        for (var index = 0; index < 1_000_000; index++)
        {
            ReminderValidation.Validate(
                options,
                "daily",
                ReminderSchedule.Cron("0 9 * * *", "Europe/Kyiv"),
                MissedReminderAction.Skip,
                now);
        }

        Assert.Equal(1, ReminderValidation.CronValidationCacheCount);
    }

    [Fact]
    public void ValidateCronSchedule_WhenLaterIntervalIsShorterThanMinimum_Throws()
    {
        var options = new AdvancedReminderOptions { MinimumReminderPeriod = TimeSpan.FromDays(31) };
        var now = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc);

        var exception = Assert.Throws<ArgumentException>(() => ReminderValidation.Validate(
            options,
            "monthly",
            ReminderSchedule.Cron("0 0 1 * *"),
            MissedReminderAction.Skip,
            now));

        Assert.Contains("30.00:00:00", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateCronSchedule_WhenEverySecondMacroIsShorterThanMinimum_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(() => ReminderValidation.Validate(
            new AdvancedReminderOptions { MinimumReminderPeriod = TimeSpan.FromMinutes(1) },
            "every-second",
            ReminderSchedule.Cron("@every_second"),
            MissedReminderAction.Skip,
            DateTime.UtcNow));

        Assert.Contains("00:00:01", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateCronSchedule_WhenExpressionExceedsStorageLimit_Throws()
    {
        var expression = $"{string.Join(',', Enumerable.Repeat("0", 101))} * * * * *";

        var exception = Assert.Throws<ArgumentException>(() => ReminderValidation.Validate(
            new AdvancedReminderOptions(),
            "long-expression",
            ReminderSchedule.Cron(expression),
            MissedReminderAction.Skip,
            DateTime.UtcNow));

        Assert.Contains("exceeds 200 characters", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateIntervalSchedule_WhenRelativeDueTimeExceedsDateRange_Throws()
    {
        var now = DateTime.SpecifyKind(DateTime.MaxValue.AddMinutes(-1), DateTimeKind.Utc);

        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderValidation.Validate(
            new AdvancedReminderOptions { MinimumReminderPeriod = TimeSpan.FromMinutes(1) },
            "outside-date-range",
            ReminderSchedule.Interval(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(1)),
            MissedReminderAction.Skip,
            now));
    }

    [Fact]
    public void CalculateNextDue_WhenNextIntervalExceedsDateTimeRange_ReturnsNull()
    {
        var start = DateTime.MaxValue.AddMinutes(-1);
        var entry = new ReminderEntry
        {
            StartAt = start,
            NextDueUtc = start,
            Period = TimeSpan.FromMinutes(2),
        };

        Assert.Null(AdvancedReminderService.CalculateNextDue(entry, start));
    }
}
