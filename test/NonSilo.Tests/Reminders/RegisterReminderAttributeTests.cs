using System;
using Orleans.Runtime;
using Xunit;

namespace NonSilo.Tests.Reminders;

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
            priority: ReminderPriority.High,
            action: MissedReminderAction.FireImmediately);

        Assert.Equal("interval-reminder", attribute.Name);
        Assert.Equal(TimeSpan.FromSeconds(15), attribute.Due);
        Assert.Equal(TimeSpan.FromSeconds(60), attribute.Period);
        Assert.Null(attribute.Cron);
        Assert.Equal(ReminderPriority.High, attribute.Priority);
        Assert.Equal(MissedReminderAction.FireImmediately, attribute.Action);
    }

    [Fact]
    public void IntervalCtor_RejectsInvalidInputs()
    {
        Assert.Throws<ArgumentException>(() => new RegisterReminderAttribute("", 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegisterReminderAttribute("r", -1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegisterReminderAttribute("r", 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegisterReminderAttribute("r", double.NaN, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegisterReminderAttribute("r", 1, double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegisterReminderAttribute("r", 1, 1, (ReminderPriority)255, MissedReminderAction.Skip));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegisterReminderAttribute("r", 1, 1, ReminderPriority.Normal, (MissedReminderAction)255));
    }

    [Fact]
    public void CronCtor_SetsExpectedValues()
    {
        var attribute = new RegisterReminderAttribute(
            "cron-reminder",
            "0 9 * * MON-FRI",
            priority: ReminderPriority.Normal,
            action: MissedReminderAction.Notify);

        Assert.Equal("cron-reminder", attribute.Name);
        Assert.Equal("0 9 * * MON-FRI", attribute.Cron);
        Assert.Null(attribute.Due);
        Assert.Null(attribute.Period);
        Assert.Equal(ReminderPriority.Normal, attribute.Priority);
        Assert.Equal(MissedReminderAction.Notify, attribute.Action);
    }

    [Fact]
    public void CronCtor_RejectsInvalidInputs()
    {
        Assert.Throws<ArgumentException>(() => new RegisterReminderAttribute("", "* * * * *"));
        Assert.Throws<ArgumentException>(() => new RegisterReminderAttribute("r", " "));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegisterReminderAttribute("r", "* * * * *", (ReminderPriority)255, MissedReminderAction.Skip));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegisterReminderAttribute("r", "* * * * *", ReminderPriority.Normal, (MissedReminderAction)255));
    }
}
