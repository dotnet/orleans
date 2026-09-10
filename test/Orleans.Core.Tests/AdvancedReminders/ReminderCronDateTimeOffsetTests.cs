using Orleans.AdvancedReminders;
using Xunit;

namespace UnitTests.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders")]
public class ReminderCronDateTimeOffsetTests
{
    [Theory]
    [InlineData(-840)]
    [InlineData(-570)]
    [InlineData(-210)]
    [InlineData(0)]
    [InlineData(330)]
    [InlineData(345)]
    [InlineData(765)]
    [InlineData(840)]
    public void EquivalentInstants_ProduceTheSameUtcOccurrence(int offsetMinutes)
    {
        var instant = new DateTimeOffset(2025, 3, 9, 6, 45, 0, TimeSpan.Zero);
        var from = instant.ToOffset(TimeSpan.FromMinutes(offsetMinutes));
        var expected = instant.AddMinutes(15);
        var expression = ReminderCronExpression.Parse("*/15 * * * *");
        var builder = ReminderCronBuilder.DailyAt(2, 30).InTimeZone("America/New_York");

        var direct = expression.GetNextOccurrence(from);
        var zoned = builder.GetNextOccurrence(from);
        Assert.Equal(expected, direct);
        Assert.Equal(expected, zoned);
        Assert.Equal(TimeSpan.Zero, direct!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, zoned!.Value.Offset);
    }

    [Fact]
    public void RepeatedWallClockTimes_WithDifferentOffsets_AreDifferentStartingInstants()
    {
        var first = new DateTimeOffset(2025, 11, 2, 1, 15, 0, TimeSpan.FromHours(-4));
        var second = new DateTimeOffset(2025, 11, 2, 1, 15, 0, TimeSpan.FromHours(-5));
        var daily = ReminderCronBuilder.DailyAt(1, 30).InTimeZone("America/New_York");
        var interval = ReminderCronBuilder.EveryMinutes(15).InTimeZone("America/New_York");

        Assert.Equal(new DateTimeOffset(2025, 11, 2, 5, 30, 0, TimeSpan.Zero), daily.GetNextOccurrence(first));
        Assert.Equal(new DateTimeOffset(2025, 11, 3, 6, 30, 0, TimeSpan.Zero), daily.GetNextOccurrence(second));
        Assert.Equal(new DateTimeOffset(2025, 11, 2, 6, 30, 0, TimeSpan.Zero), interval.GetNextOccurrence(second));
    }

    [Fact]
    public void RangeAcrossClockRollback_UsesBothEndpointOffsets()
    {
        var from = new DateTimeOffset(2025, 11, 2, 0, 30, 0, TimeSpan.FromHours(-4));
        var until = new DateTimeOffset(2025, 11, 2, 2, 30, 0, TimeSpan.FromHours(-5));
        var builder = ReminderCronBuilder.EveryMinutes(30).InTimeZone("America/New_York");
        var expected = Enumerable.Range(0, 6).Select(index => from.ToUniversalTime().AddMinutes(index * 30)).ToArray();
        var actual = builder.GetOccurrences(from, until).ToArray();

        Assert.Equal(expected, actual);
        Assert.All(actual, occurrence => Assert.Equal(TimeSpan.Zero, occurrence.Offset));
    }

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(false, true, 0)]
    [InlineData(true, false, 0)]
    [InlineData(true, true, 1)]
    public void EqualInstantsWithDifferentOffsets_RespectEndpointFlags(bool fromInclusive, bool toInclusive, int expectedCount)
    {
        var from = DateTimeOffset.UnixEpoch.ToOffset(TimeSpan.FromHours(14));
        var until = DateTimeOffset.UnixEpoch.ToOffset(TimeSpan.FromHours(-14));
        var expression = ReminderCronExpression.Parse("* * * * * *");
        var builder = ReminderCronBuilder.FromExpression("* * * * * *");

        Assert.Equal(expectedCount, expression.GetOccurrences(from, until, fromInclusive, toInclusive).Count());
        Assert.Equal(expectedCount, builder.GetOccurrences(from, until, fromInclusive, toInclusive).Count());
        Assert.Equal(DateTimeOffset.UnixEpoch, builder.GetNextOccurrence(from, inclusive: true));
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(1), builder.GetNextOccurrence(from));
    }

    [Fact]
    public void RangeValidation_ComparesInstantsInsteadOfLocalClockValues()
    {
        var from = DateTimeOffset.UnixEpoch.ToOffset(TimeSpan.FromHours(-14));
        var until = DateTimeOffset.UnixEpoch.AddSeconds(-1).ToOffset(TimeSpan.FromHours(14));
        Assert.True(from.DateTime < until.DateTime);
        var expression = ReminderCronExpression.Parse("* * * * * *");
        var builder = ReminderCronBuilder.FromExpression("* * * * * *");
        Assert.Throws<ArgumentException>(() => expression.GetOccurrences(from, until).ToArray());
        Assert.Throws<ArgumentException>(() => builder.GetOccurrences(from, until).ToArray());
        Assert.Single(expression.GetOccurrences(until, from));
        Assert.Single(builder.GetOccurrences(until, from));
    }

    [Fact]
    public void RepresentableDateLimits_WithNonzeroOffsets_DoNotOverflow()
    {
        var expression = ReminderCronExpression.Parse("* * * * * *");
        var builder = ReminderCronBuilder.FromExpression("* * * * * *");
        var minimum = DateTimeOffset.MinValue.ToOffset(TimeSpan.FromHours(14));
        var maximum = DateTimeOffset.MaxValue.ToOffset(TimeSpan.FromHours(-14));
        Assert.Equal(DateTimeOffset.MinValue, expression.GetNextOccurrence(minimum, inclusive: true));
        Assert.Equal(DateTimeOffset.MinValue.AddSeconds(1), builder.GetNextOccurrence(minimum));
        Assert.Null(expression.GetNextOccurrence(maximum, inclusive: true));
        Assert.Null(builder.GetNextOccurrence(maximum));
        Assert.Empty(builder.GetOccurrences(maximum, maximum, true, true));
    }

    [Theory]
    [MemberData(nameof(ReminderCronAllocationTestCases.Occurrences), MemberType = typeof(ReminderCronAllocationTestCases))]
    public void RepeatedOffsetQueries_DoNotAllocateAfterWarmup(string expression, string zoneId, DateTime fromUtc, DateTime? expectedUtc)
    {
        var builder = ReminderCronBuilder.FromExpression(expression, ReminderCronTestTimeZones.Resolve(zoneId));
        var from = new DateTimeOffset(fromUtc).ToOffset(TimeSpan.FromMinutes(345));
        DateTimeOffset? expected = expectedUtc is { } value ? new DateTimeOffset(value) : null;
        Assert.Equal(expected, builder.GetNextOccurrence(from));
        for (var index = 0; index < 1000; index++) _ = builder.GetNextOccurrence(from);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1000; index++) _ = builder.GetNextOccurrence(from);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
