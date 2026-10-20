---
title: "ORLEANS0217: Pass AttemptCancellationToken to calls in task-cancellation cleanup"
description: Understand ORLEANS0217, which reports cleanup after task cancellation which passes the already-canceled CancellationToken to a call, and fix it with AttemptCancellationToken or a shielded step.
ms.date: 10/19/2026
ms.topic: reference
---

# ORLEANS0217: Pass AttemptCancellationToken to calls in task-cancellation cleanup

| Property | Value |
| --- | --- |
| Severity | Warning |
| Category | Usage |
| Code fix | Available |

## Cause

Code which runs only after task cancellation passes `DurableTaskContext.CancellationToken` to a call. Such code is the handler of a `catch` clause, or the true branch of an `if` statement or a conditional expression, whose filter or condition is `DurableTaskContext.IsTaskCancellationRequested`, alone or as an operand of `&&`:

:::code source="snippets/durable-tasks/Orleans0217.cs" id="orleans0217_violation" language="csharp":::

## Rule description

A durable task's context carries two cancellation tokens:

- `CancellationToken` is canceled by task cancellation and by attempt cancellation, so the calls which ordinary task code makes end promptly in both cases.
- `AttemptCancellationToken` is canceled only by attempt cancellation, for example when the task's activation stops.

Task cancellation is recorded with the task, so `IsTaskCancellationRequested` stays `true`, and `CancellationToken` stays canceled, in the canceled attempt and in every later attempt. Cleanup which runs after task cancellation therefore passes a canceled token, and a call which observes it ends at once. In the example, `ReleaseAsync` throws `OperationCanceledException` before it releases the room, the exception escapes the body, and the task ends canceled with the room still held.

The rule inspects the code of the region itself. Lambdas and local functions in the region can run in another task's context, such as a step's, so the rule skips them.

For the two tokens, see [Execution model](../grains/durable-tasks.md#execution-model), and for cleanup, see [Compensating canceled work](../grains/durable-tasks.md#compensating-canceled-work).

## How to fix violations

**Pass `AttemptCancellationToken`.** The code fix replaces `CancellationToken` with `AttemptCancellationToken` and keeps the receiver. The call runs to completion, and it still ends promptly when the host cancels the attempt:

:::code source="snippets/durable-tasks/Orleans0217.cs" id="orleans0217_fix_attempt_token" language="csharp":::

**Release in a shielded step.** A step receives its own cancellation token, and the task's cancellation doesn't reach a shielded step. The step records its outcome, so a replay of the handler returns it and releases the room once:

:::code source="snippets/durable-tasks/Orleans0217.cs" id="orleans0217_fix_shielded_step" language="csharp":::

In-process cleanup, such as releasing a semaphore or disposing a resource, belongs in `finally` blocks and `using` statements, which run for both kinds of cancellation.

## When to suppress

Suppress the diagnostic for a call which is meant to end at once after task cancellation, such as optional work which the cleanup skips once the task is canceled.
