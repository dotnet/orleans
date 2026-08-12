# Reminder concurrency control

Orleans reminders are durable timers which periodically call
`IRemindable.ReceiveReminder`. When many reminders become due together, their
ticks can create a burst of grain calls and overload the silo or a downstream
dependency.

Reminder concurrency control is an opt-in admission pipeline which can limit
the number and rate of reminder ticks dispatched by each silo. If it isn't
configured, reminder delivery behavior is unchanged.

## Configure reminder concurrency control

Call `AddReminderConcurrencyControl` after configuring reminders on the silo:

```csharp
using Orleans.Hosting;
using Orleans.Reminders.Concurrency;

siloBuilder
    .AddReminders()
    .UseAzureTableReminderService(options =>
    {
        options.ConfigureTableServiceClient(connectionString);
    })
    .AddReminderConcurrencyControl(options => options
        .PerSilo(throttle => throttle
            .MaxConcurrent(50, ThrottleBlockMode.Wait)
            .PermitsPerSecond(
                value: 200,
                burstSize: 200,
                blockMode: ThrottleBlockMode.Wait)));
```

This example limits each silo to:

- 50 concurrent `ReceiveReminder` calls.
- A sustained dispatch rate of 200 ticks per second.
- A burst of up to 200 ticks.

Both limits use `ThrottleBlockMode.Wait`, so a tick waits for capacity instead
of being skipped.

At least one of `MaxConcurrent`, `PermitsPerSecond`, or `RespectOverload` must
be configured. Invalid combinations are rejected during silo startup.

## Choose limits

`MaxConcurrent` limits the number of reminder calls in flight. Use it when
each reminder consumes a constrained resource, such as a database connection
or an outbound HTTP request.

`PermitsPerSecond` uses a token bucket to limit the sustained dispatch rate.
Use it for dependencies which impose a request-rate limit. The `burstSize`
controls how many ticks can be dispatched together when tokens have
accumulated.

The two limits can be combined. A tick must pass every configured admission
gate before Orleans dispatches it.

## Choose blocking behavior

Every admission gate requires an explicit `ThrottleBlockMode`:

| Mode | Behavior |
| --- | --- |
| `ThrottleBlockMode.Wait` | Wait for capacity. If all gates use this mode, ticks aren't skipped, but tardiness is unbounded. |
| `ThrottleBlockMode.WaitUpTo(timeout)` | Wait up to the timeout, then skip the tick. |
| `ThrottleBlockMode.SkipImmediately` | Skip the tick when capacity isn't immediately available. |

Sequential admission gates share one timeout budget. For example, if an
overload gate consumes 300 ms of a 500 ms `WaitUpTo` budget, later gates have
at most 200 ms remaining. Once a gate establishes a deadline, it also bounds
later gates configured with `Wait`.

When a tick is skipped, Orleans doesn't call `ReceiveReminder` for that tick.
The next scheduled tick is considered independently. Don't use skipped ticks
for work which must run exactly once.

## Back off when the silo is overloaded

Use `RespectOverload` to stop admitting reminder work while the silo's
`IOverloadDetector` reports CPU or memory pressure:

```csharp
.AddReminderConcurrencyControl(options => options
    .PerSilo(throttle => throttle
        .MaxConcurrent(100, ThrottleBlockMode.Wait)
        .RespectOverload(
            ThrottleBlockMode.WaitUpTo(TimeSpan.FromSeconds(30)))));
```

The overload gate runs before concurrency and rate gates, so waiting for the
silo to recover doesn't consume their permits.

## Ramp up after startup

Slow start reduces reminder fan-out while a silo warms caches, connection
pools, JIT-compiled code, and thread-pool capacity:

```csharp
.AddReminderConcurrencyControl(options => options
    .PerSilo(throttle => throttle
        .MaxConcurrent(100, ThrottleBlockMode.Wait)
        .SlowStart(
            initialCapacity: 10,
            interval: TimeSpan.FromSeconds(10),
            onCapacityExceeded: ThrottleBlockMode.Wait)));
```

Capacity starts at 10 and doubles after each interval until it reaches the
configured `MaxConcurrent` value. Slow start requires `MaxConcurrent`, and
`initialCapacity` must not exceed that limit.

## Delivery semantics

Concurrency control deliberately trades scheduling accuracy for load
protection:

- Waiting can make a tick arrive later than its scheduled time.
- `SkipImmediately` and expired `WaitUpTo` waits can omit a tick entirely.
- `TickStatus.CurrentTickTime` reflects the actual dispatch time after
  admission.
- Updating a reminder schedule cancels any admission wait for the stale
  schedule. The replacement schedule is then armed normally.

Use `Wait` when delayed execution is acceptable but skipped ticks aren't. Use
`WaitUpTo` or `SkipImmediately` when protecting downstream capacity is more
important than delivering every tick.

## Observability

Reminder concurrency control publishes metrics through the
`Microsoft.Orleans` meter:

| Metric | Description |
| --- | --- |
| `orleans-reminders-throttle-queued-duration` | Time spent waiting for admission. |
| `orleans-reminders-throttle-active-leases` | Current admitted reminder deliveries by tier. |
| `orleans-reminders-ticks-skipped` | Skipped ticks grouped by tier and reason. |

Subscribe to the `Microsoft.Orleans.Reminders` activity source to collect
`Reminder.Dispatch` spans:

```csharp
services.AddOpenTelemetry()
    .WithTracing(tracing =>
        tracing.AddSource("Microsoft.Orleans.Reminders"));
```

`Orleans.Reminders.Diagnostics.ReminderEvents.TickSkipped` is emitted for
in-process diagnostic subscribers when admission skips a tick.

## Custom admission policies

`IReminderDeliveryThrottle` is the extension point for custom admission
policies:

```csharp
services.AddSingleton<IReminderDeliveryThrottle, MyReminderDeliveryThrottle>();
```

Custom implementations must:

- Return an admitted or skipped `ReminderDeliveryLease`.
- Reclaim admission resources when an admitted lease is disposed.
- Return an appropriate `ReminderSkipReason` for skipped ticks.
- Treat cancellation as a hard stop because the silo is shutting down or the
  reminder schedule changed.
