---
title: "ORLEANS0122: Pass delegates which return a task for asynchronous work"
description: Understand ORLEANS0122, which reports an async void delegate passed to DurableTask.Run or DurableTask.RunInline, and fix it with a delegate which returns a Task.
ms.date: 10/17/2026
ms.topic: reference
---

# ORLEANS0122: Pass delegates which return a task for asynchronous work

| Property | Value |
| --- | --- |
| Severity | Error |
| Category | Usage |
| Code fix | None |

## Cause

`DurableTask.Run` or `DurableTask.RunInline` receives an asynchronous delegate which returns `void`: an `async` lambda cast to `Action` or `Action<CancellationToken>`, an `async void` method, or a local which a declaration initializes with one of them:

:::code source="snippets/durable-tasks/Orleans0122.cs" id="orleans0122_violation" language="csharp":::

An `async` lambda written directly in the call binds to the overloads which take a `Task`, so the C# compiler rejects it for `DurableTask.RunInline` and runs it as an asynchronous step for `DurableTask.Run`.

## Rule description

An `async void` delegate returns to its caller at its first incomplete `await`. `DurableTask.Run` and `DurableTask.RunInline` record the delegate's completion when it returns, so the record precedes the work which follows the `await`. A replay returns the record without running the delegate, so work which didn't finish never runs, and its failures escape the task instead of failing it. In the example, the step completes before the email is sent, and a failed send is lost.

## How to fix violations

Pass a delegate which returns the task of the work, so the step completes when the work completes:

:::code source="snippets/durable-tasks/Orleans0122.cs" id="orleans0122_fix" language="csharp":::

`DurableTask.RunInline` records synchronous work only; run asynchronous work as an awaited step, with `DurableTask.Run` or an `async DurableTask` method.

## When to suppress

Don't suppress this diagnostic: the task records the work as complete before it runs.
