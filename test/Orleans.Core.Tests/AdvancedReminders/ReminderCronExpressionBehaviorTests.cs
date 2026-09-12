#nullable enable
using Orleans.AdvancedReminders;
using Xunit;

namespace UnitTests.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders")]
public class ReminderCronExpressionBehaviorTests
{
    [Fact]
    public void TryParse_BlankExpression_ReturnsFalse()
    {
        var result = ReminderCronExpression.TryParse("   ", out var expression);

        Assert.False(result);
        Assert.Null(expression);
    }

    [Fact]
    public void FromValidatedString_PreservesExpressionText()
    {
        var expression = ReminderCronExpression.FromValidatedString("0 9 * * *");

        Assert.Equal("0 9 * * *", expression.ExpressionText);
        Assert.Equal("0 9 * * *", expression.ToExpressionString());
        Assert.Equal("0 9 * * *", expression.ToString());
    }

    [Fact]
    public void Equality_UsesOrdinalExpressionText()
    {
        var first = ReminderCronExpression.Parse("0 9 * * *");
        var second = ReminderCronExpression.Parse("0 9 * * *");
        var different = ReminderCronExpression.Parse("0 10 * * *");

        Assert.True(first.Equals(second));
        Assert.True(first.Equals((object)second));
        Assert.False(first.Equals(different));
        Assert.False(first.Equals((object)"0 9 * * *"));
        Assert.False(first.Equals(null));
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void GetNextOccurrence_WithNonUtcDateTime_Throws()
    {
        var expression = ReminderCronExpression.Parse("0 9 * * *");
        var local = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);

        Assert.Throws<ArgumentException>(() => expression.GetNextOccurrence(local));
    }

    [Fact]
    public void GetNextOccurrence_AfterMaximumDate_ReturnsNull()
    {
        var expression = ReminderCronExpression.Parse("* * * * *");

        Assert.Null(expression.GetNextOccurrence(DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc)));
    }

    [Fact]
    public void GetOccurrences_WithNonUtcRange_Throws()
    {
        var expression = ReminderCronExpression.Parse("0 9 * * *");
        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Local);

        Assert.Throws<ArgumentException>(() => expression.GetOccurrences(from, to).ToArray());
    }
}
