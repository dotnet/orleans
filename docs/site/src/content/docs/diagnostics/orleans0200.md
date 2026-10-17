---
title: "ORLEANS0200: Access persistent grain state in recorded effects or after the last durable operation"
description: Understand ORLEANS0200, which reports durable task code which reads or changes persistent grain state where a replay runs it again, and fix it with recorded effects, the synchronous tail, local steps, or journaled state.
ms.date: 10/16/2026
ms.topic: reference
---

# ORLEANS0200: Access persistent grain state in recorded effects or after the last durable operation

| Property | Value |
| --- | --- |
| Severity | Warning |
| Category | Usage |
| Code fix | None |

## Cause

A durable task body reads or changes persistent grain state at a point from which the body still reaches a durable operation. Persistent grain state is a field or property whose type is, or implements, `IPersistentState<TState>`. The first method counts a reminder before sending it, and the second branches on a status which its own step changes:

:::code source="snippets/durable-tasks/Orleans0200.cs" id="orleans0200_violation" language="csharp":::

## Rule description

A replay runs the body from the start, and each recorded operation returns its recorded outcome. Persistent state isn't rebuilt: it holds every committed change, including the changes which later operations made, and the grain changes its value in place. The code before the body's last durable operation therefore runs again against newer state:

- **Writes apply again.** After a deactivation, the replay of `RemindAsync` increments `_remindersSent` a second time, although the reminder was sent once.
- **Reads observe later writes.** The replay of `ShipWhenPaidAsync` reads `_status`, which the recorded `ship` step wrote, and skips the branch which the first run took. A replay which schedules different operations, or the same operations with different arguments, returns a different result or diverges and stalls the execution.

Journaled state, such as `IDurableValue<T>` and the durable collections, records each access of a durable task body, and a replay returns the recorded outcomes, so the rule doesn't apply to it. Three places access persistent state once:

- A `DurableTask.RunInline` effect runs once, and its state changes commit with its record.
- A local step, an `async DurableTask` method of the same grain, commits the state changes of its final code with its recorded result.
- The body's synchronous tail, the code after its last durable operation, commits its state changes with the task's result.

For the rules and the replay model, see [Replay-safety rules](../grains/durable-tasks-replay-rules.md).

## How to fix violations

**Keep the state in journaled state.** An `IDurableValue<T>` or a durable collection records each read and write of the body, so a replay observes the values which the first run read and applies no committed change again:

:::code source="snippets/durable-tasks/Orleans0200.cs" id="orleans0200_journaled" language="csharp":::

**Record the access with `DurableTask.RunInline`.** The effect runs once and returns its outputs, and a replay returns the recorded values. Read state which decides later operations the same way:

:::code source="snippets/durable-tasks/Orleans0200.cs" id="orleans0200_fix_once" language="csharp":::

**Move the change into the synchronous tail.** A change with no durable operation after it commits with the task's result:

:::code source="snippets/durable-tasks/Orleans0200.cs" id="orleans0200_fix_tail" language="csharp":::

**Make the change in a local step.** A step whose body changes state and returns, such as `MarkShipped` above, commits the change with its recorded result, and a replay returns the result without running the step:

:::code source="snippets/durable-tasks/Orleans0200.cs" id="orleans0200_fix_step" language="csharp":::

For journaled state and recorded effects, see [Journaled state in durable task bodies](../grains/durable-tasks.md#journaled-state-in-durable-task-bodies) and [Recorded effects, local steps, and calls to other grains](../grains/durable-tasks.md#recorded-effects-local-steps-and-calls-to-other-grains).

## When to suppress

Suppress the diagnostic for a read whose value never decides a durable operation, its arguments, or a state change, such as a value which only reaches a log message, and for state which no operation changes while the task runs, such as a value which the grain writes once when it's created.