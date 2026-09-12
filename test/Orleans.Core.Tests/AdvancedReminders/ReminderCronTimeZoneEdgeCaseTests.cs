#nullable enable
using Orleans.AdvancedReminders;
using Orleans.AdvancedReminders.Cron.Internal;
using Xunit;

namespace UnitTests.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders")]
public class ReminderCronTimeZoneEdgeCaseTests
{
    [Fact]
    public void Builder_WithNepalTimeZone_PreservesQuarterHourOffset()
    {
        var zone = AdvancedReminderTimeZoneTestHelper.GetNepalTimeZone();
        var builder = ReminderCronBuilder.DailyAt(9, 0).InTimeZone(zone);
        var fromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var next = builder.GetNextOccurrence(fromUtc);

        Assert.Equal(AdvancedReminderTimeZoneTestHelper.ToUtc(zone, 2026, 1, 1, 9, 0, 0), next);
    }

    [Fact]
    public void Builder_WithKyivTimeZoneOverload_AcrossEuropeanSpringForward_PreservesNineAmLocal()
    {
        var zone = AdvancedReminderTimeZoneTestHelper.GetKyivTimeZone();
        var builder = ReminderCronBuilder.DailyAt(9, 0, zone);
        var fromUtc = new DateTime(2025, 3, 28, 0, 0, 0, DateTimeKind.Utc);
        var toUtc = new DateTime(2025, 4, 2, 0, 0, 0, DateTimeKind.Utc);

        var occurrences = builder.GetOccurrences(fromUtc, toUtc).ToArray();

        Assert.Equal(
            [
                AdvancedReminderTimeZoneTestHelper.ToUtc(zone, 2025, 3, 28, 9, 0, 0),
                AdvancedReminderTimeZoneTestHelper.ToUtc(zone, 2025, 3, 29, 9, 0, 0),
                AdvancedReminderTimeZoneTestHelper.ToUtc(zone, 2025, 3, 30, 9, 0, 0),
                AdvancedReminderTimeZoneTestHelper.ToUtc(zone, 2025, 3, 31, 9, 0, 0),
                AdvancedReminderTimeZoneTestHelper.ToUtc(zone, 2025, 4, 1, 9, 0, 0),
            ],
            occurrences);
    }

    [Fact]
    public void Builder_WithLordHoweAcrossDstTransition_PreservesNineAmLocal()
    {
        var zone = AdvancedReminderTimeZoneTestHelper.GetLordHoweTimeZone();
        var builder = ReminderCronBuilder.DailyAt(9, 0).InTimeZone(zone);
        var fromUtc = new DateTime(2025, 4, 4, 0, 0, 0, DateTimeKind.Utc);
        var toUtc = new DateTime(2025, 4, 8, 0, 0, 0, DateTimeKind.Utc);

        var occurrences = builder.GetOccurrences(fromUtc, toUtc).ToArray();

        Assert.Equal(
            [
                AdvancedReminderTimeZoneTestHelper.ToUtc(zone, 2025, 4, 5, 9, 0, 0),
                AdvancedReminderTimeZoneTestHelper.ToUtc(zone, 2025, 4, 6, 9, 0, 0),
                AdvancedReminderTimeZoneTestHelper.ToUtc(zone, 2025, 4, 7, 9, 0, 0),
                AdvancedReminderTimeZoneTestHelper.ToUtc(zone, 2025, 4, 8, 9, 0, 0),
            ],
            occurrences);
    }

    [Fact]
    public void Builder_WithIndiaTimeZone_AcrossNewYear_PreservesLocalMidnightSchedule()
    {
        var zone = AdvancedReminderTimeZoneTestHelper.GetIndiaTimeZone();
        var builder = ReminderCronBuilder.DailyAt(0, 15).InTimeZone(zone);
        var fromUtc = new DateTime(2025, 12, 31, 18, 0, 0, DateTimeKind.Utc);

        var next = builder.GetNextOccurrence(fromUtc);

        Assert.Equal(AdvancedReminderTimeZoneTestHelper.ToUtc(zone, 2026, 1, 1, 0, 15, 0), next);
    }

    [Fact]
    public void Builder_WithDubaiTimeZoneOverload_DoesNotShiftAcrossDstWindows()
    {
        var zone = AdvancedReminderTimeZoneTestHelper.GetDubaiTimeZone();
        var builder = ReminderCronBuilder.DailyAt(9, 0, zone);
        var fromUtc = new DateTime(2025, 3, 7, 0, 0, 0, DateTimeKind.Utc);
        var toUtc = new DateTime(2025, 3, 12, 0, 0, 0, DateTimeKind.Utc);

        var occurrences = builder.GetOccurrences(fromUtc, toUtc).ToArray();

        Assert.Equal(
            [
                AdvancedReminderTimeZoneTestHelper.ToUtc(zone, 2025, 3, 7, 9, 0, 0),
                AdvancedReminderTimeZoneTestHelper.ToUtc(zone, 2025, 3, 8, 9, 0, 0),
                AdvancedReminderTimeZoneTestHelper.ToUtc(zone, 2025, 3, 9, 9, 0, 0),
                AdvancedReminderTimeZoneTestHelper.ToUtc(zone, 2025, 3, 10, 9, 0, 0),
                AdvancedReminderTimeZoneTestHelper.ToUtc(zone, 2025, 3, 11, 9, 0, 0),
            ],
            occurrences);
    }

    [Fact]
    public void Builder_WithKyivAndNewYork_WhenUsAlreadyOnDst_ReturnExpectedUtcOffsets()
    {
        var kyiv = AdvancedReminderTimeZoneTestHelper.GetKyivTimeZone();
        var newYork = AdvancedReminderTimeZoneTestHelper.GetUsEasternTimeZone();
        var kyivBuilder = ReminderCronBuilder.DailyAt(9, 0, kyiv);
        var newYorkBuilder = ReminderCronBuilder.DailyAt(9, 0, newYork);
        var fromUtc = new DateTime(2025, 3, 10, 0, 0, 0, DateTimeKind.Utc);

        var kyivNext = kyivBuilder.GetNextOccurrence(fromUtc);
        var newYorkNext = newYorkBuilder.GetNextOccurrence(fromUtc);

        Assert.Equal(new DateTime(2025, 3, 10, 7, 0, 0, DateTimeKind.Utc), kyivNext);
        Assert.Equal(new DateTime(2025, 3, 10, 13, 0, 0, DateTimeKind.Utc), newYorkNext);
        Assert.Equal(AdvancedReminderTimeZoneTestHelper.ToUtc(kyiv, 2025, 3, 10, 9, 0, 0), kyivNext);
        Assert.Equal(AdvancedReminderTimeZoneTestHelper.ToUtc(newYork, 2025, 3, 10, 9, 0, 0), newYorkNext);
    }

    [Fact]
    public void Builder_WithParisAndNewYork_WhenEuropeAlreadyStandardButUsStillOnDst_ReturnExpectedUtcOffsets()
    {
        var paris = AdvancedReminderTimeZoneTestHelper.GetParisTimeZone();
        var newYork = AdvancedReminderTimeZoneTestHelper.GetUsEasternTimeZone();
        var parisBuilder = ReminderCronBuilder.DailyAt(9, 0, paris);
        var newYorkBuilder = ReminderCronBuilder.DailyAt(9, 0, newYork);
        var fromUtc = new DateTime(2025, 10, 27, 0, 0, 0, DateTimeKind.Utc);

        var parisNext = parisBuilder.GetNextOccurrence(fromUtc);
        var newYorkNext = newYorkBuilder.GetNextOccurrence(fromUtc);

        Assert.Equal(new DateTime(2025, 10, 27, 8, 0, 0, DateTimeKind.Utc), parisNext);
        Assert.Equal(new DateTime(2025, 10, 27, 13, 0, 0, DateTimeKind.Utc), newYorkNext);
        Assert.Equal(AdvancedReminderTimeZoneTestHelper.ToUtc(paris, 2025, 10, 27, 9, 0, 0), parisNext);
        Assert.Equal(AdvancedReminderTimeZoneTestHelper.ToUtc(newYork, 2025, 10, 27, 9, 0, 0), newYorkNext);
    }

    [Fact]
    public void Builder_WithDubaiAndNewYork_WhenUsAlreadyOnDst_DubaiRemainsFixed()
    {
        var dubai = AdvancedReminderTimeZoneTestHelper.GetDubaiTimeZone();
        var newYork = AdvancedReminderTimeZoneTestHelper.GetUsEasternTimeZone();
        var dubaiBuilder = ReminderCronBuilder.DailyAt(9, 0, dubai);
        var newYorkBuilder = ReminderCronBuilder.DailyAt(9, 0, newYork);
        var fromUtc = new DateTime(2025, 3, 10, 0, 0, 0, DateTimeKind.Utc);

        var dubaiNext = dubaiBuilder.GetNextOccurrence(fromUtc);
        var newYorkNext = newYorkBuilder.GetNextOccurrence(fromUtc);

        Assert.Equal(new DateTime(2025, 3, 10, 5, 0, 0, DateTimeKind.Utc), dubaiNext);
        Assert.Equal(new DateTime(2025, 3, 10, 13, 0, 0, DateTimeKind.Utc), newYorkNext);
        Assert.Equal(AdvancedReminderTimeZoneTestHelper.ToUtc(dubai, 2025, 3, 10, 9, 0, 0), dubaiNext);
        Assert.Equal(AdvancedReminderTimeZoneTestHelper.ToUtc(newYork, 2025, 3, 10, 9, 0, 0), newYorkNext);
    }

    [Fact]
    public void Builder_WithIndiaAndParis_WhenEuropeAlreadyStandard_IndiaRemainsFixed()
    {
        var india = AdvancedReminderTimeZoneTestHelper.GetIndiaTimeZone();
        var paris = AdvancedReminderTimeZoneTestHelper.GetParisTimeZone();
        var indiaBuilder = ReminderCronBuilder.DailyAt(9, 0, india);
        var parisBuilder = ReminderCronBuilder.DailyAt(9, 0, paris);
        var fromUtc = new DateTime(2025, 10, 27, 0, 0, 0, DateTimeKind.Utc);

        var indiaNext = indiaBuilder.GetNextOccurrence(fromUtc);
        var parisNext = parisBuilder.GetNextOccurrence(fromUtc);

        Assert.Equal(new DateTime(2025, 10, 27, 3, 30, 0, DateTimeKind.Utc), indiaNext);
        Assert.Equal(new DateTime(2025, 10, 27, 8, 0, 0, DateTimeKind.Utc), parisNext);
        Assert.Equal(AdvancedReminderTimeZoneTestHelper.ToUtc(india, 2025, 10, 27, 9, 0, 0), indiaNext);
        Assert.Equal(AdvancedReminderTimeZoneTestHelper.ToUtc(paris, 2025, 10, 27, 9, 0, 0), parisNext);
    }

    [Fact]
    public void Schedule_WithNepalTimeZone_AcrossNewYear_PreservesQuarterHourOffset()
    {
        var zone = AdvancedReminderTimeZoneTestHelper.GetNepalTimeZone();
        var schedule = ReminderCronSchedule.Parse("0 9 * * *", ReminderCronSchedule.NormalizeTimeZoneIdForStorage(zone));
        var fromUtc = new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc);
        var toUtc = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);

        var occurrences = schedule.GetOccurrences(fromUtc, toUtc).ToArray();

        Assert.Equal(
            [
                AdvancedReminderTimeZoneTestHelper.ToUtc(zone, 2025, 12, 31, 9, 0, 0),
                AdvancedReminderTimeZoneTestHelper.ToUtc(zone, 2026, 1, 1, 9, 0, 0),
            ],
            occurrences);
    }

    [Fact]
    public void Occurrences_AcrossClockRollback_RepeatIntervalsInUtcOrder()
    {
        var zone = AdvancedReminderTimeZoneTestHelper.GetUsEasternTimeZone();
        var expression = ReminderCronExpression.Parse("*/30 * * * *");
        var from = new DateTime(2025, 11, 2, 5, 0, 0, DateTimeKind.Utc);
        var actual = expression.GetOccurrences(from, from.AddHours(3), zone).ToArray();

        Assert.Equal(Enumerable.Range(0, 6).Select(index => from.AddMinutes(index * 30)), actual);
    }
}
