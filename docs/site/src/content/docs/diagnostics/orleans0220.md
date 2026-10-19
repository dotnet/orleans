---
title: "ORLEANS0220: Name durable operations whose position can change"
description: Understand ORLEANS0220, which reports unnamed durable operations in loops, conditions, or after other unnamed operations, and fix it with WithId or a DurableTask.RunInline name.
ms.date: 10/19/2026
ms.topic: reference
---

# ORLEANS0220: Name durable operations whose position can change

| Property | Value |
| --- | --- |
| Severity | Info |
| Category | Usage |
| Code fix | Available |

## Cause

A durable task body runs an unnamed durable operation in a loop or a condition, or after another unnamed operation of its kind. Unnamed operations are definitions which the body awaits, schedules, or posts without `WithId`, `DurableTask.RunInline` effects without a name, and awaited combinators without `WithId`:

:::code source="snippets/durable-tasks/Orleans0220.cs" id="orleans0220_violation" language="csharp":::

## Rule description

An unnamed operation takes the execution's next generated identifier, which counts the unnamed operations of its kind which ran before it. Children, effects, and detached starts share one count, `$child-1`, `$child-2`, and so on, and awaited combinators take operation identifiers, such as `$when-all-1`, from a count of their own. A signal wait which the body awaits directly runs as part of the execution and takes no identifier.

In the example, the points effect is `$child-1` in the executions of repeat orders and `$child-2` in the executions of first orders. A replay of the same execution on the same code reproduces the count. A deployment which adds, removes, or moves an unnamed operation before it shifts the count, so the replay schedules an operation under an identifier which the execution recorded for another operation. The replay diverges: the host cancels the attempt with `DurableTaskAttemptCancellationReason.Diverged` and keeps the task pending.

A name identifies the operation by its call site. A call site which runs more than once in one run, such as one in a loop, keeps one name, and later occurrences take `name$2`, `name$3`, and so on by their order.

For identifiers, see [Writing replay-safe workflows](https://github.com/dotnet/orleans/blob/main/src/Orleans.DurableTasks.Abstractions/README.md#writing-replay-safe-workflows), and for unnamed effects, see [Recorded effects](https://github.com/dotnet/orleans/blob/main/src/Orleans.DurableTasks.Abstractions/README.md#recorded-effects).

## How to fix violations

**Name each operation.** Name definitions and combinators with `WithId`, and pass a name as the first argument of `DurableTask.RunInline`:

:::code source="snippets/durable-tasks/Orleans0220.cs" id="orleans0220_fix" language="csharp":::

The code fix adds `WithId` to a definition, with the kebab-case name of the method which defines it, without its `Async` suffix, such as `send-welcome` for `SendWelcome`. It adds a number, such as `send-welcome-2`, when the body already uses the name. Name `DurableTask.RunInline` effects by hand.

A name changes the operation's identifier. Name operations before executions which use them are pending, or branch the change on `DurableTask.GetVersion`.

## When to suppress

Suppress the diagnostic for a body whose executions complete on the code which started them, such as a short-lived step, when its loops and conditions depend only on its arguments and recorded results.
