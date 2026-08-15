# Microsoft Orleans Advanced Reminders

Advanced Reminders adds one-shot, interval, and cron schedules, priorities, missed-reminder policies, and administrative paging on top of Orleans Durable Jobs.

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

Optional cleanup policies operate on each due reminder and do not scan the reminder table:

```csharp
siloBuilder.AddAdvancedReminders(options =>
{
    // Delete a due reminder when its grain type is absent from every active silo manifest.
    options.DeleteReminderWhenGrainTypeIsUnavailable = true;

    // Safety valve: remove a reminder which is still failing at Durable Jobs dequeue 3.
    options.MaximumDeliveryAttempts = 3;
});
```

Both policies are independent and disabled by default: `DeleteReminderWhenGrainTypeIsUnavailable` is `false` and `MaximumDeliveryAttempts` is `null`. Either policy can be enabled without the other. Type-based cleanup only deletes after the cluster manifest is complete for a stable set of active silos, but grain types can still be deliberately absent during deployment, so enable it only when that tradeoff is acceptable. `MaximumDeliveryAttempts` is a safety limit for preventing persistently broken reminders from consuming delivery resources indefinitely, not an exact callback-exception counter. If configured, it must be positive and the Durable Jobs retry policy must allow at least that many attempts.

For an explicit administrative cleanup, page only matching entries with `EnumerateFilteredAsync(new ReminderQueryFilter { GrainType = Orleans.Runtime.GrainType.Create("credentialvaultkeyrotation") })`, inspect them, and call `DeleteAsync` for the selected rows. This is a bounded server-side filter but still scans storage because reminder tables are not indexed by grain type.

Use `ReminderSchedule.OneShot(dueTime)` or `ReminderSchedule.OneShot(dueAtUtc)` for durable work which must fire once. A one-shot registration removes itself after its callback completes.

## Documentation

- [Microsoft Orleans documentation](https://learn.microsoft.com/dotnet/orleans/)
- [Orleans timers and reminders](https://learn.microsoft.com/dotnet/orleans/grains/timers-and-reminders)
