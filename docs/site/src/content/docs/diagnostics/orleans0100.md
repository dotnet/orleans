---
title: "ORLEANS0100: Access grain state in recorded effects or after the last durable operation"
description: Understand ORLEANS0100, which reports durable task code which accesses grain state in replayed code.
ms.date: 10/13/2026
ms.topic: reference
---

# ORLEANS0100: Access grain state in recorded effects or after the last durable operation

| Property | Value |
| --- | --- |
| Severity | Warning |
| Category | Usage |
| Code fix | None |

## Cause

Code in a durable task body reads or writes grain state, such as an `IDurableValue<T>`, `IDurableDictionary<TKey, TValue>`, or `IPersistentState<TState>` member, at a point from which the body can still reach a durable operation.

## Rule description

Grain state always holds the latest committed changes. When a body replays from the start, the code before its last durable operation runs again: it reads state which later operations wrote, and it applies its own writes again. A value read from that state can change which durable operations the replay schedules, which the host reports as a divergence.

Three places access state once. A `DurableTask.Once` effect runs once and records its result. A local step, an `async DurableTask` method on the same grain, commits its state changes with its recorded result. The code after the body's last durable operation commits with the body's result.

For the replay model, see [Replay and determinism](../grains/durable-tasks.md#replay-and-determinism).

## How to fix violations

Move the state access into a `DurableTask.Once` effect or a local step, and return the values which the body needs from it. Alternatively, move the access after the body's last durable operation.

## When to suppress

Suppress the diagnostic for state which the body reads to make decisions which don't affect its durable operations, such as a read whose value only reaches a log message.
