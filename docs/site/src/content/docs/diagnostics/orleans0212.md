---
title: "ORLEANS0212: Filter cancellation handlers by IsTaskCancellationRequested"
description: Understand ORLEANS0212, which reports a handler which catches cancellation around durable operations without filtering by DurableTask.Current.IsTaskCancellationRequested, and fix it with a filter or a rethrow.
ms.date: 10/19/2026
ms.topic: reference
---

# ORLEANS0212: Filter cancellation handlers by IsTaskCancellationRequested

| Property | Value |
| --- | --- |
| Severity | Warning |
| Category | Usage |
| Code fix | None |

## Cause

A `try` block in a durable task body contains durable operations, and one of its `catch` clauses catches `OperationCanceledException`, a type it derives from, or a type which derives from it, has no filter which reads `DurableTaskContext.IsTaskCancellationRequested` or excludes `OperationCanceledException`, such as `when (e is not OperationCanceledException)`, and doesn't rethrow:

:::code source="snippets/durable-tasks/Orleans0212.cs" id="orleans0212_violation" language="csharp":::

## Rule description

A durable task observes two kinds of cancellation through `OperationCanceledException`:

- **Task cancellation** is durable: a caller requests it, such as with `CancelAsync` on the task's handle, and the host records it with the task, together with the point at which each execution first observes it. A replay observes it at that same point, and the body decides the task's outcome, for example by compensating.
- **Attempt cancellation** stops the current run of the body, for example when the activation stops, or the task is aborted or deleted. The attempt's later durable operations throw `DurableTaskAttemptCanceledException`, and its changes to journaled state commit with the grain's next journal write. A stopping activation's task continues in a new attempt.

An unfiltered handler treats both alike. When the host cancels the attempt during the hold period, the handler in the example compensates for a booking which nobody canceled: its state change commits, and an external call which it makes, such as releasing the room in a booking system, takes effect, while the next attempt replays the task and confirms the booking.

`DurableTask.Current.IsTaskCancellationRequested` is `true` only for task cancellation, in the canceled attempt and in every later attempt.

For compensation, see [Compensating canceled work](../grains/durable-tasks.md#compensating-canceled-work).

## How to fix violations

**Filter the handler.** The handler then runs for task cancellation, and the exception of attempt cancellation ends the run. Shield compensating children with `Shielded()`, so they run although the task's cancellation was requested:

:::code source="snippets/durable-tasks/Orleans0212.cs" id="orleans0212_fix_filter" language="csharp":::

**Rethrow after compensating.** A handler which ends with `throw;` lets every cancellation end the run, and the task ends canceled after task cancellation:

:::code source="snippets/durable-tasks/Orleans0212.cs" id="orleans0212_fix_rethrow" language="csharp":::

## When to suppress

Suppress the diagnostic when the handler's code is correct for both task cancellation and attempt cancellation, for example a handler which only records a log entry through a replay-safe logger.
