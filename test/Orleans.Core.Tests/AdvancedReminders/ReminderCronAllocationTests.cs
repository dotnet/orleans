using Orleans.AdvancedReminders;
using Xunit;

namespace UnitTests.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders")]
public class ReminderCronAllocationTests
{
    [Theory]
    [MemberData(nameof(ReminderCronAllocationTestCases.Occurrences), MemberType = typeof(ReminderCronAllocationTestCases))]
    public void RepeatedQueries_DoNotAllocateAfterWarmup(string expression, string zoneId, DateTime from, DateTime? expected)
    {
        var builder = ReminderCronBuilder.FromExpression(expression, ReminderCronTestTimeZones.Resolve(zoneId));
        Assert.Equal(expected, builder.GetNextOccurrence(from));
        for (var index = 0; index < 1000; index++) _ = builder.GetNextOccurrence(from);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1000; index++) _ = builder.GetNextOccurrence(from);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Theory]
    [InlineData("* * * * * *")]
    [InlineData("0 9 * * *")]
    [InlineData("5-45/10 0,15,30,45 8-18 ? JAN,MAR MON-FRI")]
    public void ParsingCommonExpressions_StaysWithinAllocationBudget(string text)
    {
        for (var index = 0; index < 100; index++) _ = ReminderCronExpression.Parse(text);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 100; index++) _ = ReminderCronExpression.Parse(text);
        var allocatedPerParse = (GC.GetAllocatedBytesForCurrentThread() - before) / 100;

        Assert.InRange(allocatedPerParse, 0, 1024);
    }
}
