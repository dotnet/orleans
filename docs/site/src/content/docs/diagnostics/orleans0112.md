---
title: "ORLEANS0112: Filter cancellation handlers by IsCancellationRequested"
description: Understand ORLEANS0112, which reports a handler which catches cancellation around durable operations without filtering by DurableTask.Current.IsCancellationRequested, and fix it with a filter or a rethrow.
ms.date: 10/15/2026
ms.topic: reference
---

# ORLEANS0112: Filter cancellation handlers by IsCancellationRequested

| Property | Value |
| --- | --- |
| Severity | Info |
| Category | Usage |
| Code fix | None |

## Cause

A `try` block in a durable task body contains durable operations, and one of its `catch` clauses catches `OperationCanceledException`, a type it derives from, or a type which derives from it, has no filter which reads `IsCancellationRequested`, and doesn't rethrow:

:::code source="snippets/durable-tasks/Orleans0112.cs" id="orleans0112_violation" language="csharp":::

## Rule description

A durable task observes two kinds of cancellation through `OperationCanceledException`:

- **Durable cancellation** is a request which a caller commits, such as `CancelAsync` on the task's handle. It is recorded with the task, every replay observes it, and the body decides the task's outcome, for example by compensating.
- **A host stop** ends the current run without recording anything, for example when the activation deactivates, its silo shuts down, or an abort or deletion ends the task. The next activation replays the task.

An unfiltered handler treats both alike. When the host stops the run during the hold period, the handler in the example marks the booking as cancelled, a change which commits with the grain's next journal write, although no caller canceled it. The replay then confirms the booking whose state reports it as cancelled.

`DurableTask.Current.IsCancellationRequested` is `true` only for durable cancellation, in the canceled run and in every replay.

For compensation, see [Compensate inline when the workflow is canceled](../grains/durable-tasks-recipes.md#compensate-inline-when-the-workflow-is-canceled).

## How to fix violations

**Filter the handler.** The handler then runs for durable cancellation, and the exception of a host stop ends the run. Shield compensating children with `Shielded()`, so they run although the task's cancellation was requested:

:::code source="snippets/durable-tasks/Orleans0112.cs" id="orleans0112_fix_filter" language="csharp":::

**Rethrow after compensating.** A handler which ends with `throw;` lets every cancellation end the run, and the task ends canceled after durable cancellation:

:::code source="snippets/durable-tasks/Orleans0112.cs" id="orleans0112_fix_rethrow" language="csharp":::

## When to suppress

Suppress the diagnostic when the handler's code is correct for both kinds of cancellation, for example a handler which only records a log entry through a replay-safe logger.
