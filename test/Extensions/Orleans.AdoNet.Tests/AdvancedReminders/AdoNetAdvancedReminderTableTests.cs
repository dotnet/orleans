#nullable enable
extern alias AdvancedRemindersAdoNet;

using Xunit;
using AdvancedReminderEntry = Orleans.AdvancedReminders.ReminderEntry;
using AdoNetReminderTable = AdvancedRemindersAdoNet::Orleans.AdvancedReminders.Runtime.ReminderService.AdoNetReminderTable;

namespace UnitTests.AdvancedRemindersTest;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
public class AdoNetAdvancedReminderTableTests
{
    [Fact]
    public void NormalizeUtcFields_SpecifiesUtcKindWithoutChangingTicks()
    {
        var startAt = new DateTime(2026, 7, 22, 10, 0, 0, DateTimeKind.Unspecified);
        var nextDueUtc = startAt.AddMinutes(10);
        var lastFireUtc = startAt.AddMinutes(-2);
        var reminder = new AdvancedReminderEntry
        {
            StartAt = startAt,
            NextDueUtc = nextDueUtc,
            LastFireUtc = lastFireUtc,
        };

        AdoNetReminderTable.NormalizeUtcFields(reminder);

        Assert.Equal(DateTimeKind.Utc, reminder.StartAt.Kind);
        Assert.Equal(startAt.Ticks, reminder.StartAt.Ticks);
        Assert.Equal(DateTimeKind.Utc, reminder.NextDueUtc.Value.Kind);
        Assert.Equal(nextDueUtc.Ticks, reminder.NextDueUtc.Value.Ticks);
        Assert.Equal(DateTimeKind.Utc, reminder.LastFireUtc.Value.Kind);
        Assert.Equal(lastFireUtc.Ticks, reminder.LastFireUtc.Value.Ticks);
    }
}
