#nullable enable
using Orleans.AdvancedReminders;
using Orleans.AdvancedReminders.Runtime;
using Xunit;

namespace UnitTests.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders")]
public class RegisterReminderAttributeTests
{
    [Fact]
    public void IntervalCtor_SetsExpectedValues()
    {
        var attribute = new RegisterReminderAttribute(
            "interval-reminder",
            dueSeconds: 15,
            periodSeconds: 60,
            action: MissedReminderAction.FireImmediately);

        Assert.Equal("interval-reminder", attribute.Name);
        Assert.Equal(TimeSpan.FromSeconds(15), attribute.Due);
        Assert.Equal(TimeSpan.FromSeconds(60), attribute.Period);
        Assert.Null(attribute.Cron);
        Assert.Equal(MissedReminderAction.FireImmediately, attribute.Action);
    }

    [Fact]
    public void IntervalCtor_RejectsInvalidInputs()
    {
        Assert.Throws<ArgumentException>(() => new RegisterReminderAttribute("", 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegisterReminderAttribute("r", -1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegisterReminderAttribute("r", 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegisterReminderAttribute("r", 1, 1, (MissedReminderAction)255));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegisterReminderAttribute("r", double.MaxValue, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegisterReminderAttribute("r", 1, double.MaxValue));
    }

    [Fact]
    public void CronCtor_SetsExpectedValues()
    {
        var attribute = new RegisterReminderAttribute(
            "cron-reminder",
            "0 9 * * MON-FRI",
            action: MissedReminderAction.Notify);

        Assert.Equal("cron-reminder", attribute.Name);
        Assert.Equal("0 9 * * MON-FRI", attribute.Cron);
        Assert.Null(attribute.Due);
        Assert.Null(attribute.Period);
        Assert.Equal(MissedReminderAction.Notify, attribute.Action);
    }

    [Fact]
    public void CronCtor_RejectsInvalidInputs()
    {
        Assert.Throws<ArgumentException>(() => new RegisterReminderAttribute("", "* * * * *"));
        Assert.Throws<ArgumentException>(() => new RegisterReminderAttribute("r", " "));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegisterReminderAttribute("r", "* * * * *", (MissedReminderAction)255));
    }
}
