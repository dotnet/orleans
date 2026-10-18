---
title: "ORLEANS0119: Await, combine, or return the handles of scheduled children"
description: Understand ORLEANS0119, which reports the handle of an attached child which a durable task body schedules and never observes, and fix it by awaiting the handle or detaching the task.
ms.date: 10/17/2026
ms.topic: reference
---

# ORLEANS0119: Await, combine, or return the handles of scheduled children

| Property | Value |
| --- | --- |
| Severity | Info |
| Category | Usage |
| Code fix | None |

## Cause

A durable task body awaits `ScheduleAsync` on a definition which isn't `Detached()`, and discards the handle, or keeps it in a local which no code awaits, passes on, or returns:

:::code source="snippets/durable-tasks/Orleans0119.cs" id="orleans0119_violation" language="csharp":::

A local whose only uses read the handle's properties, such as `Id` or `TargetTaskId`, is reported too.

## Rule description

In a durable task body, `ScheduleAsync` starts an attached child and returns its handle once the child's host has accepted it. The execution owns the child: the child's result and failure reach only code which observes the handle, and when the execution's tree is deleted, after the execution is terminal, a child which is still running is canceled. In the example, a notification which fails goes unnoticed, and a notification which runs longer than the shipment is canceled with the shipment's tree.

For ownership and lifetimes, see [Structured concurrency](../grains/durable-tasks-structured-concurrency.md).

## How to fix violations

**Await the handle** before the body completes, or pass it to a combinator, such as `ScheduledTask.WhenAll`, or return it. The child runs concurrently with the work between the start and the await:

:::code source="snippets/durable-tasks/Orleans0119.cs" id="orleans0119_fix_await" language="csharp":::

**Detach work which outlives the execution.** A detached task is an independent root, which continues after its starter completes and which its starter's deletion doesn't reach. Name it with `WithId`, so other parties can address it:

:::code source="snippets/durable-tasks/Orleans0119.cs" id="orleans0119_fix_detach" language="csharp":::

## When to suppress

Suppress the diagnostic for a child which the execution deliberately leaves to the end of its tree's lifetime, such as one which another party observes through the handle's ID.
