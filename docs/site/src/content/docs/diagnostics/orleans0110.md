---
title: "ORLEANS0110: Run concurrent durable work through durable combinators"
description: Understand ORLEANS0110, which reports Task.WhenAll, Task.WhenAny, Task.WaitAll, or Task.WaitAny over durable work, and fix it with DurableTask.WhenAll, WhenAny, Race, or ScheduleAll.
ms.date: 10/15/2026
ms.topic: reference
---

# ORLEANS0110: Run concurrent durable work through durable combinators

| Property | Value |
| --- | --- |
| Severity | Warning |
| Category | Usage |
| Code fix | None |

## Cause

A durable task body passes durable work, such as lambdas which await durable tasks, to `Task.WhenAll`, `Task.WhenAny`, `Task.WaitAll`, or `Task.WaitAny`:

:::code source="snippets/durable-tasks/Orleans0110.cs" id="orleans0110_violation" language="csharp":::

## Rule description

Enumerating the `Select` in this example invokes each lambda in input order, and its awaits capture the activation's scheduler. Each child commits its invocation in a separate journal write. A durable fan-out combinator gives the inputs a shared durable identity and commits their invocations as a batch.

`Task` combinators also leave losing inputs running: `Task.WhenAny` returns while the other inputs continue, and nothing cancels or awaits them. `Task.WaitAll` and `Task.WaitAny` block the activation's scheduler.

Durable combinators identify each input by its index beneath the combinator's name, such as `reserve/0` and `reserve/1`, schedule the inputs in one batch which one journal write commits, and record which input completed first, so every replay observes the same outcome.

For combinators, see [Combinators](https://github.com/dotnet/orleans/blob/main/src/Orleans.DurableTasks.Abstractions/README.md#combinators).

## How to fix violations

**Use `DurableTask.WhenAll`** for results in input order:

:::code source="snippets/durable-tasks/Orleans0110.cs" id="orleans0110_fix_when_all" language="csharp":::

**Use `DurableTask.Race`** for the first result. `Race` requests cancellation of the other inputs and waits until each is terminal, so no input outlives it. `DurableTask.WhenAny` returns the first result and leaves the others running:

:::code source="snippets/durable-tasks/Orleans0110.cs" id="orleans0110_fix_race" language="csharp":::

**Use `DurableTask.ScheduleAll`** to start every input and continue with their handles, and `DurableTask.WhenAllCompleted` to wait for every outcome without throwing. To bound durable work with a timeout instead of racing it with `Task.Delay`, use the task's `WaitAsync` or `WaitUntilAsync`.

## When to suppress

Suppress the diagnostic when the combined tasks perform no durable operations, which the analyzer can't determine, for example when they call helpers in another assembly.
