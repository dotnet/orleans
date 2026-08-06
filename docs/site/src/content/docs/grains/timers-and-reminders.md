---
title: Grain timers and reminders
description: Schedule activation-scoped and durable periodic work in Orleans.
ms.date: 08/02/2026
ms.topic: concept-article
---

# Grain timers and reminders

Orleans provides two mechanisms for periodic grain work:

- **Grain timers** belong to one activation. They stop when that activation deactivates or its silo fails.
- **Reminders** belong to a logical grain. Their definitions are stored and can reactivate the grain after deactivation or cluster restart.

Use a timer for frequent, activation-scoped work. Use a reminder when the schedule must survive activation changes and occasional missed ticks are acceptable.

## Grain timers

Register timers with <xref:Orleans.GrainBaseExtensions.RegisterGrainTimer*>. `RegisterTimer` is obsolete.

```csharp
public sealed class CacheGrain : Grain, ICacheGrain
{
    private IGrainTimer? _timer;

    public override Task OnActivateAsync(
        CancellationToken cancellationToken)
    {
        _timer = this.RegisterGrainTimer(
            Refresh,
            new GrainTimerCreationOptions
            {
                DueTime = TimeSpan.Zero,
                Period = TimeSpan.FromMinutes(1)
            });

        return base.OnActivateAsync(cancellationToken);
    }

    private Task Refresh(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
```

`RegisterGrainTimer` returns <xref:Orleans.Runtime.IGrainTimer>. Dispose it to stop the timer, or call <xref:Orleans.Runtime.IGrainTimer.Change*> to change its due time and period.

### Timer behavior

<xref:Orleans.Runtime.GrainTimerCreationOptions> controls scheduling:

| Property | Default | Behavior |
|---|---:|---|
| `DueTime` | Required | Delay before the first callback. |
| `Period` | Required | Delay from callback completion until the next callback. |
| `Interleave` | `false` | Whether callbacks can interleave with other grain requests. |
| `KeepAlive` | `false` | Whether timer activity extends activation lifetime. |

A timer callback never overlaps itself. Orleans waits for the callback task to complete before measuring the next period. Exceptions are logged, and later ticks continue.

The callback token is canceled when the timer is disposed or the grain begins deactivating. Timer callbacks execute as grain turns, participate in call filters and tracing, and don't interleave with other requests unless configured or allowed by the grain's reentrancy settings.

## Reminders

A grain receiving reminders implements <xref:Orleans.IRemindable>:

```csharp
public sealed class ReportGrain :
    Grain,
    IReportGrain,
    IRemindable
{
    public Task ReceiveReminder(
        string reminderName,
        TickStatus status)
    {
        return GenerateReport();
    }

    private Task GenerateReport() => Task.CompletedTask;
}
```

Register or update a reminder from the grain:

```csharp
IGrainReminder reminder = await this.RegisterOrUpdateReminder(
    "daily-report",
    dueTime: TimeSpan.FromMinutes(1),
    period: TimeSpan.FromDays(1));
```

Cancel it explicitly:

```csharp
IGrainReminder? reminder =
    await this.GetReminder("daily-report");

if (reminder is not null)
{
    await this.UnregisterReminder(reminder);
}
```

Store the reminder name, not the `IGrainReminder` handle, across activations. Handles aren't guaranteed to remain valid beyond the activation that retrieved them.

### Reminder behavior

Reminder definitions are durable, but individual tick messages aren't. If the cluster is unavailable at a scheduled time, that occurrence can be missed. The next scheduled tick still occurs. Reminder delivery follows normal grain request scheduling and can activate an inactive grain.

Reminders are intended for periods measured in minutes, hours, or days, not high-frequency scheduling. A common pattern is for a reminder to wake a grain and create a finer-grained local timer.

## Configure reminder storage

Every silo must configure a reminder provider. Production deployments should use a durable provider such as Azure Table, ADO.NET, Redis, or Cosmos DB. In-memory reminders are suitable only for local development and tests because definitions are lost when the cluster stops.

The provider-specific configuration is covered by each reminder provider package. For a compiled in-repository configuration example, see the [reminder configuration snippets](https://github.com/dotnet/orleans/tree/main/docs/site/src/content/docs/grains/snippets/timers).

## POCO grains

Grains implementing <xref:Orleans.IGrainBase> directly can use the same extension APIs. Inject <xref:Orleans.Timers.ITimerRegistry> or <xref:Orleans.Timers.IReminderRegistry> when lower-level registration is required.
