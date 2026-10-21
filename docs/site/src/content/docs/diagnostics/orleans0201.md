---
title: "ORLEANS0201: Produce non-deterministic values in recorded effects"
description: Understand ORLEANS0201, which reports durable task code which produces clock, random, or environment values where a replay runs it again, and fix it with DurableTask.RunInline, logical time, or task-derived keys.
ms.date: 10/15/2026
ms.topic: reference
---

# ORLEANS0201: Produce non-deterministic values in recorded effects

| Property | Value |
| --- | --- |
| Severity | Warning |
| Category | Usage |
| Code fix | None |

## Cause

A durable task body produces a value which differs between runs at a point from which it still reaches a durable operation. The rule reports wall-clock time (`DateTime.Now`, `DateTime.UtcNow`, `DateTime.Today`, `DateTimeOffset.Now`, `DateTimeOffset.UtcNow`, `TimeProvider.GetUtcNow`, `TimeProvider.GetLocalNow`, `TimeProvider.GetTimestamp`, and `Stopwatch`), random values and identifiers (`Guid.NewGuid`, `Guid.CreateVersion7`, `Random`, and `RandomNumberGenerator`), and process values (`Environment.TickCount`, `Environment.MachineName`, `Environment.ProcessId`, `Environment.CurrentManagedThreadId`, `Environment.ProcessorCount`, and environment variables). Values which only reach a log call are exempt.

The following payment draws an idempotency key and computes a deadline from the system clock:

:::code source="snippets/durable-tasks/Orleans0201.cs" id="orleans0201_violation" language="csharp":::

## Rule description

A replay runs the body from the start, so it produces another value:

- **Another deadline.** The replay computes a deadline from the current clock. The recorded wait keeps its recorded due time, so the value which the body computed disagrees with the wait which it scheduled.
- **Another key.** A replay which reaches the `charge` step before it recorded a result runs the step with a new key, so the payment provider can't recognize the retry and charges again.
- **Divergence.** A value which reaches a durable call's arguments, a `WithId` name, or a branch makes the replay schedule a different operation than the one which the first run recorded, and the operation fails with a divergence error.

For the replay model, see [Writing replay-safe workflows](../grains/durable-tasks.md#writing-replay-safe-workflows).

## How to fix violations

**Derive identifiers from the task's identity, and read logical time.** The grain, `DurableTask.Current.Id`, and `DurableTask.Current.Incarnation` name the run in every replay, so a key built from them is the same in every replay and attempt, including one which repeats a charge whose result didn't commit. The incarnation distinguishes a later run of the same task identifier, such as one which starts after the earlier run completed and its history was deleted, so that run charges under another key. `DurableTask.Current.UtcNow` is the execution's logical time, which the runtime records, so a replay computes the same deadline:

:::code source="snippets/durable-tasks/Orleans0201.cs" id="orleans0201_fix" language="csharp":::

**Record other values with `DurableTask.RunInline`.** The effect runs once, such as `DurableTask.RunInline("reference", () => Guid.NewGuid().ToString("N"))`, and every replay returns the recorded value. The record commits with the task's next journal write, together with the grain state which the body changes, so state and branches observe the same value in every run. A value which leaves the grain before that write, such as a key which a step passes to an external service, comes from the task's identity.

**Produce the value inside a step.** A `DurableTask.Run` delegate or a local step records its result, so values which it returns are recorded with it.

## When to suppress

Suppress the diagnostic for a value which never reaches a durable operation, its arguments, its name, a branch, or grain state, such as a timestamp which only measures the duration of the current run for a metric.
