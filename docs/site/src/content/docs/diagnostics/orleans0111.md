---
title: "ORLEANS0111: Wait with durable delays"
description: Understand ORLEANS0111, which reports durable task code which waits with a non-durable delay.
ms.date: 10/13/2026
ms.topic: reference
---

# ORLEANS0111: Wait with durable delays

| Property | Value |
| --- | --- |
| Severity | Warning |
| Category | Usage |
| Code fix | None |

## Cause

A durable task body calls `Task.Delay` or `Thread.Sleep` at a point from which it can still reach a durable operation.

## Rule description

A durable delay records its due time in logical time, so a replay resumes the remaining wait, and an elapsed delay returns immediately. Other waits restart for their whole duration on each run.

For the replay model, see [Replay and determinism](../grains/durable-tasks.md#replay-and-determinism).

## How to fix violations

Use `DurableTask.Delay` or `DurableTask.DelayUntil`.

## When to suppress

None in durable task bodies.
