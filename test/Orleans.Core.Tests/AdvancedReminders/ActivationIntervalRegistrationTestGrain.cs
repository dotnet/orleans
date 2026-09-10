#nullable enable
using Orleans.AdvancedReminders;
using AdvancedRemindable = Orleans.AdvancedReminders.IRemindable;
using AdvancedTickStatus = Orleans.AdvancedReminders.Runtime.TickStatus;

namespace UnitTests.AdvancedReminders;

[RegisterReminder("interval-activation-registration", dueSeconds: 5, periodSeconds: 30)]
internal sealed class ActivationIntervalRegistrationTestGrain : Grain, IActivationIntervalRegistrationTestGrain, AdvancedRemindable
{
    public Task ReceiveReminder(string reminderName, AdvancedTickStatus status) => Task.CompletedTask;
}
