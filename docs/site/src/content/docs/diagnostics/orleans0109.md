---
title: "ORLEANS0109: Keep durable task bodies on their scheduler"
description: Understand ORLEANS0109, which reports durable task code which starts threads, timers, or thread-pool work, and fix it with steps or services which the body awaits.
ms.date: 10/15/2026
ms.topic: reference
---

# ORLEANS0109: Keep durable task bodies on their scheduler

| Property | Value |
| --- | --- |
| Severity | Warning |
| Category | Usage |
| Code fix | None |

## Cause

A durable task body, a `DurableTask.Run` delegate, or a `DurableTask.RunInline` effect starts work on another scheduler: `Task.Run`, `TaskFactory.StartNew`, `Parallel` methods, `ThreadPool.QueueUserWorkItem`, a new `Thread`, or a new `System.Threading.Timer`, `System.Timers.Timer`, or `PeriodicTimer`:

:::code source="snippets/durable-tasks/Orleans0109.cs" id="orleans0109_violation" language="csharp":::

## Rule description

A durable task body runs on its grain activation's scheduler, which runs one piece of grain code at a time. The scheduler orders the body's durable operations, so each run creates them in the same order, and it keeps the body's state changes from racing with the grain's other work. `Task.Run` executes its delegate on the thread pool; a normal `await Task.Run(...)` captures the activation's scheduler and resumes the body there. The off-scheduler delegate carries the risks:

- Code inside the delegate runs outside the activation. `DurableTask.RunInline` and `Post` throw `InvalidOperationException` there, because they stage their records with the activation's state changes.
- Grain state which the work changes races with the grain's methods and with other executions, and its changes commit with whichever journal write follows.
- Threads and timers outlive the run which starts them, and a replay starts them again.

ORLEANS0014 reports `ConfigureAwait(false)`, which also moves continuations off the activation.

## How to fix violations

**Record the work in an effect.** A `DurableTask.RunInline` effect runs synchronous work on the activation's scheduler, and its result is recorded, so a replay returns it without computing it again:

:::code source="snippets/durable-tasks/Orleans0109.cs" id="orleans0109_fix_inline" language="csharp":::

**Await a service which runs the work.** For CPU-intensive work, call a service or a stateless worker grain from a step. The service uses its own threads, and the step's continuation returns to the activation:

:::code source="snippets/durable-tasks/Orleans0109.cs" id="orleans0109_fix_service" language="csharp":::

**Replace timers with durable delays.** `DurableTask.Delay` and `DurableTask.DelayUntil` resume the task when they're due, including after a deactivation. Periodic work runs as a loop of named delays, or as a detached task which each iteration starts.

## When to suppress

Suppress the diagnostic for work which touches no grain state, schedules no durable operations, and which the body awaits to completion, such as a pure computation, when running it on the thread pool is required.
