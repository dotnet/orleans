using System.Globalization;
using Orleans.AdvancedReminders;
using Xunit;

namespace UnitTests.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders")]
public class ReminderCronDailyTransitionTests
{
    public static IEnumerable<object[]> TransitionCases()
    {
        (string Zone, int Year, int Month, int Day)[] transitions =
        [
            ("America/New_York", 2025, 3, 9), ("America/New_York", 2025, 11, 2),
            ("Europe/Kyiv", 2025, 3, 30), ("Europe/Kyiv", 2025, 10, 26),
            ("Australia/Lord_Howe", 2025, 4, 5), ("Australia/Lord_Howe", 2025, 10, 4),
            ("Pacific/Chatham", 2025, 4, 5), ("Pacific/Chatham", 2025, 9, 27),
            ("Africa/Casablanca", 2025, 2, 23), ("Africa/Casablanca", 2025, 4, 6),
            ("Test/TwoHourSeason", 2025, 3, 30), ("Test/TwoHourSeason", 2025, 10, 26),
            ("America/Santiago", 2025, 4, 5), ("America/Santiago", 2025, 9, 6),
            ("Asia/Beirut", 2025, 3, 30), ("Asia/Beirut", 2025, 10, 26),
            ("Pacific/Apia", 2011, 12, 30),
        ];
        foreach (var transition in transitions)
            foreach (var hour in new[] { 0, 1, 2, 3, 12, 23 })
            {
                yield return [transition.Zone, transition.Year, transition.Month, transition.Day, hour];
            }
    }

    [Theory]
    [MemberData(nameof(TransitionCases))]
    public void FixedDailySchedule_MatchesIndependentUtcTimelineAcrossTransitions(string zoneId, int year, int month, int day, int hour)
    {
        var zone = ReminderCronTestTimeZones.Resolve(zoneId);
        var from = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);
        var until = from.AddDays(3);
        var target = TimeSpan.FromHours(hour) + TimeSpan.FromMinutes(30);
        var byLocalDate = new Dictionary<DateTime, DateTime>();
        var scanStart = from.AddDays(-2);
        var previous = TimeZoneInfo.ConvertTimeFromUtc(scanStart.AddMinutes(-1), zone);
        for (var utc = scanStart; utc < until.AddDays(2); utc = utc.AddMinutes(1))
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(utc, zone);
            if (local.TimeOfDay == target) byLocalDate.TryAdd(local.Date, utc);
            if (local > previous.AddMinutes(1))
            {
                // Any scheduled wall time skipped by this forward jump catches up now.
                for (var skippedDate = previous.Date; skippedDate <= local.Date; skippedDate = skippedDate.AddDays(1))
                {
                    var scheduled = skippedDate + target;
                    if (scheduled > previous && scheduled < local) byLocalDate.TryAdd(skippedDate, utc);
                }
            }

            previous = local;
        }

        var expected = byLocalDate.Values.Where(utc => utc >= from && utc < until).Distinct().Order().ToArray();
        var builder = ReminderCronBuilder.FromExpression($"30 {hour.ToString(CultureInfo.InvariantCulture)} * * *", zone);
        var actual = builder.GetOccurrences(from, until).ToArray();
        Assert.Equal(expected, actual);

        // Restarting a query after a delivery must not fire the second copy of a rolled-back clock time.
        for (var index = 0; index + 1 < expected.Length; index++)
        {
            Assert.Equal(expected[index + 1], builder.GetNextOccurrence(expected[index]));
        }
    }
}
