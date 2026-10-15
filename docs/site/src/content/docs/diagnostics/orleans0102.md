---
title: "ORLEANS0102: Await external operations in steps or after the last durable operation"
description: Understand ORLEANS0102, which reports durable task code which awaits a service, a grain call, or other external work where a replay runs it again, and fix it with steps or the synchronous tail.
ms.date: 10/15/2026
ms.topic: reference
---

# ORLEANS0102: Await external operations in steps or after the last durable operation

| Property | Value |
| --- | --- |
| Severity | Warning |
| Category | Usage |
| Code fix | None |

## Cause

A durable task body awaits an operation which isn't durable, such as an `HttpClient` call, a database query, an ordinary grain call, or a `Task`-returning service method, at a point from which it still reaches a durable operation. Awaits of durable tasks, their configurations and handles, durable task APIs, `Task.Yield`, and helpers in the same compilation which perform durable operations aren't external. The following invoice fetches an exchange rate before its first step:

:::code source="snippets/durable-tasks/Orleans0102.cs" id="orleans0102_violation" language="csharp":::

## Rule description

A replay runs the code before the body's last durable operation again, including each await in it. An external await then:

- **Repeats its effects.** A call which changes another system, such as sending a message or creating a record, runs once per replay.
- **Observes another result.** The replay receives another exchange rate and passes another amount to the recorded `bill` step, which fails with a divergence error when the step is a call to another grain, and otherwise returns the recorded total for a different amount.
- **Fails where the first run succeeded.** A replay which runs while the service is unavailable fails the task, although every recorded operation succeeded.

A step records its result, so a replay returns the result without awaiting the operation again.

For the rules and the replay model, see [Replay-safety rules](../grains/durable-tasks-replay-rules.md).

## How to fix violations

**Await the operation in `DurableTask.Run`.** The delegate runs as a child step, its result is recorded, and the step receives a cancellation token which durable cancellation signals:

:::code source="snippets/durable-tasks/Orleans0102.cs" id="orleans0102_fix_run" language="csharp":::

**Await the operation in a local step.** An `async DurableTask` method of the same grain records its completion. Name it when it does more than one thing, or when other workflows reuse it:

:::code source="snippets/durable-tasks/Orleans0102.cs" id="orleans0102_fix_step" language="csharp":::

**Await the operation after the body's last durable operation.** The synchronous tail runs only in a run which completes the body:

:::code source="snippets/durable-tasks/Orleans0102.cs" id="orleans0102_fix_tail" language="csharp":::

A step which ends before its result commits runs again, and so does the tail of a run which ends before the task's result commits. An external operation in a step or the tail therefore runs at least once, so give it an idempotency key derived from the task, such as `$"{grainId}/{DurableTask.Current.Id}"`, when it changes another system; see [Idempotency in durable tasks](../grains/durable-tasks-idempotency.md) and [Integrate durable tasks with other systems](../grains/durable-tasks-integrations.md).

## When to suppress

Suppress the diagnostic for a read which is idempotent, returns the same value in every run, and can't fail during a replay, such as a read of immutable configuration.
