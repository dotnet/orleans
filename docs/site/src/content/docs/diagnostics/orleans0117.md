---
title: "ORLEANS0117: Pass AttemptCancellationToken to calls in task-cancellation cleanup"
description: Understand ORLEANS0117, which reports cleanup code that runs after task cancellation and passes the already-canceled DurableTaskContext.CancellationToken to a call, and fix it with AttemptCancellationToken.
ms.date: 10/16/2026
ms.topic: reference
---

# ORLEANS0117: Pass AttemptCancellationToken to calls in task-cancellation cleanup

| Property | Value |
| --- | --- |
| Severity | Warning |
| Category | Usage |
| Code fix | Available |

## Cause

Code which runs only after task cancellation passes `DurableTaskContext.CancellationToken`, such as `DurableTask.Current.CancellationToken`, as an argument to a call. The rule recognizes two such regions:

- The handler of a `catch` clause whose filter is `IsTaskCancellationRequested`, or a `&&` conjunction with it as an operand.
- The true branch of an `if` statement or a conditional expression whose condition is `IsTaskCancellationRequested`, or a `&&` conjunction with it as an operand.

The rule doesn't inspect lambdas and local functions in those regions, because they can run in another task's context, such as a `DurableTask.Run` step.

:::code source="snippets/durable-tasks/Orleans0117.cs" id="orleans0117_violation" language="csharp":::

## Rule description

A durable task's context provides two tokens:

- `CancellationToken` is canceled when task cancellation is requested and when the host cancels the attempt. Ordinary workflow code passes it to its calls, so they end promptly in both cases.
- `AttemptCancellationToken` is canceled only when the host cancels the attempt, for example when the grain activation stops, or the task is aborted or deleted.

Task cancellation is durable, and `IsTaskCancellationRequested` stays `true` once it is requested, so code which that check guards always observes a canceled `CancellationToken`. A cleanup call which receives it, such as the release in the example, ends at once, and the reservation stays held.

For both kinds of cancellation, see [Task cancellation and attempt cancellation](../grains/durable-tasks-structured-concurrency.md#task-cancellation-and-attempt-cancellation).

## How to fix violations

**Pass `AttemptCancellationToken` to the cleanup's calls.** The call runs to completion, and still ends promptly when the host cancels the attempt; when the activation stops, the task's next attempt runs the cleanup again. Release in-process resources, such as semaphores and disposables, in `finally` blocks and `using` statements, which run for both kinds of cancellation:

:::code source="snippets/durable-tasks/Orleans0117.cs" id="orleans0117_fix" language="csharp":::

The code fix replaces `CancellationToken` with `AttemptCancellationToken` and keeps the receiver, such as `DurableTask.Current` or a context variable. Fix all occurrences in a document, project, or solution at once.

## When to suppress

Suppress the diagnostic when the call should end immediately after task cancellation, for example a call which only checks the token to skip optional work.
