#nullable enable
using AdvancedRemindable = Orleans.AdvancedReminders.IRemindable;
using AdvancedTickStatus = Orleans.AdvancedReminders.Runtime.TickStatus;

namespace UnitTests.AdvancedReminders;

internal sealed class ActivationNoAttributeTestGrain : Grain, IActivationNoAttributeTestGrain, AdvancedRemindable
{
    public Task ReceiveReminder(string reminderName, AdvancedTickStatus status) => Task.CompletedTask;
}
