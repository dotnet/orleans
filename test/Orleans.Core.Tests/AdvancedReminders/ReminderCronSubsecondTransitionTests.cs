using Orleans.AdvancedReminders;
using Xunit;

namespace UnitTests.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders")]
public class ReminderCronSubsecondTransitionTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(500)]
    [InlineData(999)]
    public void GapEndingBetweenSeconds_ReturnsFirstWholeSecondWithoutRefiring(int milliseconds)
    {
        var start = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 23, 59, 59, milliseconds), 9, 6);
        var end = TimeZoneInfo.TransitionTime.CreateFixedDateRule(DateTime.MinValue, 11, 1);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2025, 1, 1), new DateTime(2025, 12, 31), TimeSpan.FromHours(1), start, end);
        var zone = TimeZoneInfo.CreateCustomTimeZone("Subsecond", TimeSpan.FromHours(-4), "Subsecond", "Standard", "Adjusted", [rule]);
        var builder = ReminderCronBuilder.FromExpression("30 0 * * *", zone);
        var expected = new DateTime(2025, 9, 7, 4, 0, 0, DateTimeKind.Utc);
        Assert.Equal(expected, builder.GetNextOccurrence(expected.AddHours(-1)));
        Assert.Equal(expected, builder.GetNextOccurrence(expected.AddTicks(-1)));
        Assert.Equal(expected, builder.GetNextOccurrence(expected, inclusive: true));
        Assert.Equal(new DateTime(2025, 9, 8, 3, 30, 0, DateTimeKind.Utc), builder.GetNextOccurrence(expected));
    }

    [Fact]
    public void SkippedDateFixture_ActuallyRemovesTwentyFourLocalHours()
    {
        var zone = ReminderCronTestTimeZones.Resolve("Test/SkippedDate");
        var transition = new DateTime(2011, 12, 30, 10, 0, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2011, 12, 29, 23, 59, 59), TimeZoneInfo.ConvertTimeFromUtc(transition.AddSeconds(-1), zone));
        Assert.Equal(new DateTime(2011, 12, 31, 0, 0, 0), TimeZoneInfo.ConvertTimeFromUtc(transition, zone));
    }
}
