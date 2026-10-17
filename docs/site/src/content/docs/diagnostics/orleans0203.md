---
title: "ORLEANS0203: Change persistent grain state after the last external await"
description: Understand ORLEANS0203, which reports a persistent state change which a step or a body's tail makes before an external await, and fix it by reordering, recording the change, or keeping the state in journaled state.
ms.date: 10/16/2026
ms.topic: reference
---

# ORLEANS0203: Change persistent grain state after the last external await

| Property | Value |
| --- | --- |
| Severity | Warning |
| Category | Usage |
| Code fix | None |

## Cause

Code which runs once, such as a local step or the body's synchronous tail, changes persistent grain state and then awaits an external operation before it returns. A change is an assignment, an increment, or a decrement of `IPersistentState<TState>.State`, or a call to `IPersistentState<TState>.WriteStateAsync` or `ClearStateAsync`. The following step counts a notice, then sends it:

:::code source="snippets/durable-tasks/Orleans0203.cs" id="orleans0203_violation" language="csharp":::

## Rule description

A step's result is staged in the work item which runs its final synchronous code, so the state changes which that code makes commit in the same journal write as the result. A change to persistent state which precedes an external await is staged earlier, without a record of its own. While the step awaits, other work on the grain, such as another workflow's step or an ordinary grain method, can request a journal write, and that write commits the change without the step's result.

When the run then ends before the result commits, for example because the activation deactivates, the next run runs the step again: it counts the notice a second time and sends the email again. The task's record shows one notice, and the grain's state shows two.

Journaled state, such as `IDurableValue<T>` and the durable collections, stages each change with its record, so a step which runs again returns the recorded outcome without making the change again, and the rule doesn't apply to it. For the commit model, see [Replay-safety rules](../grains/durable-tasks-replay-rules.md).

## How to fix violations

**Keep the state in journaled state.** A change to an `IDurableValue<T>` or a durable collection commits with its record:

:::code source="snippets/durable-tasks/Orleans0203.cs" id="orleans0203_journaled" language="csharp":::

**Change state after the last external await.** The change is then part of the step's final synchronous code, and commits with its result:

:::code source="snippets/durable-tasks/Orleans0203.cs" id="orleans0203_fix_reorder" language="csharp":::

**Record each change separately.** Split the work into a step for the external call and `DurableTask.RunInline` effects for the state changes. Each effect commits its change with its own record:

:::code source="snippets/durable-tasks/Orleans0203.cs" id="orleans0203_fix_split" language="csharp":::

## When to suppress

Suppress the diagnostic for a change which is idempotent, such as setting a value which every run sets to the same result, when its commit before the external call is acceptable.
