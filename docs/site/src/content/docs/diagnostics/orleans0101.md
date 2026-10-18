---
title: "ORLEANS0101: Produce non-deterministic values in recorded effects"
description: Understand ORLEANS0101, which reports durable task code which produces clock, random, or environment values where a replay runs it again, and fix it with DurableTask.RunInline, logical time, or task-derived keys.
ms.date: 10/15/2026
ms.topic: reference
---

# ORLEANS0101: Produce non-deterministic values in recorded effects

| Property | Value |
| --- | --- |
| Severity | Warning |
| Category | Usage |
| Code fix | None |

## Cause

A durable task body produces a value which differs between runs at a point from which it still reaches a durable operation. The rule reports wall-clock time (`DateTime.Now`, `DateTime.UtcNow`, `DateTime.Today`, `DateTimeOffset.Now`, `DateTimeOffset.UtcNow`, `TimeProvider.GetUtcNow`, `TimeProvider.GetLocalNow`, `TimeProvider.GetTimestamp`, and `Stopwatch`), random values and identifiers (`Guid.NewGuid`, `Guid.CreateVersion7`, `Random`, and `RandomNumberGenerator`), and process values (`Environment.TickCount`, `Environment.MachineName`, `Environment.ProcessId`, `Environment.CurrentManagedThreadId`, `Environment.ProcessorCount`, and environment variables). Values which only reach a log call are exempt.

The following payment draws an idempotency key and computes a deadline from the system clock:

:::code source="snippets/durable-tasks/Orleans0101.cs" id="orleans0101_violation" language="csharp":::

## Rule description

A replay runs the body from the start, so it produces another value:

- **Another deadline.** The replay computes a deadline from the current clock. The recorded wait keeps its recorded due time, so the value which the body computed disagrees with the wait which it scheduled.
- **Another key.** A replay which reaches the `charge` step before it recorded a result runs the step with a new key, so the payment provider can't recognize the retry and charges again.
- **Divergence.** A value which reaches a durable call's arguments, a `WithId` name, or a branch makes the replay schedule a different operation than the one which the first run recorded, and the operation fails with a divergence error.

For the replay model, see [Writing replay-safe workflows](https://github.com/dotnet/orleans/blob/main/src/Orleans.DurableTasks.Abstractions/README.md#writing-replay-safe-workflows).

## How to fix violations

**Record the value with `DurableTask.RunInline`, and read logical time.** The effect runs once, and every replay returns the recorded value. `DurableTask.Current.UtcNow` is the execution's logical time, which the runtime records, so a replay computes the same deadline:

:::code source="snippets/durable-tasks/Orleans0101.cs" id="orleans0101_fix" language="csharp":::

**Derive identifiers from the task's identity.** The grain and `DurableTask.Current.Id` name the execution in every replay, so a key built from them is stable without a record:

:::code source="snippets/durable-tasks/Orleans0101.cs" id="orleans0101_fix_task_id" language="csharp":::

**Produce the value inside a step.** A `DurableTask.Run` delegate or a local step records its result, so values which it computes, including values which it passes to an external service, are recorded with it.

## When to suppress

Suppress the diagnostic for a value which never reaches a durable operation, its arguments, its name, a branch, or grain state, such as a timestamp which only measures the duration of the current run for a metric.
