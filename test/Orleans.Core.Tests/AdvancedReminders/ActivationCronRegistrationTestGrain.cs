#nullable enable
using Orleans.AdvancedReminders;
using Orleans.AdvancedReminders.Runtime;
using AdvancedRemindable = Orleans.AdvancedReminders.IRemindable;
using AdvancedTickStatus = Orleans.AdvancedReminders.Runtime.TickStatus;

namespace UnitTests.AdvancedReminders;

[RegisterReminder(
    "cron-activation-registration",
    "0 9 * * MON-FRI",
    action: MissedReminderAction.FireImmediately)]
internal sealed class ActivationCronRegistrationTestGrain : Grain, IActivationCronRegistrationTestGrain, AdvancedRemindable
{
    public Task ReceiveReminder(string reminderName, AdvancedTickStatus status) => Task.CompletedTask;
}
