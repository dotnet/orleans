---
title: "ORLEANS0219: Await, combine, or return the handles of scheduled children"
description: Understand ORLEANS0219, which reports the handle of a scheduled child which a durable task body never observes, and fix it by awaiting the handle, combining it, or detaching the work.
ms.date: 10/19/2026
ms.topic: reference
---

# ORLEANS0219: Await, combine, or return the handles of scheduled children

| Property | Value |
| --- | --- |
| Severity | Info |
| Category | Usage |
| Code fix | None |

## Cause

A durable task body schedules an attached child with `ScheduleAsync` and discards the returned handle, or holds it in a local whose only uses read its properties, such as `Id`:

:::code source="snippets/durable-tasks/Orleans0219.cs" id="orleans0219_violation" language="csharp":::

## Rule description

In a durable task body, `ScheduleAsync` starts an attached child and returns its handle once the child's host has accepted and stored the child. The execution owns the child: the execution's cancellation reaches it, and deleting the execution's tree, once the execution is terminal, cancels a child which is still running. The child's result and failure reach only code which observes the handle.

In the example, the body completes while the notice can still be running. A notice which fails leaves the order dispatched with no record of the failure in the workflow, and a notice which is still running when the execution's tree is deleted is canceled.

For attached and detached starts, see [Starting and observing tasks](https://github.com/dotnet/orleans/blob/main/src/Orleans.DurableTasks.Abstractions/README.md#starting-and-observing-tasks).

## How to fix violations

**Await the handle.** The child runs while the body continues, and the body observes its outcome before it completes:

:::code source="snippets/durable-tasks/Orleans0219.cs" id="orleans0219_fix_await" language="csharp":::

**Pass the handle to a combinator.** `ScheduledTask.WhenAll` waits for every handle and throws the first failure in input order:

:::code source="snippets/durable-tasks/Orleans0219.cs" id="orleans0219_fix_combinator" language="csharp":::

**Return the handle.** A body which returns the handle passes the observation to its caller.

**Detach work which outlives the execution.** `Detached()` starts an independent root task, which continues after the execution completes and after its tree is deleted:

:::code source="snippets/durable-tasks/Orleans0219.cs" id="orleans0219_fix_detached" language="csharp":::

## When to suppress

Suppress the diagnostic for a child whose outcome the workflow ignores, and which may stop when the execution's tree is deleted, such as a best-effort cache warm-up.
