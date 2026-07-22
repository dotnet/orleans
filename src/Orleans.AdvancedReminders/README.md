# Microsoft Orleans Advanced Reminders

Advanced Reminders adds interval and cron schedules, priorities, missed-reminder policies, and administrative paging on top of Orleans Durable Jobs.

> This package is prerelease. The in-memory provider is intended for development and testing because its reminder definitions and jobs do not survive a full cluster restart.

## Configuration

Install `Microsoft.Orleans.AdvancedReminders`, then configure a reminder-table provider. For local development:

```csharp
using Orleans.Hosting;

builder.UseOrleans(siloBuilder =>
{
    siloBuilder
        .UseLocalhostClustering()
        .UseInMemoryAdvancedReminderService();
});
```

For production, use one of the storage-backed provider packages instead, such as Azure Storage, Cosmos DB, DynamoDB, ADO.NET, or Redis. The provider also configures or requires the corresponding Durable Jobs backend.

## Grain usage

Advanced reminder methods have explicit `AdvancedReminder` names so that classic and advanced reminders can be referenced from the same application without ambiguous extension-method calls.

```csharp
using Orleans;
using Orleans.AdvancedReminders;
using Orleans.AdvancedReminders.Runtime;

public sealed class CleanupGrain : Grain, ICleanupGrain, Orleans.AdvancedReminders.IRemindable
{
    public Task StartAsync() => this.RegisterOrUpdateAdvancedReminder(
        "cleanup",
        ReminderCronBuilder.DailyAt(2, 0),
        ReminderPriority.High,
        MissedReminderAction.FireImmediately);

    public Task ReceiveReminder(string reminderName, TickStatus status)
    {
        // Perform the scheduled work.
        return Task.CompletedTask;
    }
}
```

Use `GetAdvancedReminder`, `GetAdvancedReminders`, and `UnregisterAdvancedReminder` to inspect or remove registrations. `ReminderOptions.MinimumReminderPeriod` applies to both interval and cron registrations.

## Documentation

- [Microsoft Orleans documentation](https://learn.microsoft.com/dotnet/orleans/)
- [Orleans timers and reminders](https://learn.microsoft.com/dotnet/orleans/grains/timers-and-reminders)
