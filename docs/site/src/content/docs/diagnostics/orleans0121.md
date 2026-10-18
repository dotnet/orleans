---
title: "ORLEANS0121: Run synchronous work with DurableTask.RunInline"
description: Understand ORLEANS0121, which reports a DurableTask.Run step whose delegate neither awaits nor observes its CancellationToken, and fix it with DurableTask.RunInline.
ms.date: 10/17/2026
ms.topic: reference
---

# ORLEANS0121: Run synchronous work with DurableTask.RunInline

| Property | Value |
| --- | --- |
| Severity | Warning |
| Category | Usage |
| Code fix | None |

## Cause

A durable task body awaits a `DurableTask.Run` step, optionally named or shielded, whose lambda doesn't await, doesn't return a task, and doesn't read the `CancellationToken` which it receives:

:::code source="snippets/durable-tasks/Orleans0121.cs" id="orleans0121_violation" language="csharp":::

Steps which a body schedules, posts, detaches, or passes to a combinator aren't reported.

## Rule description

A step runs its delegate as a child execution when the body awaits it, possibly in a later turn of the grain, with its own `DurableTask.Current` and cancellation scope, and commits its result in a journal write of its own. A delegate which completes synchronously uses none of that. `DurableTask.RunInline` runs the same work immediately, in the current work item, and records its result with the execution's next journal write, together with the state changes which it makes. Consecutive effects in one synchronous block commit in the same write, and no other turn of the grain runs between them.

For the two kinds of recorded work, see [Recorded effects](../grains/durable-tasks.md#recorded-effects-local-steps-and-calls-to-other-grains).

## How to fix violations

Run the work with `DurableTask.RunInline`, under the name which the step had:

:::code source="snippets/durable-tasks/Orleans0121.cs" id="orleans0121_fix" language="csharp":::

An effect and a step with the same name share an identifier, and a replay which finds a step recorded where the body now runs an effect diverges, which keeps the task pending until code which matches its record replays it. A deployment which changes a step of pending executions into an effect branches the change on `DurableTask.GetVersion`; see [Version durable tasks](../grains/durable-tasks-versioning.md).

## When to suppress

Suppress the diagnostic for a step which needs a child execution of its own, for example one which other code cancels separately.
