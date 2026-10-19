---
title: "ORLEANS0221: Run synchronous work with DurableTask.RunInline"
description: Understand ORLEANS0221, which reports DurableTask.Run steps which run only synchronous work, and fix them with DurableTask.RunInline effects, gated by DurableTask.GetVersion while executions are pending.
ms.date: 10/19/2026
ms.topic: reference
---

# ORLEANS0221: Run synchronous work with DurableTask.RunInline

| Property | Value |
| --- | --- |
| Severity | Info |
| Category | Usage |
| Code fix | None |

## Cause

A durable task body awaits a `DurableTask.Run` step where it defines it, optionally named or shielded, and the step's lambda awaits nothing, returns no task, and doesn't read the `CancellationToken` which the step receives:

:::code source="snippets/durable-tasks/Orleans0221.cs" id="orleans0221_violation" language="csharp":::

## Rule description

`DurableTask.Run` runs its delegate as a child execution, with its own `DurableTask.Current` and cancellation scope, when the body awaits the step, possibly in a later turn. The step commits its result in a journal write of its own.

`DurableTask.RunInline` runs synchronous work in the current work item and records its outcome with the execution's next journal write, together with the state changes which the effect makes. Consecutive effects commit in the same journal write. In the fix, the status change and its record commit with the execution's next journal write instead of a write of their own.

A step and an effect at the same call site use the same identifier, and the execution records which kind ran. A replay which finds a step recorded where the body now runs an effect diverges, so a deployment with pending executions branches the change on `DurableTask.GetVersion`.

For effects and steps, see [Recorded effects](https://github.com/dotnet/orleans/blob/main/src/Orleans.DurableTasks.Abstractions/README.md#recorded-effects).

## How to fix violations

**Run the work as an effect.** Pass the delegate to `DurableTask.RunInline` with the step's name:

:::code source="snippets/durable-tasks/Orleans0221.cs" id="orleans0221_fix" language="csharp":::

**Branch the change on a version.** While executions which recorded the step are pending, the body runs the effect for new executions and keeps the step for executions which recorded it:

:::code source="snippets/durable-tasks/Orleans0221.cs" id="orleans0221_fix_versioned" language="csharp":::

## When to suppress

Suppress the diagnostic for a step which needs a child execution of its own, for example for its own cancellation scope, and in the branch which executions that recorded the step replay.
