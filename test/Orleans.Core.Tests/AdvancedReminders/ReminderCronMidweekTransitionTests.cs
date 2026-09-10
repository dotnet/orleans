using System.Globalization;
using Orleans.AdvancedReminders;
using Xunit;

namespace UnitTests.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders")]
public class ReminderCronMidweekTransitionTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var transitionDay in new[] { 19, 20 }) // Wednesday/Thursday, March 2025.
            foreach (var deltaMinutes in new[] { -120, -60, -30, 30, 60, 120 })
                foreach (var transitionHour in new[] { 0, 2, 23 })
                    foreach (var interval in new[] { false, true })
                    {
                        yield return [transitionDay, deltaMinutes, transitionHour, interval];
                    }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TuesdayThroughFriday_WithMidweekClockChanges_HasExactDeliveryInstants(
        int transitionDay, int deltaMinutes, int transitionHour, bool interval)
    {
        var zone = CreateZone(transitionDay, deltaMinutes, transitionHour);
        var from = new DateTime(2025, 3, 17, 18, 0, 0, DateTimeKind.Utc);
        var until = new DateTime(2025, 3, 23, 6, 0, 0, DateTimeKind.Utc);
        var minute = interval ? "*/15" : "15";
        var hour = transitionHour.ToString(CultureInfo.InvariantCulture);
        var builder = ReminderCronBuilder.FromExpression($"{minute} {hour} * * TUE-FRI", zone);
        var expected = ScanUtcTimeline(zone, from, until, transitionHour, interval);
        var actual = builder.GetOccurrences(from, until).ToArray();

        Assert.NotEmpty(expected);
        Assert.Equal(expected, actual);
        Assert.Equal(actual.Length, actual.Distinct().Count());

        // Resume after every delivery, or from within the gap/repeated clock window.
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index], builder.GetNextOccurrence(expected[index], inclusive: true));
            Assert.Equal(expected[index], builder.GetNextOccurrence(expected[index].AddTicks(-1)));
            if (index + 1 < expected.Length)
            {
                Assert.Equal(expected[index + 1], builder.GetNextOccurrence(expected[index]));
                Assert.Equal(expected[index + 1], builder.GetNextOccurrence(expected[index].AddTicks(1)));
            }
        }

        for (var cursor = from; cursor < expected[^1]; cursor = cursor.AddMinutes(17))
        {
            var next = expected.First(value => value > cursor);
            Assert.Equal(next, builder.GetNextOccurrence(cursor));
            var offsetCursor = new DateTimeOffset(cursor).ToOffset(TimeSpan.FromMinutes(-570));
            Assert.Equal(new DateTimeOffset(next), builder.GetNextOccurrence(offsetCursor));
        }

        // Adjacent half-open pages through the transition must equal one complete query.
        var cut = new DateTime(2025, 3, transitionDay, 0, 0, 0, DateTimeKind.Utc);
        var paged = builder.GetOccurrences(from, cut).Concat(builder.GetOccurrences(cut, until));
        Assert.Equal(expected, paged);
    }

    [Fact]
    public void WednesdayGapInsideTuesdayFridayRange_FiresAtFirstValidInstant()
    {
        var builder = ReminderCronBuilder.FromExpression("30 2 * * TUE-FRI", CreateZone(19, 60, 2));
        var from = new DateTime(2025, 3, 18, 0, 0, 0, DateTimeKind.Utc);
        DateTime[] expected =
        [
            new(2025, 3, 18, 0, 30, 0, DateTimeKind.Utc), // Tuesday 02:30 +02:00.
            new(2025, 3, 19, 0, 0, 0, DateTimeKind.Utc), // Wednesday 02:30 is skipped; fires at 03:00 +03:00.
            new(2025, 3, 20, 0, 30, 0, DateTimeKind.Utc), // Thursday after the rollback, 02:30 +02:00.
            new(2025, 3, 21, 0, 30, 0, DateTimeKind.Utc), // Friday 02:30 +02:00.
        ];
        Assert.Equal(expected, builder.GetOccurrences(from, from.AddDays(4)));
        Assert.Equal(new DateTime(2025, 3, 25, 0, 30, 0, DateTimeKind.Utc), builder.GetNextOccurrence(expected[^1]));
    }

    private static TimeZoneInfo CreateZone(int transitionDay, int deltaMinutes, int transitionHour)
    {
        var time = new DateTime(1, 1, 1, transitionHour, 0, 0);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2025, 1, 1), new DateTime(2025, 12, 31), TimeSpan.FromMinutes(deltaMinutes),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(time, 3, transitionDay),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(time, 3, transitionDay + 1));
        return TimeZoneInfo.CreateCustomTimeZone($"Midweek/{transitionDay}/{deltaMinutes}/{transitionHour}", TimeSpan.FromHours(2),
            "Midweek", "Standard", "Adjusted", [rule]);
    }

    private static DateTime[] ScanUtcTimeline(TimeZoneInfo zone, DateTime from, DateTime until, int hour, bool interval)
    {
        var matches = new SortedSet<DateTime>();
        var deliveredLocalTimes = new HashSet<DateTime>();
        var previous = TimeZoneInfo.ConvertTimeFromUtc(from.AddDays(-1).AddMinutes(-1), zone);
        bool Matches(DateTime local) => local.DayOfWeek is >= DayOfWeek.Tuesday and <= DayOfWeek.Friday
            && local.Hour == hour && (interval ? local.Minute % 15 == 0 : local.Minute == 15);
        void Add(DateTime local, DateTime utc)
        {
            if (Matches(local) && (interval || deliveredLocalTimes.Add(local)) && utc >= from && utc < until)
            {
                matches.Add(utc);
            }
        }

        for (var utc = from.AddDays(-1); utc < until; utc = utc.AddMinutes(1))
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(utc, zone);
            Add(local, utc);
            // Missing local minutes are eligible at the first real instant after a forward jump.
            for (var skipped = previous.AddMinutes(1); skipped < local; skipped = skipped.AddMinutes(1))
            {
                Add(skipped, utc);
            }

            previous = local;
        }

        return matches.ToArray();
    }
}
