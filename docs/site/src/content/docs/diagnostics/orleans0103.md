---
title: "ORLEANS0103: Change grain state after the last external await"
description: Understand ORLEANS0103, which reports a state change which a step or a body's tail makes before an external await, and fix it by reordering or recording the change.
ms.date: 10/15/2026
ms.topic: reference
---

# ORLEANS0103: Change grain state after the last external await

| Property | Value |
| --- | --- |
| Severity | Warning |
| Category | Usage |
| Code fix | None |

## Cause

Code which runs once, such as a local step or the body's synchronous tail, changes grain state and then awaits an external operation before it returns. A change is an assignment, an increment, or a decrement of a grain state member, such as `IDurableValue<T>.Value` or a dictionary or list element, or a call which changes a collection, such as `Add`, `AddRange`, `Remove`, `Clear`, `Insert`, `RemoveAt`, `Enqueue`, `Dequeue`, `TryDequeue`, `TryAdd`, `UnionWith`, `IntersectWith`, `ExceptWith`, `SymmetricExceptWith`, `IDurableLog<T>.Append` and `TrimBefore`, the `TrySet` methods of `IDurableTaskCompletionSource<T>`, and `IPersistentState<TState>.WriteStateAsync` and `ClearStateAsync`. The following step counts a notice, then sends it:

:::code source="snippets/durable-tasks/Orleans0103.cs" id="orleans0103_violation" language="csharp":::

## Rule description

A step's result is staged in the work item which runs its final synchronous code, so the state changes which that code makes commit in the same journal write as the result. A change which precedes an external await is staged earlier. While the step awaits, other work on the grain, such as another workflow's step or an ordinary grain method, can request a journal write, and that write commits the change without the step's result.

When the run then ends before the result commits, for example because the activation deactivates, the next run runs the step again: it counts the notice a second time and sends the email again. The task's record shows one notice, and the grain's state shows two.

For the commit model, see [Replay-safety rules](../grains/durable-tasks-replay-rules.md).

## How to fix violations

**Change state after the last external await.** The change is then part of the step's final synchronous code, and commits with its result:

:::code source="snippets/durable-tasks/Orleans0103.cs" id="orleans0103_fix_reorder" language="csharp":::

**Record each change separately.** Split the work into a step for the external call and `DurableTask.Once` effects for the state changes. Each effect commits its change with its own record:

:::code source="snippets/durable-tasks/Orleans0103.cs" id="orleans0103_fix_split" language="csharp":::

## When to suppress

Suppress the diagnostic for a change which is idempotent, such as setting a value which every run sets to the same result, when its commit before the external call is acceptable.
