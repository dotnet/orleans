---
title: "ORLEANS0110: Run concurrent durable work through durable combinators"
description: Understand ORLEANS0110, which reports durable task code which combines durable work with a Task combinator.
ms.date: 10/13/2026
ms.topic: reference
---

# ORLEANS0110: Run concurrent durable work through durable combinators

| Property | Value |
| --- | --- |
| Severity | Warning |
| Category | Usage |
| Code fix | None |

## Cause

A durable task body passes durable operations to `Task.WhenAll`, `Task.WhenAny`, `Task.WaitAll`, or `Task.WaitAny`.

## Rule description

Concurrent durable operations which a Task combinator starts receive generated identifiers in the order in which they begin, which can differ on replay. Durable combinators identify each input by its index, and choose the first completion by the grain's recorded completion order.

For the replay model, see [Replay and determinism](../grains/durable-tasks.md#replay-and-determinism).

## How to fix violations

Use `DurableTask.WhenAll`, `DurableTask.WhenAny`, `DurableTask.ScheduleAll`, or `DurableTask.WhenEach`. For examples, see [Durable task recipes](../grains/durable-tasks-recipes.md).

## When to suppress

None in durable task bodies.
