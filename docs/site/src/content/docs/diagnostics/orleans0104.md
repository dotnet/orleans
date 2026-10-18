---
title: "ORLEANS0104: Return the outputs of a DurableTask.RunInline effect"
description: Understand ORLEANS0104, which reports a DurableTask.RunInline effect which assigns variables of the enclosing body, and fix it by returning the outputs.
ms.date: 10/15/2026
ms.topic: reference
---

# ORLEANS0104: Return the outputs of a DurableTask.RunInline effect

| Property | Value |
| --- | --- |
| Severity | Warning |
| Category | Usage |
| Code fix | None |

## Cause

The delegate which `DurableTask.RunInline` receives assigns, increments, or decrements a variable which the enclosing body declares:

:::code source="snippets/durable-tasks/Orleans0104.cs" id="orleans0104_violation" language="csharp":::

## Rule description

`DurableTask.RunInline` runs its effect once for the running task and records the effect's result. A replay returns the recorded result without running the effect, so the assignments in the delegate happen only in the run which runs it. In every replay, `number` stays `0` and `reference` stays `Guid.Empty`: the replay passes other arguments to the recorded `print` step and returns another ticket than the first run computed.

The state changes which the effect makes, such as the increment of `_lastNumber`, are correct: they commit with the record, and the replay finds them in the grain's state.

For recorded effects, see [Recorded effects](https://github.com/dotnet/orleans/blob/main/src/Orleans.DurableTasks.Abstractions/README.md#recorded-effects).

## How to fix violations

**Return every output.** Return a tuple from the effect, and deconstruct it in the body:

:::code source="snippets/durable-tasks/Orleans0104.cs" id="orleans0104_fix" language="csharp":::

**Return a record.** A type with an Orleans serializer names the outputs, and the body can return it:

:::code source="snippets/durable-tasks/Orleans0104.cs" id="orleans0104_fix_record" language="csharp":::

Variables which the delegate declares, and grain state which it changes, need no change.

## When to suppress

Suppress the diagnostic for an assignment which the body reads only inside the effect, or only for diagnostics in the run which ran the effect.
