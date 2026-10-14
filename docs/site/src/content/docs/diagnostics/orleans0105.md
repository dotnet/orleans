---
title: "ORLEANS0105: Await, schedule, or post durable task definitions"
description: Understand ORLEANS0105, which reports durable task code which creates a durable task definition which the code never observes.
ms.date: 10/13/2026
ms.topic: reference
---

# ORLEANS0105: Await, schedule, or post durable task definitions

| Property | Value |
| --- | --- |
| Severity | Warning |
| Category | Usage |
| Code fix | None |

## Cause

Code calls a method which returns `DurableTask` or `DurableTask<TResult>`, or configures one with `WithId`, `Detached`, or `Shielded`, and discards the result.

## Rule description

Calling a method which returns `DurableTask` creates a definition, and configuring it returns a configuration. The task runs when the code awaits the definition, schedules it with `ScheduleAsync`, or starts it with `Post`. A discarded definition never runs.

For the replay model, see [Replay and determinism](../grains/durable-tasks.md#replay-and-determinism).

## How to fix violations

Await the definition to run it and observe its result, schedule it with `ScheduleAsync` to observe it later through its handle, or post it with `Post` to start it with the caller's next durable write. A detached task, `.Detached().Post()`, runs independently of its starter. For the options, see [Starting and observing tasks](../grains/durable-tasks.md#starting-and-observing-tasks).

## When to suppress

None. A discarded definition has no effect.
