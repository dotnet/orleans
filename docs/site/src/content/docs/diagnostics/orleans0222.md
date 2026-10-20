---
title: "ORLEANS0222: Pass delegates which return a task for asynchronous work"
description: Understand ORLEANS0222, which reports async void delegates passed to DurableTask.Run and DurableTask.RunInline, and fix them with delegates which return a task or with async DurableTask methods.
ms.date: 10/19/2026
ms.topic: reference
---

# ORLEANS0222: Pass delegates which return a task for asynchronous work

| Property | Value |
| --- | --- |
| Severity | Error |
| Category | Usage |
| Code fix | None |

## Cause

`DurableTask.Run` or `DurableTask.RunInline` receives a delegate which returns `void` and binds an `async` function: an `async` lambda converted to `Action` or `Action<CancellationToken>`, an `async void` method, or a local which holds one:

:::code source="snippets/durable-tasks/Orleans0222.cs" id="orleans0222_violation" language="csharp":::

## Rule description

An `async void` delegate returns to its caller at its first incomplete `await`. `DurableTask.Run` and `DurableTask.RunInline` record the delegate's completion when it returns, so the record precedes the work which follows the `await`:

- The step or the effect completes, and the body continues, while the work is still running.
- A replay returns the record without running the delegate, so the work runs only in the first run.
- An exception which the work throws after the `await` escapes the task instead of failing the step or the effect.

In the example, the step completes before the email is sent, and a failed send leaves the step's record successful.

For steps and effects, see [Recorded effects](../grains/durable-tasks.md#recorded-effects).

## How to fix violations

**Return a task.** `DurableTask.Run` with a delegate which returns a `Task` completes the step when the task completes, and records the task's outcome:

:::code source="snippets/durable-tasks/Orleans0222.cs" id="orleans0222_fix_task" language="csharp":::

**Run the work in an `async DurableTask` method.** The method is a step which the body awaits:

:::code source="snippets/durable-tasks/Orleans0222.cs" id="orleans0222_fix_durable_method" language="csharp":::

`DurableTask.RunInline` runs synchronous work only. Asynchronous work runs as a step.

## When to suppress

Suppress the diagnostic only for fire-and-forget work which the task neither records nor waits for, and whose failures the delegate handles itself.
