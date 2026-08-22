---
title: Control reminder delivery concurrency
description: Limit reminder delivery concurrency and rate to protect silos and downstream dependencies.
ms.date: 08/21/2026
ms.topic: how-to
---

# Control reminder delivery concurrency

Orleans reminders can become due in bursts after a silo starts, a reminder range
moves between silos, or a downstream dependency recovers. Standard reminder
delivery uses available silo capacity for <xref:Orleans.IRemindable.ReceiveReminder*>
calls.

Reminder concurrency control adds an opt-in, per-silo admission pipeline before
reminder dispatch. Use it to protect a constrained dependency or to reduce
reminder work while a silo is overloaded. The default no-op throttle preserves
standard reminder delivery.

> [!IMPORTANT]
> The limits apply independently to each silo. Increasing the number of silos
> can increase the cluster-wide reminder dispatch capacity. Configure every silo
> consistently and size limits from the capacity of the protected dependency.

## Configure a per-silo limit

Configure a reminder provider first, and then call
<xref:Orleans.Hosting.SiloBuilderReminderConcurrencyExtensions.AddReminderConcurrencyControl*?displayProperty=nameWithType>.
The following example caps both concurrent and sustained reminder delivery:

:::code language="csharp" source="../snippets/compiled/Grains/WorkersAndTimersSnippets.cs" id="configure_reminder_concurrency":::

This configuration allows:

- At most 50 admitted reminder calls in flight on each silo.
- A sustained rate of 200 admitted reminder calls per second on each silo.
- A burst of up to 200 calls when rate tokens have accumulated.

A tick must pass every configured gate. Configure at least one of
<xref:Orleans.Reminders.Concurrency.ReminderThrottleConfigBuilder.MaxConcurrent*>,
<xref:Orleans.Reminders.Concurrency.ReminderThrottleConfigBuilder.PermitsPerSecond*>,
or <xref:Orleans.Reminders.Concurrency.ReminderThrottleConfigBuilder.RespectOverload*>.
Empty and invalid configurations fail during silo startup.

## Choose controls

| Control | Use when | Important behavior |
|---|---|---|
| `MaxConcurrent` | Each tick holds a limited resource, such as a database connection or downstream request. | Caps admitted calls until their delivery attempt completes. |
| `PermitsPerSecond` | A dependency has a request-rate limit or performs poorly under bursts. | Uses a token bucket. `burstSize` controls how many accumulated tokens can be consumed together. |
| `RespectOverload` | Reminder work should back off during silo CPU or memory pressure. | Checks the silo overload detector before consuming concurrency or rate capacity. |
| `SlowStart` | A starting silo needs time to warm caches, connection pools, JIT-compiled code, or thread-pool capacity. | Starts at `initialCapacity` and doubles after each interval until `MaxConcurrent` is reached. Requires `MaxConcurrent`. |

Combine concurrency and rate limits when the protected dependency has both
constraints. Choose values from measured dependency capacity, not the normal
reminder rate. Account for all silos which can dispatch reminders concurrently.

<xref:Orleans.Reminders.Concurrency.ReminderThrottleConfigBuilder.RespectOverload*>
uses the silo's existing overload detector. Its block mode determines whether a
tick waits or is skipped while the silo is overloaded. Enable
<xref:Orleans.Configuration.LoadSheddingOptions.LoadSheddingEnabled> and configure
its thresholds so the detector reports CPU or memory pressure.

<xref:Orleans.Reminders.Concurrency.ReminderThrottleConfigBuilder.SlowStart*>
limits admitted concurrency during startup. Its initial capacity must be
positive and no greater than `MaxConcurrent`.

## Choose admission behavior

Every gate requires an explicit
<xref:Orleans.Reminders.Concurrency.ThrottleBlockMode>:

| Mode | Behavior when capacity isn't available |
|---|---|
| `Wait` | Wait until capacity is available or the reminder schedule or silo shuts down. Tardiness is unbounded when every gate uses this mode. |
| `WaitUpTo(timeout)` | Wait until capacity is available or the timeout expires. Skip the tick after the timeout. |
| `SkipImmediately` | Skip the tick instead of waiting. |

Sequential gates share one timeout budget. For example, if an overload gate
uses 300 ms of a 500 ms `WaitUpTo` budget, later gates have at most 200 ms
remaining. Once a gate establishes a deadline, it also bounds later gates
configured with `Wait`.

Use `Wait` when delayed execution is acceptable but dropping a tick isn't. Use
`WaitUpTo` or `SkipImmediately` when exceeding downstream capacity is worse than
missing an occurrence.

## Understand delivery semantics

Concurrency control deliberately trades scheduling accuracy for load
protection:

- Waiting can make a tick arrive later than its scheduled time.
- `SkipImmediately` and an expired `WaitUpTo` can omit a tick entirely.
- A skipped occurrence isn't retried. The next scheduled occurrence is
  considered independently.
- <xref:Orleans.Runtime.TickStatus.CurrentTickTime> reflects the actual dispatch
  time after admission.
- Updating a reminder schedule cancels an admission wait for the stale schedule.

Don't use skipped reminder occurrences as the only record of work which must
happen exactly once. Persist required work separately and use a reminder to
prompt its processing.

## Observe and tune limits

Reminder concurrency control publishes metrics through the `Microsoft.Orleans`
meter:

| Instrument | Type | Use |
|---|---|---|
| `orleans-reminders-throttle-queued-duration` | Histogram, seconds | Track admission delay by tier and outcome. |
| `orleans-reminders-throttle-active-leases` | Observable up/down counter | Compare current admitted deliveries with the configured concurrency limit. |
| `orleans-reminders-ticks-skipped` | Counter | Alert on skipped ticks by tier and <xref:Orleans.Reminders.Concurrency.ReminderSkipReason>. |

Monitor high-percentile queued duration, active leases near `MaxConcurrent`,
and the rate of skipped ticks together. Sustained queueing means the configured
capacity is below demand. A rising skip rate means the selected protection
policy is discarding occurrences.

Reminder dispatch also emits `Reminder.Dispatch` activities from the
`Microsoft.Orleans.Reminders` activity source. Subscribe to that source to
trace admitted grain execution. Use
`orleans-reminders-throttle-queued-duration` for the preceding admission delay.
See
[Orleans observability](../host/monitoring/index.md) for OpenTelemetry
configuration.

For in-process diagnostics,
<xref:Orleans.Reminders.Diagnostics.ReminderEvents.TickSkipped> reports the
tier, wait duration, and skip reason.

## Deploy safely

Apply the same settings to every silo which hosts reminders. During a rolling
deployment, silos without concurrency control continue to dispatch reminders
without these limits, so the feature doesn't provide a cluster-wide cap until
the rollout is complete.

Start with metrics-only observation of existing reminder demand, select
conservative limits, and then watch queued duration, skips, reminder tardiness,
downstream latency, and errors during rollout. Increase capacity only when the
protected dependency can safely absorb it.
