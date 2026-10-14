---
title: "ORLEANS0105: Await or schedule durable task definitions"
description: Understand ORLEANS0105, which reports durable task code which creates a durable task definition which the code never observes.
ms.date: 10/13/2026
ms.topic: reference
---

# ORLEANS0105: Await or schedule durable task definitions

| Property | Value |
| --- | --- |
| Severity | Warning |
| Category | Usage |
| Code fix | None |

## Cause

Code calls a method which returns `DurableTask` or `DurableTask<TResult>` and discards the result.

## Rule description

Calling a method which returns `DurableTask` creates a definition. The definition runs when the code awaits it, schedules it with `ScheduleAsync`, or starts it with `DetachAsync`. A discarded definition never runs.

For the replay model, see [Replay and determinism](../grains/durable-tasks.md#replay-and-determinism).

## How to fix violations

Await the definition to run it as a child, schedule it to observe it later, or detach it to start a task which runs independently. For examples, see [Detach fire-and-forget side effects](../grains/durable-tasks-recipes.md#detach-fire-and-forget-side-effects).

## When to suppress

None. A discarded definition has no effect.
