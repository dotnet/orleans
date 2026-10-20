---
title: "ORLEANS0227: Run durable operations outside DurableTask.RunInline effects"
description: Understand ORLEANS0227, which reports task starts and version checks inside DurableTask.RunInline effects, where they throw, and fix them by running the operations in the body.
ms.date: 10/19/2026
ms.topic: reference
---

# ORLEANS0227: Run durable operations outside DurableTask.RunInline effects

| Property | Value |
| --- | --- |
| Severity | Error |
| Category | Usage |
| Code fix | None |

## Cause

The delegate of a `DurableTask.RunInline` effect starts a durable task, with `ScheduleAsync` or `Post`, or checks a version, with `DurableTask.GetVersion`, `DurableVersion.Get`, or `DurableVersion.IsAtLeast`:

:::code source="snippets/durable-tasks/Orleans0227.cs" id="orleans0227_violation" language="csharp":::

## Rule description

A replay returns an effect's recorded outcome without running the effect. A durable operation in the effect would run in the first run only: the replay would miss it, and the identifiers of the body's later operations would shift.

The runtime throws `InvalidOperationException` for a durable operation inside an effect, and the effect records the exception as its outcome. Changes which the effect made before the throw commit with the recorded exception. In the example, the version check throws after the seat count increments, the increment commits, and every run of the body, including every replay, receives the recorded exception from `DurableTask.RunInline`.

A `DurableTask.RunInline` call inside an effect runs as part of the enclosing effect, and its outcome is part of the enclosing effect's outcome.

For effects, see [Effects and steps](../grains/durable-tasks.md#effects-and-steps).

## How to fix violations

**Run the operations in the body.** Check versions and start tasks before or after the effect. Pass values into the effect through its closure, and return values from it:

:::code source="snippets/durable-tasks/Orleans0227.cs" id="orleans0227_fix" language="csharp":::

## When to suppress

Keep the diagnostic enabled: a durable operation in an effect throws `InvalidOperationException`, which the effect records as its outcome.
