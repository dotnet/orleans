#nullable enable
using Orleans.AdvancedReminders;

namespace UnitTests.AdvancedReminders;

[RegisterReminder("non-remindable", dueSeconds: 1, periodSeconds: 5)]
internal sealed class ActivationNonRemindableTestGrain : Grain, IActivationNonRemindableTestGrain;
