---
title: "ORLEANS0113: Return a task from asynchronous code in durable task bodies"
description: Understand ORLEANS0113, which reports async void methods, local functions, and lambdas in durable task bodies, and fix them by returning and awaiting a task.
ms.date: 10/15/2026
ms.topic: reference
---

# ORLEANS0113: Return a task from asynchronous code in durable task bodies

| Property | Value |
| --- | --- |
| Severity | Warning |
| Category | Usage |
| Code fix | None |

## Cause

A durable task body declares an `async void` local function, or an `async` lambda which converts to a delegate that returns `void`, such as `Action`:

:::code source="snippets/durable-tasks/Orleans0113.cs" id="orleans0113_violation" language="csharp":::

## Rule description

A durable task body observes the operations which it awaits: their results, their failures, and their cancellation. Asynchronous `void` code can't be awaited. In the example:

- The body returns, and the task's result commits, before the email is sent.
- A failure to send escapes the body, so the task completes successfully, and the exception surfaces outside the task.
- Durable operations which the code performs after its first `await` run after the body completed, outside the task's execution.

For the replay model, see [Replay and determinism](../grains/durable-tasks.md#replay-and-determinism).

## How to fix violations

**Return a `DurableTask` and await it as a step.** The body observes the step's outcome, and a replay returns its record:

:::code source="snippets/durable-tasks/Orleans0113.cs" id="orleans0113_fix_step" language="csharp":::

**Return a `Task` from callbacks.** Use `Func<Task>` instead of `Action` for asynchronous callbacks, and await the callback in a step:

:::code source="snippets/durable-tasks/Orleans0113.cs" id="orleans0113_fix_func" language="csharp":::

## When to suppress

Don't suppress this diagnostic. An event handler whose signature requires `void` belongs outside the durable task body, where it can start a durable task through its grain.
