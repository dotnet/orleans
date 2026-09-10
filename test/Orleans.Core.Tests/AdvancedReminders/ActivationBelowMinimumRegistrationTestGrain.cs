#nullable enable
using Orleans.AdvancedReminders;
using AdvancedRemindable = Orleans.AdvancedReminders.IRemindable;
using AdvancedTickStatus = Orleans.AdvancedReminders.Runtime.TickStatus;

namespace UnitTests.AdvancedReminders;

[RegisterReminder("below-minimum-activation-registration", dueSeconds: 0, periodSeconds: 1)]
internal sealed class ActivationBelowMinimumRegistrationTestGrain : Grain, IActivationBelowMinimumRegistrationTestGrain, AdvancedRemindable
{
    public Task ReceiveReminder(string reminderName, AdvancedTickStatus status) => Task.CompletedTask;
}
