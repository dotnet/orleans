---
title: "ORLEANS0228: Detach durable grain calls"
description: Understand ORLEANS0228, which reports Detached on a step, an async DurableTask method, or a combinator, which can't run as a root task, and fix it by detaching a durable grain call or starting the work attached.
ms.date: 10/19/2026
ms.topic: reference
---

# ORLEANS0228: Detach durable grain calls

| Property | Value |
| --- | --- |
| Severity | Error |
| Category | Usage |
| Code fix | None |

## Cause

Durable task code calls `Detached()` or `Detached(rootId)` on a definition which runs as part of the execution which starts it: a `DurableTask.Run` step, an `async DurableTask` method, or a combinator such as `DurableTask.WhenAll`:

:::code source="snippets/durable-tasks/Orleans0228.cs" id="orleans0228_violation" language="csharp":::

## Rule description

A detached task is an independent root task. Its host stores the task and runs it on its own, after the starting execution completes and after the starting execution's tree is deleted, so its definition is one which a host starts as a root task, such as a durable grain call, which the grain's host stores as a request.

A step, an `async DurableTask` method, and a combinator are code of the starting execution: a replay of that execution re-creates them, and nothing else can. Starting one detached throws `InvalidOperationException`, before the start takes an identifier of the execution.

For attached and detached starts, see [Starting and observing tasks](https://github.com/dotnet/orleans/blob/main/src/Orleans.DurableTasks.Abstractions/README.md#starting-and-observing-tasks).

## How to fix violations

**Detach a durable grain call.** Move the work into a durable method of a grain, and detach the call. The grain runs it as a root task:

:::code source="snippets/durable-tasks/Orleans0228.cs" id="orleans0228_fix_grain" language="csharp":::

**Start the work attached.** When the execution can wait for the work, await it, or schedule it and observe its handle:

:::code source="snippets/durable-tasks/Orleans0228.cs" id="orleans0228_fix_attached" language="csharp":::

## When to suppress

Keep the diagnostic enabled: the start throws at run time.