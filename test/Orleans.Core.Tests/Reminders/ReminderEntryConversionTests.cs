using Orleans.Runtime;
using Orleans.Runtime.ReminderService;
using Xunit;

namespace NonSilo.Tests.Reminders;

[TestCategory("Reminders")]
public class ReminderEntryConversionTests
{
    [Fact]
    public void ReminderEntry_ToIGrainReminder_ExposesCronTimeZone()
    {
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "key"),
            ReminderName = "rem",
            ETag = "etag",
            CronExpression = "0 */5 * * * *",
            CronTimeZoneId = "America/New_York",
            Priority = ReminderPriority.High,
            Action = MissedReminderAction.FireImmediately,
        };

        var reminder = entry.ToIGrainReminder();

        Assert.Equal(entry.ReminderName, reminder.ReminderName);
        Assert.Equal(entry.CronExpression, reminder.CronExpression);
        Assert.Equal(entry.CronTimeZoneId, reminder.CronTimeZone);
        Assert.Equal(entry.Priority, reminder.Priority);
        Assert.Equal(entry.Action, reminder.Action);
    }

    [Fact]
    public void ReminderEntry_ToIGrainReminder_NormalizesNullCronFields()
    {
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "key"),
            ReminderName = "rem",
            ETag = "etag",
            CronExpression = null,
            CronTimeZoneId = null,
        };

        var reminder = entry.ToIGrainReminder();

        Assert.Equal(string.Empty, reminder.CronExpression);
        Assert.Equal(string.Empty, reminder.CronTimeZone);
    }
}
