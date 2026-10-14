---
title: "ORLEANS0109: Keep durable task bodies on their scheduler"
description: Understand ORLEANS0109, which reports durable task code which moves durable task work off the activation scheduler.
ms.date: 10/13/2026
ms.topic: reference
---

# ORLEANS0109: Keep durable task bodies on their scheduler

| Property | Value |
| --- | --- |
| Severity | Warning |
| Category | Usage |
| Code fix | None |

## Cause

A durable task body calls `ConfigureAwait(false)`, `Task.Run`, `Task.Factory.StartNew`, `Parallel` methods, `ThreadPool.QueueUserWorkItem`, or creates a `Thread` or a `Timer`.

## Rule description

A durable task body runs on its grain activation's scheduler, which orders its durable operations and its grain-state changes. Work which runs elsewhere schedules durable operations in an order which differs between runs, and changes grain state concurrently with the activation.

For the replay model, see [Replay and determinism](../grains/durable-tasks.md#replay-and-determinism).

## How to fix violations

Run concurrent work as durable children with `DurableTask.WhenAll` or `DurableTask.ScheduleAll`, and await other work on the body's scheduler.

## When to suppress

None in durable task bodies.
