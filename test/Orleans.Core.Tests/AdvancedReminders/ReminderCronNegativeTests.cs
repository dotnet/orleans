using Orleans.AdvancedReminders;
using Xunit;

namespace UnitTests.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders")]
public class ReminderCronNegativeTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public void MissingExpression_IsRejectedWithoutAnOutput(string? text)
    {
        var parsed = ReminderCronExpression.Parse("* * * * *");
        Assert.False(ReminderCronExpression.TryParse(text!, out parsed));
        Assert.Null(parsed);
        Assert.ThrowsAny<ArgumentException>(() => ReminderCronExpression.Parse(text!));
        Assert.ThrowsAny<ArgumentException>(() => ReminderCronBuilder.FromExpression(text!));
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void NonUtcArguments_AreRejectedByEveryQuery(DateTimeKind kind)
    {
        var valid = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var invalid = DateTime.SpecifyKind(valid, kind);
        var expression = ReminderCronExpression.Parse("* * * * *");
        var builder = ReminderCronBuilder.FromExpression("* * * * *", TimeZoneInfo.FindSystemTimeZoneById("Asia/Kathmandu"));

        Assert.Throws<ArgumentException>(() => expression.GetNextOccurrence(invalid));
        Assert.Throws<ArgumentException>(() => expression.GetOccurrences(invalid, valid).ToArray());
        Assert.Throws<ArgumentException>(() => expression.GetOccurrences(valid, invalid).ToArray());
        Assert.Throws<ArgumentException>(() => builder.GetNextOccurrence(invalid));
        Assert.Throws<ArgumentException>(() => builder.GetOccurrences(invalid, valid).ToArray());
        Assert.Throws<ArgumentException>(() => builder.GetOccurrences(valid, invalid).ToArray());
    }

    [Theory]
    [InlineData("UTC")]
    [InlineData("America/New_York")]
    public void ReversedRange_ThrowsAndDoesNotPoisonSubsequentQueries(string zoneId)
    {
        var builder = ReminderCronBuilder.FromExpression("* * * * * *", TimeZoneInfo.FindSystemTimeZoneById(zoneId));
        var from = DateTime.UnixEpoch;
        Assert.Throws<ArgumentException>(() => builder.GetOccurrences(from.AddSeconds(1), from).ToArray());
        Assert.Equal(from.AddSeconds(1), builder.GetNextOccurrence(from));
    }

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(false, true, 0)]
    [InlineData(true, false, 0)]
    [InlineData(true, true, 1)]
    public void ZeroLengthRange_RespectsBothEndpointFlags(bool fromInclusive, bool toInclusive, int count)
    {
        var expression = ReminderCronExpression.Parse("* * * * * *");
        var occurrences = expression.GetOccurrences(DateTime.UnixEpoch, DateTime.UnixEpoch, fromInclusive, toInclusive).ToArray();
        Assert.Equal(count, occurrences.Length);
        Assert.All(occurrences, value => Assert.Equal(DateTime.UnixEpoch, value));
    }

    [Theory]
    [InlineData("1,,2 * * * *")]
    [InlineData("0 0 * * MON#6")]
    [InlineData("@unknown")]
    public void FailedLazyParse_RemainsAnErrorAndCannotAffectAnotherBuilder(string text)
    {
        var invalid = ReminderCronBuilder.FromExpression(text);
        var copy = invalid.InTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Asia/Kathmandu"));
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.ThrowsAny<FormatException>(() => invalid.Build());
            Assert.ThrowsAny<FormatException>(() => copy.GetNextOccurrence(DateTime.UnixEpoch));
            Assert.False(ReminderCronExpression.TryParse(text, out var parsed));
            Assert.Null(parsed);
        }

        Assert.Equal(DateTime.UnixEpoch.AddSeconds(1), ReminderCronBuilder.FromExpression("* * * * * *").GetNextOccurrence(DateTime.UnixEpoch));
    }

    [Fact]
    public void InvalidZoneChange_DoesNotMutateTheOriginalBuilder()
    {
        var builder = ReminderCronBuilder.DailyAt(9, 0);
        Assert.Throws<ArgumentNullException>(() => builder.InTimeZone((TimeZoneInfo)null!));
        Assert.Throws<ArgumentNullException>(() => builder.InTimeZone((string)null!));
        Assert.Throws<ArgumentException>(() => builder.InTimeZone(" "));
        Assert.Throws<TimeZoneNotFoundException>(() => builder.InTimeZone("Orleans/NoSuchTimeZone"));
        Assert.Equal(TimeZoneInfo.Utc, builder.TimeZone);
        Assert.Equal(DateTime.UnixEpoch.AddHours(9), builder.GetNextOccurrence(DateTime.UnixEpoch));
    }

    [Fact]
    public void UtcDateTimeLimits_RespectSecondPrecisionWithoutOverflow()
    {
        var expression = ReminderCronExpression.Parse("* * * * * *");
        var minimum = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
        var maximum = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);
        var lastSecond = maximum.AddTicks(-(TimeSpan.TicksPerSecond - 1));
        Assert.Equal(minimum, expression.GetNextOccurrence(minimum, inclusive: true));
        Assert.Equal(minimum.AddSeconds(1), expression.GetNextOccurrence(minimum));
        Assert.Equal(lastSecond, expression.GetNextOccurrence(lastSecond, inclusive: true));
        Assert.Null(expression.GetNextOccurrence(lastSecond));
        Assert.Null(expression.GetNextOccurrence(maximum, inclusive: true));
        Assert.Empty(expression.GetOccurrences(maximum, maximum, true, true));
    }
}
