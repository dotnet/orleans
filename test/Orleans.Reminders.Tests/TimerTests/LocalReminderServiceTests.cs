using Orleans.Runtime;
using Orleans.Runtime.ReminderService;
using Xunit;

namespace UnitTests.TimerTests;

public class LocalReminderServiceTests
{
    [Fact, TestCategory("BVT")]
    public void CalculateInitialDueTime_ReturnsMinimumDueTime_WhenNextTickIsDueNow()
    {
        var period = TimeSpan.FromSeconds(12);
        var startAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var entry = CreateReminderEntry(startAt, period);
        var now = startAt + period;

        var dueTime = LocalReminderService.CalculateInitialDueTime(entry, now);

        Assert.Equal(TimeSpan.FromMilliseconds(1), dueTime);
    }

    [Fact, TestCategory("BVT")]
    public void CalculateInitialDueTime_ReturnsMinimumDueTime_WhenNextTickIsWithinMinimumDueTime()
    {
        var period = TimeSpan.FromSeconds(12);
        var startAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var entry = CreateReminderEntry(startAt, period);
        var now = startAt + period - TimeSpan.FromTicks(1);

        var dueTime = LocalReminderService.CalculateInitialDueTime(entry, now);

        Assert.Equal(TimeSpan.FromMilliseconds(1), dueTime);
    }

    [Fact, TestCategory("BVT")]
    public void CalculateInitialDueTime_ReturnsRemainingDueTime_WhenNextTickIsAtLeastMinimumDueTime()
    {
        var period = TimeSpan.FromSeconds(12);
        var startAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var entry = CreateReminderEntry(startAt, period);
        var now = startAt + period - TimeSpan.FromMilliseconds(10);

        var dueTime = LocalReminderService.CalculateInitialDueTime(entry, now);

        Assert.Equal(TimeSpan.FromMilliseconds(10), dueTime);
    }

    [Fact, TestCategory("BVT")]
    public void CalculateInitialDueTime_ReturnsRemainingPeriod_WhenNextTickIsInFuture()
    {
        var period = TimeSpan.FromSeconds(12);
        var startAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var entry = CreateReminderEntry(startAt, period);
        var now = startAt + TimeSpan.FromSeconds(3);

        var dueTime = LocalReminderService.CalculateInitialDueTime(entry, now);

        Assert.Equal(TimeSpan.FromSeconds(9), dueTime);
    }

    private static ReminderEntry CreateReminderEntry(DateTime startAt, TimeSpan period)
    {
        return new ReminderEntry
        {
            GrainId = GrainId.Create("test", "grain"),
            ReminderName = "reminder",
            StartAt = startAt,
            Period = period,
            ETag = "etag",
        };
    }
}
