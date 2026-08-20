---
title: Grain timers and reminders
description: Schedule activation-scoped and durable periodic work in Orleans.
ms.date: 08/20/2026
ms.topic: concept-article
---

# Grain timers and reminders

Orleans provides two mechanisms for periodic grain work:

- **Grain timers** belong to one activation. They stop when that activation deactivates or its silo fails.
- **Reminders** belong to a logical grain. Their definitions are stored and can reactivate the grain after deactivation or cluster restart.

Use a timer for frequent, activation-scoped work. Use a reminder when the schedule must survive activation changes and occasional missed ticks are acceptable.

## Grain timers

Register timers with <xref:Orleans.GrainBaseExtensions.RegisterGrainTimer*>. <xref:Orleans.Grain.RegisterTimer*> is obsolete.

:::code language="csharp" source="../snippets/compiled/Grains/WorkersAndTimersSnippets.cs" id="grain_timer":::
<xref:Orleans.GrainBaseExtensions.RegisterGrainTimer*> returns <xref:Orleans.Runtime.IGrainTimer>. Dispose it to stop the timer, or call <xref:Orleans.Runtime.IGrainTimer.Change*> to change its due time and period.

### Timer behavior

<xref:Orleans.Runtime.GrainTimerCreationOptions> controls scheduling:

| Property | Default | Behavior |
|---|---:|---|
| <xref:Orleans.Runtime.GrainTimerCreationOptions.DueTime> | Required | Delay before the first callback. |
| <xref:Orleans.Runtime.GrainTimerCreationOptions.Period> | Required | Delay from callback completion until the next callback. |
| <xref:Orleans.Runtime.GrainTimerCreationOptions.Interleave> | `false` | Whether callbacks can interleave with other grain requests. |
| <xref:Orleans.Runtime.GrainTimerCreationOptions.KeepAlive> | `false` | Whether timer activity extends activation lifetime. |

A timer callback never overlaps itself. Orleans waits for the callback task to complete before measuring the next period. Exceptions are logged, and later ticks continue.

The callback token is canceled when the timer is disposed or the grain begins deactivating. Timer callbacks execute as grain turns, participate in call filters and tracing, and don't interleave with other requests unless configured or allowed by the grain's reentrancy settings.

## Reminders

A grain receiving reminders implements <xref:Orleans.IRemindable>:

:::code language="csharp" source="../snippets/compiled/Grains/WorkersAndTimersSnippets.cs" id="remindable_report_grain":::
Register or update a reminder from the grain:

:::code language="csharp" source="../snippets/compiled/Grains/WorkersAndTimersSnippets.cs" id="register_reminder":::
Cancel it explicitly:

:::code language="csharp" source="../snippets/compiled/Grains/WorkersAndTimersSnippets.cs" id="unregister_reminder":::
Store the reminder name, not the <xref:Orleans.Runtime.IGrainReminder> handle, across activations. Handles aren't guaranteed to remain valid beyond the activation that retrieved them.

### Reminder behavior

Reminder definitions are durable, but individual tick messages aren't. If the cluster is unavailable at a scheduled time, that occurrence can be missed. The next scheduled tick still occurs. Reminder delivery follows normal grain request scheduling and can activate an inactive grain.

Reminders are intended for periods measured in minutes, hours, or days, not high-frequency scheduling. A common pattern is for a reminder to wake a grain and create a finer-grained local timer.

### Reminder timing constraints

Reminder timing is subject to the following constraints:

- `dueTime` must be greater than or equal to `TimeSpan.Zero`; a zero `dueTime` means the first tick is scheduled immediately.
- `dueTime` cannot be negative or <xref:System.Threading.Timeout.InfiniteTimeSpan>.
- `period` must be greater than `TimeSpan.Zero`.
- `period` cannot be negative, zero, or <xref:System.Threading.Timeout.InfiniteTimeSpan>.
- The runtime rejects `period` values below the lower bound configured by <xref:Orleans.Hosting.ReminderOptions.MinimumReminderPeriod?displayProperty=nameWithType> (default: one minute).
- `dueTime` is also bounded by the remaining <xref:System.DateTime> range from the time of registration. A value which would place the first tick after <xref:System.DateTime.MaxValue> is rejected rather than clamped. Later occurrences are scheduled from the persisted start time and period.

There is no special `period` value that means "fire once and never again." To model a one-shot reminder, create a valid reminder with a positive `period`, then unregister it in the first callback or after the first tick. `TimeSpan.Zero` and negative values are rejected by the runtime rather than treated as a one-shot schedule.

## Configure reminder storage

Every silo must configure a reminder provider. Production deployments should use a durable provider such as Azure Table, ADO.NET, Redis, or [Cosmos DB](https://www.nuget.org/packages/Microsoft.Orleans.Reminders.Cosmos). In-memory reminders are suitable only for local development and tests because definitions are lost when the cluster stops.

Configure each provider through the API supplied by its package. See [Configure Amazon DynamoDB reminders](reminders/dynamodb.md) for a compiled example which configures DynamoDB clustering and reminder storage independently. For other compiled in-repository examples, see the [reminder configuration snippets](https://github.com/dotnet/orleans/tree/main/docs/site/src/content/docs/grains/snippets/timers). When composing resources with Aspire, see [Orleans and Aspire integration](../host/aspire-integration.md).

## POCO grains

Grains implementing <xref:Orleans.IGrainBase> directly can use the same extension APIs. Inject <xref:Orleans.Timers.ITimerRegistry> or <xref:Orleans.Timers.IReminderRegistry> when lower-level registration is required. See [POCO grains](../migration-guide.md#poco-grains-and-igrainbase) for the interface-only grain model.
