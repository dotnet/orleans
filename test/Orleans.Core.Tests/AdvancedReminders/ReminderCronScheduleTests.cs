#nullable enable
using Orleans.AdvancedReminders;
using Orleans.AdvancedReminders.Cron.Internal;
using Xunit;

namespace UnitTests.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders")]
public class ReminderCronScheduleTests
{
    [Fact]
    public void Schedule_Parse_WithoutTimeZone_DefaultsToUtc()
    {
        var schedule = ReminderCronSchedule.Parse("0 9 * * *");
        var next = schedule.GetNextOccurrence(new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc));

        Assert.Equal(TimeZoneInfo.Utc.Id, schedule.TimeZone.Id);
        Assert.Null(schedule.TimeZoneId);
        Assert.Equal(new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Schedule_Parse_WithExpressionAndZone_NormalizesStorageIdAndUsesLocalSchedule()
    {
        var zone = AdvancedReminderTimeZoneTestHelper.GetNepalTimeZone();
        var expression = ReminderCronExpression.Parse("0 9 * * *");
        var schedule = ReminderCronSchedule.Parse(expression, zone);
        var fromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var next = schedule.GetNextOccurrence(fromUtc);
        var occurrences = schedule.GetOccurrences(fromUtc, new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc)).ToArray();

        Assert.Equal(ReminderCronSchedule.NormalizeTimeZoneIdForStorage(zone), schedule.TimeZoneId);
        Assert.Equal(AdvancedReminderTimeZoneTestHelper.ToUtc(zone, 2026, 1, 1, 9, 0, 0), next);
        Assert.Equal(
            [
                AdvancedReminderTimeZoneTestHelper.ToUtc(zone, 2026, 1, 1, 9, 0, 0),
                AdvancedReminderTimeZoneTestHelper.ToUtc(zone, 2026, 1, 2, 9, 0, 0),
            ],
            occurrences);
    }

    [Fact]
    public void Schedule_NormalizeTimeZoneIdForStorage_ReturnsNullForUtcAndNull()
    {
        Assert.Null(ReminderCronSchedule.NormalizeTimeZoneIdForStorage(null));
        Assert.Null(ReminderCronSchedule.NormalizeTimeZoneIdForStorage(TimeZoneInfo.Utc));
    }

    [Fact]
    public void Schedule_NormalizeTimeZoneIdForStorage_RejectsCustomRulesWhichCannotBeRestored()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone(
            "Orleans/Test-Custom-Zone",
            TimeSpan.FromHours(3),
            "Orleans custom zone",
            "Orleans custom zone");

        var exception = Assert.Throws<ArgumentException>(() => ReminderCronSchedule.NormalizeTimeZoneIdForStorage(zone));

        Assert.Contains("cannot be stored", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Schedule_Parse_WithUnknownTimeZone_ThrowsReminderCronParseException()
    {
        var exception = Assert.Throws<ReminderCronParseException>(() => ReminderCronSchedule.Parse("0 9 * * *", "Definitely/Not-A-TimeZone"));

        Assert.Contains("Unknown time zone id", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Schedule_Parse_WithAlternatePlatformTimeZoneId_UsesEquivalentZone()
    {
        var zoneId = AdvancedReminderTimeZoneTestHelper.GetCentralEuropeanAlternateTimeZoneId();
        var expectedZoneId = ReminderCronSchedule.NormalizeTimeZoneIdForStorage(AdvancedReminderTimeZoneTestHelper.GetCentralEuropeanTimeZone());
        var schedule = ReminderCronSchedule.Parse("0 9 * * *", zoneId);

        Assert.Equal(expectedZoneId, schedule.TimeZoneId);
    }

    [Fact]
    public void Schedule_Parse_ReusesCachedScheduleForEquivalentInputs()
    {
        var first = ReminderCronSchedule.Parse(" 0 9 * * * ", " Europe/Berlin ");
        var second = ReminderCronSchedule.Parse("0 9 * * *", "Europe/Berlin");

        Assert.Same(first, second);
    }

    [Fact]
    public void Schedule_Parse_BoundsTheProcessWideCache()
    {
        for (var index = 1; index <= ReminderCronSchedule.MaxCacheEntries + 100; index++)
        {
            _ = ReminderCronSchedule.Parse($"0{new string(' ', index)}9 * * *");
        }

        Assert.InRange(ReminderCronSchedule.CacheCount, 1, ReminderCronSchedule.MaxCacheEntries);
    }
}
