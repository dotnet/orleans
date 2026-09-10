#nullable enable
using Orleans.AdvancedReminders;
using Xunit;

namespace UnitTests.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders")]
public class ReminderCronExpressionTimeZoneTests
{
    [Fact]
    public void GetNextOccurrence_WithTimeZone_UsesLocalScheduleAndReturnsUtc()
    {
        var expression = ReminderCronExpression.Parse("0 9 * * *");
        var fromUtc = new DateTime(2026, 1, 1, 6, 30, 0, DateTimeKind.Utc);
        var zone = AdvancedReminderTimeZoneTestHelper.GetCentralEuropeanTimeZone();

        var next = expression.GetNextOccurrence(fromUtc, zone);

        Assert.Equal(new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void GetOccurrences_WithUsEasternAcrossFallBack_PreservesNineAmLocal()
    {
        var expression = ReminderCronExpression.Parse("0 9 * * *");
        var zone = AdvancedReminderTimeZoneTestHelper.GetUsEasternTimeZone();
        var fromUtc = new DateTime(2025, 10, 31, 0, 0, 0, DateTimeKind.Utc);
        var toUtc = new DateTime(2025, 11, 4, 0, 0, 0, DateTimeKind.Utc);

        var occurrences = expression.GetOccurrences(fromUtc, toUtc, zone).ToArray();

        Assert.Equal(
            [
                new DateTime(2025, 10, 31, 13, 0, 0, DateTimeKind.Utc),
                new DateTime(2025, 11, 1, 13, 0, 0, DateTimeKind.Utc),
                new DateTime(2025, 11, 2, 14, 0, 0, DateTimeKind.Utc),
                new DateTime(2025, 11, 3, 14, 0, 0, DateTimeKind.Utc),
            ],
            occurrences);
    }

    [Fact]
    public void GetNextOccurrence_WithUsEastern_WhenLocalTimeIsInvalid_MovesToNextValidInstant()
    {
        var expression = ReminderCronExpression.Parse("30 2 * * *");
        var zone = AdvancedReminderTimeZoneTestHelper.GetUsEasternTimeZone();
        var fromUtc = new DateTime(2025, 3, 8, 13, 0, 0, DateTimeKind.Utc);

        var next = expression.GetNextOccurrence(fromUtc, zone);

        Assert.Equal(new DateTime(2025, 3, 9, 7, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void GetNextOccurrence_WithLeapDaySchedule_SkipsToNextLeapYear()
    {
        var expression = ReminderCronExpression.Parse("0 9 29 2 *");
        var fromUtc = new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        var next = expression.GetNextOccurrence(fromUtc);

        Assert.Equal(new DateTime(2028, 2, 29, 9, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void GetNextOccurrence_WithTimeZone_ThrowsOnNullZone()
    {
        var expression = ReminderCronExpression.Parse("0 9 * * *");
        var fromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.Throws<ArgumentNullException>(() => expression.GetNextOccurrence(fromUtc, zone: null!));
    }

    [Fact]
    public void GetNextOccurrence_WithNegativeUtcOffsetAtMaximumDate_ReturnsNull()
    {
        var expression = ReminderCronExpression.Parse("0 20 * * *");
        var zone = AdvancedReminderTimeZoneTestHelper.GetUsEasternTimeZone();

        var result = expression.GetNextOccurrence(
            DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc),
            zone);

        Assert.Null(result);
    }

    [Fact]
    public void GetOccurrences_WithPositiveOffsetRollback_FiresDailyScheduleOnce()
    {
        var daylightStart = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
            new DateTime(1, 1, 1, 2, 0, 0),
            month: 3,
            week: 2,
            dayOfWeek: DayOfWeek.Sunday);
        var daylightEnd = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
            new DateTime(1, 1, 1, 2, 0, 0),
            month: 11,
            week: 1,
            dayOfWeek: DayOfWeek.Sunday);
        var adjustment = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2025, 1, 1),
            new DateTime(2025, 12, 31),
            TimeSpan.FromHours(1),
            daylightStart,
            daylightEnd);
        var zone = TimeZoneInfo.CreateCustomTimeZone(
            "UTC+13 with DST",
            TimeSpan.FromHours(13),
            "UTC+13 with DST",
            "UTC+13",
            "UTC+14",
            [adjustment]);
        var expression = ReminderCronExpression.Parse("30 1 * * *");
        var from = new DateTime(2025, 11, 1, 10, 0, 0, DateTimeKind.Utc);

        var result = expression.GetOccurrences(from, from.AddHours(6), zone).ToArray();

        Assert.Equal([new DateTime(2025, 11, 1, 11, 30, 0, DateTimeKind.Utc)], result);
    }
}
