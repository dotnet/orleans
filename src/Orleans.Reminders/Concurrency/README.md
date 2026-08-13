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
            .MaxConcurrent(50, ThrottleBlockMode.Wait)
            .PermitsPerSecond(200, 200, ThrottleBlockMode.Wait)));
```

That configuration says: on this silo, never deliver more than 50 reminder ticks at once, never exceed a sustained rate of 200 ticks/second (with an explicit burst capacity of 200), and if a tick can't be admitted right away, **wait** for capacity rather than skipping the tick.

A configuration that also wants to honor silo overload and ramp up gradually after silo restart:

```csharp
.AddReminderConcurrencyControl(c => c
    .PerSilo(t => t
        .MaxConcurrent(100, ThrottleBlockMode.Wait)
        .PermitsPerSecond(500, 500, ThrottleBlockMode.Wait)

        // While the silo's IOverloadDetector reports overload (CPU / memory pressure
        // exceeding the configured load-shedding thresholds), wait up to 30 seconds for
        // the pressure to clear, then skip the tick if it still hasn't.
        .RespectOverload(ThrottleBlockMode.WaitUpTo(TimeSpan.FromSeconds(30)))

        // After silo startup, only admit 10 dispatches concurrently for the first interval,
        // doubling every 10 seconds until reaching MaxConcurrent. During ramp-up, callers
        // that exceed the dynamic capacity wait for it to open.
        .SlowStart(initialCapacity: 10, interval: TimeSpan.FromSeconds(10), onCapacityExceeded: ThrottleBlockMode.Wait)));
```

## Reasoning about the configuration

The feature is intentionally minimal: there is one tier in Phase 1 (the **Per-Silo** tier). Each piece of behavior is independently optional and each one is a deliberate decision we declined to make for you because the wrong default is too costly. Read through the decisions below before turning the feature on in production.

### 1. Do you actually need it?

If your downstreams comfortably absorb anything reminders can throw at them — for example, all reminder work is in-process state mutation on the same grain, or the grain writes to a backend that auto-scales without complaint — leave the feature off. Concurrency control adds latency variability (some ticks now wait), and that's worse than nothing for workloads that aren't downstream-bound.

Turn it on when:

- A failure investigation traces back to a thundering herd of reminders saturating a backend.
- Capacity planning shows the worst-case "all reminders due at once" scenario exceeds what a dependency can sustain.
- Reminder grains call external APIs with their own rate limits and you want to absorb the rate-limiter on the Orleans side instead of getting `HTTP 429` storms.
- You're seeing reminder ticks contributing to silo CPU/memory pressure during recovery from a downstream outage or after a membership change.

### 2. Bound concurrency, bound rate, or both?

- **`MaxConcurrent(N)`** caps the number of in-flight `ReceiveReminder` calls. This is the right knob when each tick holds an expensive resource (a database connection, a downstream HTTP call) and you know how many of those resources are available.
- **`PermitsPerSecond(R)`** caps the sustained dispatch rate. This is the right knob when the downstream complaint is "too many requests per unit time" (a rate-limited API, a database that handles concurrency well but writes per second poorly).
- **Both at once** is allowed and composes by AND: a dispatch must obtain both a concurrency permit and a rate token to be admitted. Use this when both constraints apply.

You must specify at least one of `MaxConcurrent`, `PermitsPerSecond`, or `RespectOverload`. A configuration with none of them is rejected at startup rather than silently turning into a no-op.

`PermitsPerSecond` requires an explicit `burstSize` and `ThrottleBlockMode`. That makes the token-bucket shape and the backpressure behavior a deliberate choice at the call site instead of relying on hidden defaults.

### 3. What happens when the limit binds?

Choose the limiter behavior by passing one of these block modes when you configure each limiter:

| Block mode | Behavior when no permit is available | Trade-off |
|---|---|---|
| `ThrottleBlockMode.Wait` (default) | Wait indefinitely for a permit unless an earlier composed gate established a shared `WaitUpTo` deadline. | When all gates use `Wait`, no ticks are dropped, but tardiness is unbounded. The grain may see a tick arrive much later than scheduled. |
| `ThrottleBlockMode.WaitUpTo(timeout)` | Wait up to `timeout`, then skip the tick if no permit became available. | Bounded tardiness, bounded skip rate. Skips are classified as `ReminderSkipReason.AcquireTimeout` for observability. |
| `ThrottleBlockMode.SkipImmediately` | Skip the tick if no permit is available right now. | Maximum downstream protection; minimum delivery guarantee. Best when "missing a tick" is materially better than "exceeding the limit". |

All sequential waiting phases share a single `WaitUpTo` budget: a `WaitUpTo(500ms)` cap means the total wait across the composed gates is at most 500 ms, not 500 ms per gate. Once established, that deadline also bounds later gates configured with `Wait`.

**Reasoning prompts:**

- Is it OK for a grain to *never* receive a particular tick? If the tick represents a periodic check that will repeat soon anyway, dropping one tick costs you nothing. If the tick represents real work that must happen, dropping it is a bug.
- How long can the downstream tolerate the wait? `Wait` is fine when downstream backpressure is the limit you're really respecting (i.e. the wait is the *point*). It's not fine when something else times out at the grain layer waiting on the tick.

### 4. Respect silo overload? (optional)

Orleans has a cluster-wide `IOverloadDetector` that reports when the silo is under CPU or memory pressure (using the same load-shedding thresholds that throttle the silo gateway and influence placement). The gateway, placement directors, and Durable Jobs all honor this signal. Reminders can too — but only if you opt in.

```csharp
.RespectOverload(onOverload: ThrottleBlockMode.WaitUpTo(TimeSpan.FromSeconds(30)))
```

The block mode is **required** at configuration time — it is not defaulted — because overload behavior is a deliberate design choice, not a guess we should make for you:

| Block mode | Behavior while the silo is overloaded |
|---|---|
| `ThrottleBlockMode.Wait` | Poll the overload signal (default every 1 s) until it clears. Unbounded delay; never drops a tick. |
| `ThrottleBlockMode.WaitUpTo(timeout)` | Wait up to `timeout`, then skip the tick with `ReminderSkipReason.SiloOverloaded`. Bounded delay; some ticks may drop. |
| `ThrottleBlockMode.SkipImmediately` | Skip the tick immediately if the silo is overloaded. Most protective; most ticks dropped during overload. |

The overload check runs **before** any concurrency permit or rate token is consumed — protecting an overloaded silo never costs you a permit.

`RespectOverload` is itself a valid sole tier; you may use it without configuring `MaxConcurrent` or `PermitsPerSecond` if your only goal is to back off when the silo is overloaded.

### 5. Slow-start ramp-up after silo restart? (optional)

After a silo starts (or assumes responsibility for a new range of reminders after a membership change), thousands of reminders can become due "now". Even with a configured `MaxConcurrent`, the cold-start phase is where the thundering herd bites hardest — caches are cold, connection pools are empty, JITs aren't warm, the thread pool is undersized.

Slow-start mirrors the equivalent behavior in `DurableJobsOptions` (`SlowStartInitialConcurrency`, `SlowStartInterval`). The configured `MaxConcurrent` becomes a *target*; capacity starts low and doubles every `interval` until it reaches the target.

```csharp
.MaxConcurrent(100)
.SlowStart(
    initialCapacity: 10,
    interval: TimeSpan.FromSeconds(10),
    onCapacityExceeded: ThrottleBlockMode.Wait)
```

That configuration starts at concurrency 10, doubles to 20 after 10 s, to 40 after 20 s, to 80 after 30 s, and caps at 100 after 40 s.

The block mode is **required** at configuration time so that the behavior during ramp-up is an explicit decision, not a silent default:

| Block mode | Behavior while the ramping capacity is exhausted |
|---|---|
| `ThrottleBlockMode.Wait` | Wait for the ramp-up to release more capacity. No ticks dropped during warm-up. |
| `ThrottleBlockMode.WaitUpTo(timeout)` | Wait up to `timeout`, then skip with `ReminderSkipReason.SlowStartLimited`. |
| `ThrottleBlockMode.SkipImmediately` | Skip the tick immediately if no current capacity is available. Most protective; most ticks dropped during warm-up. |

Slow-start requires `MaxConcurrent` to be configured (it ramps *up to* that target). `initialCapacity` cannot exceed `MaxConcurrent`. Both rules are enforced at startup.

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
| `orleans-reminders-throttle-active-leases` | ObservableUpDownCounter | `{lease}` | `orleans.reminder.throttle.tier` | Currently-held leases per tier. Saturates near `MaxConcurrent` when the concurrency cap is binding. |
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

- `ReminderEvents.TickSkipped` — emitted when a throttle skipped a tick. Fields: `GrainId`, `ReminderName`, `Status`, `Reason` (`ReminderSkipReason`), `TierName`, `WaitedFor`, `SiloAddress`. Possible `Reason` values include `LocalLimiterFull`, `AcquireTimeout`, `SiloOverloaded`, `SlowStartLimited`, and (reserved for cluster tiers in later phases) `ClusterLimiterFull`, `CoordinatorUnreachableFailClosed`.

The existing `TickFiring`, `TickCompleted`, and `TickFailed` events continue to fire for non-skipped dispatches.

### Logs

The feature emits exactly two log lines on the happy path:

- **Information** at startup: one line per configured tier, with effective values (`MaxConcurrent`, `PermitsPerSecond`, `BurstSize`, `BlockMode`). This lets you confirm at silo start that the configuration loaded as expected.
- *(No per-tick logs.)* Per-tick noise lives in metrics and traces.

## Pit-of-success traps we explicitly closed

These are decisions we deliberately constrained at the API level so you cannot misconfigure them quietly:

- **You cannot construct a `WaitWithTimeout` block mode without a timeout.** The factory `ThrottleBlockMode.WaitUpTo(timeout)` requires a positive `TimeSpan`. Zero/negative values throw at configuration time.
- **You cannot install the feature with no tiers configured.** `AddReminderConcurrencyControl(b => { })` fails startup with a clear message. Silent no-ops are a footgun: you'd think you had protection and not have it.
- **You cannot configure a tier with no behavior.** At least one of `MaxConcurrent`, `PermitsPerSecond`, or `RespectOverload` must be set; `ThrottleConfig` validates at build time.
- **You cannot configure `BurstSize` without `PermitsPerSecond`.** Burst is a property of a token bucket; the validator rejects the combination.
- **You cannot configure `SlowStart` without `MaxConcurrent`.** Slow-start ramps *up to* the target capacity; without a target there's nothing to ramp to.
- **You cannot configure a `SlowStart.InitialCapacity` greater than `MaxConcurrent`.** The validator rejects this combination at build time.
- **You cannot configure `RespectOverload` or `SlowStart` without explicitly choosing their block mode.** Both behaviors involve trade-offs (lose protection vs lose delivery) that the user must make consciously; no default is provided.
- **You cannot configure `RespectOverload` and forget to wire `IOverloadDetector`.** The throttle factory throws at silo startup if `IOverloadDetector` is missing from DI (it is registered by default in any silo).
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
