# Reminder concurrency control

Orleans reminders are durable timers that fire `IRemindable.ReceiveReminder` on a grain at a scheduled period. By default each silo dispatches every due reminder as soon as it is ready — there is no built-in cap on how many reminder ticks may be in flight simultaneously or how quickly they may fan out.

Most workloads do not need a cap. Some do:

- A downstream dependency went down for a while; when it comes back, a backlog of reminders that "should have already fired" all become due at once.
- A silo membership change reassigns thousands of reminders to a new silo; they all activate inside a narrow window.
- An application registers a large number of reminders with similar schedules — for example a daily rollup on every user grain.

In any of these cases the cluster can produce a **thundering herd** of `ReceiveReminder` calls and overwhelm the grain, the storage backend, or whatever downstream service the grain depends on. Reminder concurrency control is an opt-in feature that lets you bound the rate and/or in-flight count of reminder dispatches, trading some tick-time accuracy for downstream protection.

> **Opt-in only.** When you do not configure this feature, reminders behave exactly as they always have. The default DI registration is a zero-allocation no-op throttle that admits every dispatch immediately.

## Quickstart

```csharp
siloBuilder
    .AddReminders()
    .UseAzureTableReminderService(/* ... */)
    .AddReminderConcurrencyControl(c => c
        .PerSilo(t => t
            .MaxConcurrent(50)
            .PermitsPerSecond(200)
            .BlockMode(ThrottleBlockMode.Wait)));
```

That configuration says: on this silo, never deliver more than 50 reminder ticks at once, never exceed a sustained rate of 200 ticks/second (with an automatically-derived burst of 200), and if a tick can't be admitted right away, **wait** for capacity rather than skipping the tick.

## Reasoning about the configuration

The feature is intentionally minimal: there is one tier in Phase 1 (the **Per-Silo** tier), and three decisions to make about it. Every choice you make is one we deliberately could not make for you safely. Read through the decisions below before turning the feature on in production.

### 1. Do you actually need it?

If your downstreams comfortably absorb anything reminders can throw at them — for example, all reminder work is in-process state mutation on the same grain, or the grain writes to a backend that auto-scales without complaint — leave the feature off. Concurrency control adds latency variability (some ticks now wait), and that's worse than nothing for workloads that aren't downstream-bound.

Turn it on when:

- A failure investigation traces back to a thundering herd of reminders saturating a backend.
- Capacity planning shows the worst-case "all reminders due at once" scenario exceeds what a dependency can sustain.
- Reminder grains call external APIs with their own rate limits and you want to absorb the rate-limiter on the Orleans side instead of getting `HTTP 429` storms.

### 2. Bound concurrency, bound rate, or both?

- **`MaxConcurrent(N)`** caps the number of in-flight `ReceiveReminder` calls. This is the right knob when each tick holds an expensive resource (a database connection, a downstream HTTP call) and you know how many of those resources are available.
- **`PermitsPerSecond(R)`** caps the sustained dispatch rate. This is the right knob when the downstream complaint is "too many requests per unit time" (a rate-limited API, a database that handles concurrency well but writes per second poorly).
- **Both at once** is allowed and composes by AND: a dispatch must obtain both a concurrency permit and a rate token to be admitted. Use this when both constraints apply.

You must specify at least one of the two. A configuration with neither is rejected at startup rather than silently turning into a no-op.

The token bucket's burst size defaults to `ceil(PermitsPerSecond)` (roughly one second of headroom). If your workload genuinely needs a different burst — for example, you tolerate brief spikes of 5× the sustained rate — set `BurstSize(N)` explicitly. Most users should leave it alone.

### 3. What happens when the limit binds?

Choose by calling `BlockMode(...)` with one of:

| Block mode | Behavior when no permit is available | Trade-off |
|---|---|---|
| `ThrottleBlockMode.Wait` (default) | Wait indefinitely for a permit. | No ticks are dropped, but tardiness is unbounded. The grain may see a tick arrive much later than scheduled. |
| `ThrottleBlockMode.WaitUpTo(timeout)` | Wait up to `timeout`, then skip the tick if no permit became available. | Bounded tardiness, bounded skip rate. Skips are classified as `ReminderSkipReason.AcquireTimeout` for observability. |
| `ThrottleBlockMode.SkipImmediately` | Skip the tick if no permit is available right now. | Maximum downstream protection; minimum delivery guarantee. Best when "missing a tick" is materially better than "exceeding the limit". |

**Reasoning prompts:**

- Is it OK for a grain to *never* receive a particular tick? If the tick represents a periodic check that will repeat soon anyway, dropping one tick costs you nothing. If the tick represents real work that must happen, dropping it is a bug.
- How long can the downstream tolerate the wait? `Wait` is fine when downstream backpressure is the limit you're really respecting (i.e. the wait is the *point*). It's not fine when something else times out at the grain layer waiting on the tick.

## What you trade away

This feature **trades accuracy of timer ticks for concurrency control of dispatch**. That is the whole point — if you are not willing to make that trade, the feature is not for you. Specifically:

- A tick may fire later than its scheduled period (`Wait` and `WaitUpTo` modes).
- A tick may be skipped entirely (`SkipImmediately` and `WaitUpTo` after timeout). The grain will not observe that tick at all; the *next* periodic tick is considered independently.
- The `TickStatus.CurrentTickTime` passed to your grain is the actual dispatch time. If you log the difference between `TickStatus.CurrentTickTime` and `TickStatus.FirstTickTime`, expect a step change after you turn the feature on.

Because of the second point, **avoid building "tick counting" into critical accounting** on top of reminders if you're going to enable `SkipImmediately` or `WaitUpTo`. Reminders are a "fire at least sometime around this period" primitive once concurrency control is on, not a "fire exactly N times" primitive.

## Observability

The feature publishes everything you need to monitor it via standard Orleans diagnostic channels.

### Metrics

All metrics flow through the existing Orleans `Microsoft.Orleans` meter. Tag keys follow OpenTelemetry semantic-convention style (`orleans.reminder.throttle.*`).

| Metric name | Type | Unit | Tags | What it tells you |
|---|---|---|---|---|
| `orleans-reminders-throttle-queued-duration` | Histogram | `s` | `orleans.reminder.throttle.tier`, `orleans.reminder.throttle.outcome` | How long each tick waited for a lease. Watch the P95/P99 to see whether your limit is binding. |
| `orleans-reminders-throttle-active-leases` | UpDownCounter | `{lease}` | `orleans.reminder.throttle.tier` | Currently-held leases per tier. Saturates near `MaxConcurrent` when the concurrency cap is binding. |
| `orleans-reminders-ticks-skipped` | Counter | `{tick}` | `orleans.reminder.throttle.tier`, `orleans.reminder.throttle.skip_reason` | How many ticks were skipped, broken down by reason. The rate of this counter is your "how much tick delivery am I losing" signal. |
| `orleans-reminders-ticks-delivered` | Counter | (existing) | — | Total ticks delivered. Compare against the skipped counter to compute a delivery ratio. |
| `orleans-reminders-tardiness` | Histogram | (existing) | — | End-to-end tardiness. Throttle wait is a component of this; the dedicated `queued-duration` histogram lets you separate "downstream was slow" from "limiter was throttling". |

The metric names use Orleans' established kebab-case convention so they sort and group with every other Orleans metric. Tag keys are dotted lowercase (OTel style) because they are not constrained by legacy naming.

### Traces

A new `ActivitySource` (`Microsoft.Orleans.Reminders`) is added by the reminder runtime. When subscribed (e.g., through `AddOpenTelemetry().WithTracing(t => t.AddSource("Microsoft.Orleans.Reminders"))`), each tick produces a `Reminder.Dispatch` span with the following tags:

- `orleans.reminder.name`
- `orleans.grain.id`
- `orleans.grain.type`
- `orleans.reminder.tardiness`
- `orleans.reminder.throttle.tier` (when a throttle is configured)
- `orleans.reminder.throttle.outcome` (when a throttle is configured)

The grain call to `ReceiveReminder` is wrapped by Orleans' existing application-call ActivitySource; that span becomes a child of `Reminder.Dispatch`, so traces show a clean waterfall: "waited 3 s for the limiter → grain call took 200 ms".

The span is zero-allocation when no listeners are subscribed.

### Diagnostic events

Subscribe to `Orleans.Reminders.Diagnostics.ReminderEvents.AllEvents` (an `IObservable<ReminderEvent>`) for in-process listeners, integration tests, or custom observability sinks. The new event type is:

- `ReminderEvents.TickSkipped` — emitted when a throttle skipped a tick. Fields: `GrainId`, `ReminderName`, `Status`, `Reason` (`ReminderSkipReason`), `TierName`, `WaitedFor`, `SiloAddress`.

The existing `TickFiring`, `TickCompleted`, and `TickFailed` events continue to fire for non-skipped dispatches.

### Logs

The feature emits exactly two log lines on the happy path:

- **Information** at startup: one line per configured tier, with effective values (`MaxConcurrent`, `PermitsPerSecond`, `BurstSize`, `BlockMode`). This lets you confirm at silo start that the configuration loaded as expected.
- *(No per-tick logs.)* Per-tick noise lives in metrics and traces.

## Pit-of-success traps we explicitly closed

These are decisions we deliberately constrained at the API level so you cannot misconfigure them quietly:

- **You cannot construct a `WaitWithTimeout` block mode without a timeout.** The factory `ThrottleBlockMode.WaitUpTo(timeout)` requires a positive `TimeSpan`. Zero/negative values throw at configuration time.
- **You cannot install the feature with no tiers configured.** `AddReminderConcurrencyControl(b => { })` fails startup with a clear message. Silent no-ops are a footgun: you'd think you had protection and not have it.
- **You cannot configure a tier with neither a concurrency cap nor a rate cap.** At least one of `MaxConcurrent` or `PermitsPerSecond` must be set; `ThrottleConfig` validates at build time.
- **You cannot configure `BurstSize` without `PermitsPerSecond`.** Burst is a property of a token bucket; the validator rejects the combination.
- **You cannot configure attribute keys with unit suffixes.** All observability attribute keys follow OTel convention (no `_seconds` etc.); units live on the instrument, not the attribute name.

## What's not in Phase 1

Phase 1 ships the **Per-Silo** tier. The SPI and API shape leave room for the additional tiers planned in later phases:

- **Global** (cluster-wide, single shared budget).
- **Per grain interface** (cluster-wide, scoped to one grain interface type).
- **Per `(grain interface, reminder name)`** (cluster-wide, scoped to a specific named reminder on a grain interface).

When those tiers ship, they will compose with the Per-Silo tier by AND (every applicable tier must admit before a tick fires). The Per-Silo tier's API and behavior do not change.

## Extensibility (advanced)

`IReminderDeliveryThrottle` is a public SPI. If you need behavior outside what the built-in throttle provides — for example, a Redis-backed distributed limiter shared across multiple clusters — you can register your own:

```csharp
services.AddSingleton<IReminderDeliveryThrottle, MyCustomThrottle>();
```

Implementations must honor:

- Returning a `ReminderDeliveryLease` whose `Outcome` is either `Admitted` or `Skipped`.
- For an admitted lease, the caller will dispose it exactly once after the dispatch attempt; reclaim your permit there.
- For a skipped lease, return an appropriate `ReminderSkipReason` and a meaningful `TierName` so observability surfaces are populated.
- Treating `cancellationToken` as a hard cancel — the silo is shutting down or the reminder schedule changed; you must not hand out a permit after cancellation.
