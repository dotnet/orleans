---
title: "ORLEANS0104: Return the outputs of a DurableTask.Once effect"
description: Understand ORLEANS0104, which reports durable task code which assigns a captured variable in a `DurableTask.Once` effect.
ms.date: 10/13/2026
ms.topic: reference
---

# ORLEANS0104: Return the outputs of a DurableTask.Once effect

| Property | Value |
| --- | --- |
| Severity | Warning |
| Category | Usage |
| Code fix | None |

## Cause

A delegate passed to `DurableTask.Once` assigns a local variable, parameter, or field of the enclosing body.

## Rule description

A replay returns an effect's recorded result without running the effect. The assignment therefore happens only in the run which runs the effect, and the replayed body observes the variable's previous value.

For the replay model, see [Replay and determinism](../grains/durable-tasks.md#replay-and-determinism).

## How to fix violations

Return the value from the effect, for example as a tuple, and assign the variable from the effect's result.

## When to suppress

Suppress the diagnostic for variables which only the effect reads, such as a counter in a test.
