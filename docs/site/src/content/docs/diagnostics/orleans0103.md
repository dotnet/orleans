---
title: "ORLEANS0103: Change grain state after the last external await"
description: Understand ORLEANS0103, which reports durable task code which changes grain state before an external await in the body's final code.
ms.date: 10/13/2026
ms.topic: reference
---

# ORLEANS0103: Change grain state after the last external await

| Property | Value |
| --- | --- |
| Severity | Warning |
| Category | Usage |
| Code fix | None |

## Cause

Code after a durable task body's last durable operation changes grain state, and the body then awaits an external operation.

## Rule description

The host records a body's result with the state changes which its final code makes, in one journal write. A change which an external await follows can commit in an earlier journal write than the body's result, for example when another operation on the grain writes the journal during the await. A run which ends before the result commits runs the final code again on replay, and applies the change twice.

For the replay model, see [Replay and determinism](../grains/durable-tasks.md#replay-and-determinism).

## How to fix violations

Change grain state after the last external await, or make the change in a `DurableTask.Once` effect.

## When to suppress

Suppress the diagnostic for changes which applying twice leaves unchanged, such as assigning a constant.
