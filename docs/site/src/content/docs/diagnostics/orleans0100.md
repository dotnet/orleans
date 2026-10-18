---
title: "ORLEANS0100: Access grain state in recorded effects or after the last durable operation"
description: Understand ORLEANS0100, which reports durable task code which reads or changes grain state where a replay runs it again, and fix it with recorded effects, the synchronous tail, or local steps.
ms.date: 10/15/2026
ms.topic: reference
---

# ORLEANS0100: Access grain state in recorded effects or after the last durable operation

| Property | Value |
| --- | --- |
| Severity | Warning |
| Category | Usage |
| Code fix | None |

## Cause

A durable task body reads or changes grain state at a point from which the body still reaches a durable operation. Grain state is a field or property whose type is, or implements, a journaled collection, `IDurableValue<T>`, `IDurableDictionary<TKey, TValue>`, `IDurableList<T>`, `IDurableQueue<T>`, `IDurableSet<T>`, `IDurableTaskCompletionSource<T>`, or `IDurableLog<T>`, or persistent state, `IPersistentState<TState>`. The first method counts a reminder before sending it, and the second branches on a status which its own step changes:

:::code source="snippets/durable-tasks/Orleans0100.cs" id="orleans0100_violation" language="csharp":::

## Rule description

A replay runs the body from the start, and each recorded operation returns its recorded outcome. Grain state isn't rebuilt: it holds every committed change, including the changes which later operations made. The code before the body's last durable operation therefore runs again against newer state:

- **Writes apply again.** After a deactivation, the replay of `RemindAsync` increments `_remindersSent` a second time, although the reminder was sent once.
- **Reads observe later writes.** The replay of `ShipWhenPaidAsync` reads `Shipped`, which the recorded `ship` step wrote, and skips the branch which the first run took. A replay which schedules different operations, or the same operations with different arguments, returns a different result or fails with a divergence error.

Three places access grain state once:

- A `DurableTask.RunInline` effect runs once, and its state changes commit with its record.
- A local step, an `async DurableTask` method of the same grain, commits the state changes of its final code with its recorded result.
- The body's synchronous tail, the code after its last durable operation, commits its state changes with the task's result.

For the replay model, see [Writing replay-safe workflows](https://github.com/dotnet/orleans/blob/main/src/Orleans.DurableTasks.Abstractions/README.md#writing-replay-safe-workflows).

## How to fix violations

**Record the access with `DurableTask.RunInline`.** The effect runs once and returns its outputs, and a replay returns the recorded values. Read state which decides later operations the same way, and append progress to an `IDurableLog<T>` with `DurableTask.RunInline(() => log.Append(entry))`, so each entry is appended once:

:::code source="snippets/durable-tasks/Orleans0100.cs" id="orleans0100_fix_once" language="csharp":::

**Move the change into the synchronous tail.** A change with no durable operation after it commits with the task's result:

:::code source="snippets/durable-tasks/Orleans0100.cs" id="orleans0100_fix_tail" language="csharp":::

**Make the change in a local step.** A step whose body changes state and returns, such as `MarkShipped` above, commits the change with its recorded result, and a replay returns the result without running the step:

:::code source="snippets/durable-tasks/Orleans0100.cs" id="orleans0100_fix_step" language="csharp":::

For recorded effects, see [Recorded effects](https://github.com/dotnet/orleans/blob/main/src/Orleans.DurableTasks.Abstractions/README.md#recorded-effects).

## When to suppress

Suppress the diagnostic for a read whose value never decides a durable operation, its arguments, or a state change, such as a value which only reaches a log message, and for state which no operation changes while the task runs, such as a value which the grain writes once when it's created.
