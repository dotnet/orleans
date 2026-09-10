namespace Orleans.AdvancedReminders.Runtime.ReminderService;

internal interface IAdvancedReminderRecoveryGrain : IGrainWithIntegerKey
{
    Task StartAsync(bool force, CancellationToken cancellationToken);
}
