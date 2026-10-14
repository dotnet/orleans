---
title: "ORLEANS0112: Filter cancellation handlers by IsCancellationRequested"
description: Understand ORLEANS0112, which reports durable task code which catches cancellation without filtering by IsCancellationRequested.
ms.date: 10/13/2026
ms.topic: reference
---

# ORLEANS0112: Filter cancellation handlers by IsCancellationRequested

| Property | Value |
| --- | --- |
| Severity | Info |
| Category | Usage |
| Code fix | None |

## Cause

A durable task body catches `OperationCanceledException` without a `when` filter which reads `DurableTask.Current.IsCancellationRequested`.

## Rule description

A host drains and aborts a run, for example when its activation deactivates, through the same `OperationCanceledException` as durable cancellation. A handler which filters by `IsCancellationRequested` compensates for durable cancellation, and lets an aborted run stop so that the next activation resumes it.

For the replay model, see [Replay and determinism](../grains/durable-tasks.md#replay-and-determinism).

## How to fix violations

Add `when (DurableTask.Current.IsCancellationRequested)` to the handler. For an example, see [Compensate inline when the workflow is canceled](../grains/durable-tasks-recipes.md#compensate-inline-when-the-workflow-is-canceled).

## When to suppress

Suppress the diagnostic for handlers whose work is correct in both cases.
