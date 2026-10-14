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

For the replay model, see [Writing replay-safe workflows](https://github.com/dotnet/orleans/blob/main/src/Orleans.DurableTasks.Abstractions/README.md#writing-replay-safe-workflows).

## How to fix violations

Add `when (DurableTask.Current.IsCancellationRequested)` to the handler. For an example, see [Compensating canceled work](https://github.com/dotnet/orleans/blob/main/src/Orleans.DurableTasks.Abstractions/README.md#compensating-canceled-work).

## When to suppress

Suppress the diagnostic for handlers whose work is correct in both cases.
