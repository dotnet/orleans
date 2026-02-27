using System;
using System.Linq;
using Orleans;
using Xunit;

namespace NonSilo.Tests.Reminders;

[TestCategory("Reminders")]
public class ReminderCronExpressionTimeZoneTests
{
    [Fact]
    public void GetNextOccurrence_WithTimeZone_UsesLocalScheduleAndReturnsUtc()
    {
        var expression = ReminderCronExpression.Parse("0 9 * * *");
        var fromUtc = new DateTime(2026, 1, 1, 6, 30, 0, DateTimeKind.Utc);
        var zone = TimeZoneTestHelper.GetCentralEuropeanTimeZone();

        var next = expression.GetNextOccurrence(fromUtc, zone);

        Assert.Equal(new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void GetOccurrences_WithTimeZone_UsesLocalScheduleAndReturnsUtc()
    {
        var expression = ReminderCronExpression.Parse("0 9 * * *");
        var fromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var toUtc = new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc);
        var zone = TimeZoneTestHelper.GetCentralEuropeanTimeZone();

        var occurrences = expression.GetOccurrences(fromUtc, toUtc, zone).ToArray();

        Assert.Equal(
            [
                new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 1, 2, 8, 0, 0, DateTimeKind.Utc),
            ],
            occurrences);
    }

    [Fact]
    public void GetNextOccurrence_WithDubaiTimeZone_HasStableUtcOffsetAcrossSeasons()
    {
        var expression = ReminderCronExpression.Parse("0 9 * * *");
        var zone = TimeZoneTestHelper.GetDubaiTimeZone();
        var winterFromUtc = new DateTime(2026, 1, 15, 4, 30, 0, DateTimeKind.Utc);
        var summerFromUtc = new DateTime(2026, 7, 15, 4, 30, 0, DateTimeKind.Utc);

        var winterNext = expression.GetNextOccurrence(winterFromUtc, zone);
        var summerNext = expression.GetNextOccurrence(summerFromUtc, zone);

        Assert.Equal(new DateTime(2026, 1, 15, 5, 0, 0, DateTimeKind.Utc), winterNext);
        Assert.Equal(new DateTime(2026, 7, 15, 5, 0, 0, DateTimeKind.Utc), summerNext);
    }

    [Fact]
    public void GetNextOccurrence_WithIndiaAndNepalTimeZone_UsesHalfAndQuarterHourOffsets()
    {
        var expression = ReminderCronExpression.Parse("0 9 * * *");
        var indiaZone = TimeZoneTestHelper.GetIndiaTimeZone();
        var nepalZone = TimeZoneTestHelper.GetNepalTimeZone();
        var fromUtc = new DateTime(2026, 1, 15, 3, 0, 0, DateTimeKind.Utc);

        var indiaNext = expression.GetNextOccurrence(fromUtc, indiaZone);
        var nepalNext = expression.GetNextOccurrence(fromUtc, nepalZone);

        Assert.Equal(new DateTime(2026, 1, 15, 3, 30, 0, DateTimeKind.Utc), indiaNext);
        Assert.Equal(new DateTime(2026, 1, 15, 3, 15, 0, DateTimeKind.Utc), nepalNext);
    }

    [Fact]
    public void GetNextOccurrence_WithLordHoweTimeZone_ReflectsThirtyMinuteDstShift()
    {
        var expression = ReminderCronExpression.Parse("0 9 * * *");
        var zone = TimeZoneTestHelper.GetLordHoweTimeZone();
        var summerFromUtc = new DateTime(2026, 1, 14, 21, 45, 0, DateTimeKind.Utc);
        var winterFromUtc = new DateTime(2026, 7, 14, 22, 15, 0, DateTimeKind.Utc);

        var summerNext = expression.GetNextOccurrence(summerFromUtc, zone);
        var winterNext = expression.GetNextOccurrence(winterFromUtc, zone);

        Assert.Equal(new DateTime(2026, 1, 14, 22, 0, 0, DateTimeKind.Utc), summerNext);
        Assert.Equal(new DateTime(2026, 7, 14, 22, 30, 0, DateTimeKind.Utc), winterNext);
    }

    [Fact]
    public void GetNextOccurrence_WithUsAndEuropeTransitionGap_ReflectsOneWeekDstDifference()
    {
        var expression = ReminderCronExpression.Parse("0 9 * * *");
        var europeZone = TimeZoneTestHelper.GetCentralEuropeanTimeZone();
        var usZone = TimeZoneTestHelper.GetUsEasternTimeZone();

        // Between these dates in 2025, US has switched to DST while Europe has not yet switched.
        var weekGapStartUtc = new DateTime(2025, 3, 24, 0, 0, 0, DateTimeKind.Utc);
        var europeWeekGapNext = expression.GetNextOccurrence(weekGapStartUtc, europeZone);
        var usWeekGapNext = expression.GetNextOccurrence(weekGapStartUtc, usZone);

        // After Europe transitions to DST (March 30, 2025), 9:00 CET/CEST shifts by one hour in UTC.
        var postEuropeTransitionUtc = new DateTime(2025, 3, 31, 0, 0, 0, DateTimeKind.Utc);
        var europePostTransitionNext = expression.GetNextOccurrence(postEuropeTransitionUtc, europeZone);
        var usPostTransitionNext = expression.GetNextOccurrence(postEuropeTransitionUtc, usZone);

        Assert.Equal(new DateTime(2025, 3, 24, 8, 0, 0, DateTimeKind.Utc), europeWeekGapNext);
        Assert.Equal(new DateTime(2025, 3, 24, 13, 0, 0, DateTimeKind.Utc), usWeekGapNext);
        Assert.Equal(new DateTime(2025, 3, 31, 7, 0, 0, DateTimeKind.Utc), europePostTransitionNext);
        Assert.Equal(new DateTime(2025, 3, 31, 13, 0, 0, DateTimeKind.Utc), usPostTransitionNext);
    }

    [Fact]
    public void GetNextOccurrence_WhenSwitchingFromUtcToUsEastern_RecomputesForUsLocalSchedule()
    {
        var expression = ReminderCronExpression.Parse("0 9 * * *");
        var zone = TimeZoneTestHelper.GetUsEasternTimeZone();
        var fromUtc = new DateTime(2026, 1, 15, 12, 30, 0, DateTimeKind.Utc);

        var nextWhenScheduledInUtc = expression.GetNextOccurrence(fromUtc);
        var nextAfterTimeZoneUpdate = expression.GetNextOccurrence(fromUtc, zone);

        Assert.Equal(new DateTime(2026, 1, 16, 9, 0, 0, DateTimeKind.Utc), nextWhenScheduledInUtc);
        Assert.Equal(new DateTime(2026, 1, 15, 14, 0, 0, DateTimeKind.Utc), nextAfterTimeZoneUpdate);

        var localNext = TimeZoneInfo.ConvertTimeFromUtc(nextAfterTimeZoneUpdate!.Value, zone);
        Assert.Equal(9, localNext.Hour);
        Assert.Equal(0, localNext.Minute);
    }

    [Fact]
    public void GetNextOccurrence_WhenSwitchingFromUtcToUsEastern_BeforeSpringForward_KeepsNineAmLocalAcrossTransition()
    {
        var expression = ReminderCronExpression.Parse("0 9 * * *");
        var zone = TimeZoneTestHelper.GetUsEasternTimeZone();
        var fromUtc = new DateTime(2026, 3, 7, 12, 30, 0, DateTimeKind.Utc);

        var nextWhenScheduledInUtc = expression.GetNextOccurrence(fromUtc);
        var firstAfterTimeZoneUpdate = expression.GetNextOccurrence(fromUtc, zone);
        var secondAfterTimeZoneUpdate = expression.GetNextOccurrence(firstAfterTimeZoneUpdate!.Value.AddSeconds(1), zone);

        Assert.Equal(new DateTime(2026, 3, 8, 9, 0, 0, DateTimeKind.Utc), nextWhenScheduledInUtc);
        Assert.Equal(new DateTime(2026, 3, 7, 14, 0, 0, DateTimeKind.Utc), firstAfterTimeZoneUpdate);
        Assert.Equal(new DateTime(2026, 3, 8, 13, 0, 0, DateTimeKind.Utc), secondAfterTimeZoneUpdate);

        var firstLocal = TimeZoneInfo.ConvertTimeFromUtc(firstAfterTimeZoneUpdate.Value, zone);
        var secondLocal = TimeZoneInfo.ConvertTimeFromUtc(secondAfterTimeZoneUpdate!.Value, zone);
        Assert.Equal(9, firstLocal.Hour);
        Assert.Equal(9, secondLocal.Hour);
    }

    [Fact]
    public void GetNextOccurrence_WhenSwitchingFromUtcToUsEastern_BeforeFallBack_KeepsNineAmLocalAcrossTransition()
    {
        var expression = ReminderCronExpression.Parse("0 9 * * *");
        var zone = TimeZoneTestHelper.GetUsEasternTimeZone();
        var fromUtc = new DateTime(2026, 10, 31, 12, 30, 0, DateTimeKind.Utc);

        var nextWhenScheduledInUtc = expression.GetNextOccurrence(fromUtc);
        var firstAfterTimeZoneUpdate = expression.GetNextOccurrence(fromUtc, zone);
        var secondAfterTimeZoneUpdate = expression.GetNextOccurrence(firstAfterTimeZoneUpdate!.Value.AddSeconds(1), zone);

        Assert.Equal(new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc), nextWhenScheduledInUtc);
        Assert.Equal(new DateTime(2026, 10, 31, 13, 0, 0, DateTimeKind.Utc), firstAfterTimeZoneUpdate);
        Assert.Equal(new DateTime(2026, 11, 1, 14, 0, 0, DateTimeKind.Utc), secondAfterTimeZoneUpdate);

        var firstLocal = TimeZoneInfo.ConvertTimeFromUtc(firstAfterTimeZoneUpdate.Value, zone);
        var secondLocal = TimeZoneInfo.ConvertTimeFromUtc(secondAfterTimeZoneUpdate!.Value, zone);
        Assert.Equal(9, firstLocal.Hour);
        Assert.Equal(9, secondLocal.Hour);
    }

    [Fact]
    public void GetOccurrences_WithUsEasternAcrossSpringForward_PreservesNineAmLocal()
    {
        var expression = ReminderCronExpression.Parse("0 9 * * *");
        var zone = TimeZoneTestHelper.GetUsEasternTimeZone();
        var fromUtc = new DateTime(2025, 3, 7, 0, 0, 0, DateTimeKind.Utc);
        var toUtc = new DateTime(2025, 3, 12, 0, 0, 0, DateTimeKind.Utc);

        var occurrences = expression.GetOccurrences(fromUtc, toUtc, zone).ToArray();

        Assert.Equal(
            [
                new DateTime(2025, 3, 7, 14, 0, 0, DateTimeKind.Utc),
                new DateTime(2025, 3, 8, 14, 0, 0, DateTimeKind.Utc),
                new DateTime(2025, 3, 9, 13, 0, 0, DateTimeKind.Utc),
                new DateTime(2025, 3, 10, 13, 0, 0, DateTimeKind.Utc),
                new DateTime(2025, 3, 11, 13, 0, 0, DateTimeKind.Utc),
            ],
            occurrences);
    }

    [Fact]
    public void GetOccurrences_WithUsEasternAcrossFallBack_PreservesNineAmLocal()
    {
        var expression = ReminderCronExpression.Parse("0 9 * * *");
        var zone = TimeZoneTestHelper.GetUsEasternTimeZone();
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
    public void GetNextOccurrence_WithTimeZone_ThrowsOnNullZone()
    {
        var expression = ReminderCronExpression.Parse("0 9 * * *");
        var fromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.Throws<ArgumentNullException>(() => expression.GetNextOccurrence(fromUtc, zone: null!));
    }

    [Fact]
    public void GetNextOccurrence_WithUsEastern_WhenLocalTimeIsInvalid_MovesToNextValidInstant()
    {
        var expression = ReminderCronExpression.Parse("30 2 * * *");
        var zone = TimeZoneTestHelper.GetUsEasternTimeZone();
        var fromUtc = new DateTime(2025, 3, 8, 13, 0, 0, DateTimeKind.Utc);

        var next = expression.GetNextOccurrence(fromUtc, zone);

        // 2025-03-09 02:30 local does not exist in US Eastern due to spring-forward transition.
        // Scheduler should move to the DST start instant (03:00 local).
        Assert.Equal(new DateTime(2025, 3, 9, 7, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void GetNextOccurrence_WithUsEastern_WhenLocalTimeIsAmbiguous_UsesFirstOccurrenceOnly()
    {
        var expression = ReminderCronExpression.Parse("30 1 * * *");
        var zone = TimeZoneTestHelper.GetUsEasternTimeZone();
        var fromUtc = new DateTime(2025, 11, 2, 0, 0, 0, DateTimeKind.Utc);

        var first = expression.GetNextOccurrence(fromUtc, zone);
        var second = expression.GetNextOccurrence(first!.Value.AddSeconds(1), zone);

        // 2025-11-02 01:30 local occurs twice. Scheduler should pick the earlier (daylight) instant.
        Assert.Equal(new DateTime(2025, 11, 2, 5, 30, 0, DateTimeKind.Utc), first);
        Assert.Equal(new DateTime(2025, 11, 3, 6, 30, 0, DateTimeKind.Utc), second);
    }
}
