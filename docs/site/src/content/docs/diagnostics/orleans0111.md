---
title: "ORLEANS0111: Wait with durable delays"
description: Understand ORLEANS0111, which reports Task.Delay or Thread.Sleep in durable task code, and fix it with DurableTask.Delay, DurableTask.DelayUntil, or durable timeouts.
ms.date: 10/15/2026
ms.topic: reference
---

# ORLEANS0111: Wait with durable delays

| Property | Value |
| --- | --- |
| Severity | Warning |
| Category | Usage |
| Code fix | None |

## Cause

A durable task body awaits `Task.Delay` or calls `Thread.Sleep` at a point from which it still reaches a durable operation:

:::code source="snippets/durable-tasks/Orleans0111.cs" id="orleans0111_violation" language="csharp":::

## Rule description

`Task.Delay` waits in the memory of the current run. When the activation deactivates, its silo restarts, or the run fails before the delay ends, the wait is lost, and the replay starts a new 14-day wait from the start of the run, so a trial whose run restarts every few days never expires. `Thread.Sleep` also blocks the activation's scheduler, so the grain processes nothing else while it sleeps.

A durable delay records its due time in the execution's logical time. The grain can deactivate while it waits, the runtime reactivates it when the delay is due, and a replay resumes the recorded delay instead of starting another.

For delays and timeouts, see [Combinators and handles](../grains/durable-tasks.md#combinators-and-handles).

## How to fix violations

**Wait for a duration with `DurableTask.Delay`:**

:::code source="snippets/durable-tasks/Orleans0111.cs" id="orleans0111_fix_delay" language="csharp":::

**Wait until a time with `DurableTask.DelayUntil`.** Compute the time from arguments, recorded results, or `DurableTask.Current.UtcNow`, so every replay computes the same due time:

:::code source="snippets/durable-tasks/Orleans0111.cs" id="orleans0111_fix_delay_until" language="csharp":::

**Bound work with `WaitAsync` or `WaitUntilAsync`** instead of racing it with `Task.Delay`. The timeout is a durable delay which counts from the first run. When it elapses first, the work receives a cancellation request, and a task which ends canceled throws `TimeoutException`:

:::code source="snippets/durable-tasks/Orleans0111.cs" id="orleans0111_fix_timeout" language="csharp":::

## When to suppress

Suppress the diagnostic for a short pause which a replay may repeat, such as a pause before a durable operation which polls a resource that is still starting. A backoff between attempts of an external call inside a `DurableTask.Run` delegate reaches no durable operation, so the rule doesn't report it.
