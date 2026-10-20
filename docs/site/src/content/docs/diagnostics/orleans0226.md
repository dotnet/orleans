---
title: "ORLEANS0226: Continue durable task bodies on their scheduler"
description: Understand ORLEANS0226, which reports ConfigureAwait calls which move a durable task body's continuation off its grain's scheduler, and fix them by awaiting without ConfigureAwait.
ms.date: 10/19/2026
ms.topic: reference
---

# ORLEANS0226: Continue durable task bodies on their scheduler

| Property | Value |
| --- | --- |
| Severity | Error |
| Category | Usage |
| Code fix | None |

## Cause

A durable task body, including a step, awaits with `ConfigureAwait(false)`, or with `ConfigureAwaitOptions` which omit `ContinueOnCapturedContext`:

:::code source="snippets/durable-tasks/Orleans0226.cs" id="orleans0226_violation" language="csharp":::

## Rule description

A durable task body runs on its grain activation's scheduler, which runs one piece of the grain's code at a time. The scheduler orders the body's durable operations and its state changes, and the runtime commits the body's result with the state changes which the body's tail makes.

An await which continues without its captured context resumes the body on the thread pool:

- Durable operations, such as `DurableTask.RunInline`, task starts, and `DurableTask.GetVersion`, throw `InvalidOperationException` there, because the execution orders and records its operations on its scheduler.
- State changes race with the grain's other turns, including its methods and other executions.
- The tail's state changes and the body's result commit separately. In the example, a crash after `_lastRate` commits and before the result commits runs the tail again, which fetches a rate and assigns `_lastRate` again.

ORLEANS0014 reports `ConfigureAwait(false)` in grain code in general.

For the body's tail, see [Writing replay-safe workflows](../grains/durable-tasks.md#writing-replay-safe-workflows).

## How to fix violations

**Await without `ConfigureAwait`.** The continuation returns to the activation's scheduler. `ConfigureAwait(true)` and `ConfigureAwaitOptions.ContinueOnCapturedContext` continue there too:

:::code source="snippets/durable-tasks/Orleans0226.cs" id="orleans0226_fix" language="csharp":::

Library methods which the body awaits keep their own `ConfigureAwait(false)`: the rule inspects the body's awaits, and the body's await of the library method resumes on the scheduler.

## When to suppress

Keep the diagnostic enabled: durable operations throw off the scheduler, and state changes there race with the grain's other work.
