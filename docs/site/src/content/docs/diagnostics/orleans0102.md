---
title: "ORLEANS0102: Await external operations in steps or after the last durable operation"
description: Understand ORLEANS0102, which reports durable task code which awaits an external operation in replayed code.
ms.date: 10/13/2026
ms.topic: reference
---

# ORLEANS0102: Await external operations in steps or after the last durable operation

| Property | Value |
| --- | --- |
| Severity | Warning |
| Category | Usage |
| Code fix | None |

## Cause

A durable task body awaits an operation other than a durable operation, such as an HTTP request, an ordinary grain call, or a `Task`, at a point from which the body can still reach a durable operation.

## Rule description

A replay re-runs each await between durable operations. It repeats the operation's external effects, it can observe another result, and it can fail where the first run succeeded. A step records its result, so a replay of its parent returns that result without running the step again.

For the replay model, see [Replay and determinism](../grains/durable-tasks.md#replay-and-determinism).

## How to fix violations

Move the await into a step: a lambda passed to `DurableTask.Run`, or an `async DurableTask` method. Alternatively, move it after the body's last durable operation. For waits, use `DurableTask.Delay`, which [ORLEANS0111](orleans0111.md) describes.

## When to suppress

Suppress the diagnostic for awaits whose repetition is harmless and whose result doesn't affect the body's durable operations, such as a cache lookup.
