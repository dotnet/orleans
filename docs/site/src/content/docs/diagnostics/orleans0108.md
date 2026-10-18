---
title: "ORLEANS0108: Give each durable operation call site its own name"
description: Understand ORLEANS0108, which reports two durable operation call sites in one body with the same name, and fix it by naming each call site.
ms.date: 10/15/2026
ms.topic: reference
---

# ORLEANS0108: Give each durable operation call site its own name

| Property | Value |
| --- | --- |
| Severity | Warning |
| Category | Usage |
| Code fix | None |

## Cause

Two call sites of `WithId`, or two call sites of `DurableTask.RunInline`, in one durable task body pass the same constant name, and one run can reach both. Call sites in mutually exclusive branches, such as the two arms of an `if` statement, and one call site which a loop repeats aren't reported:

:::code source="snippets/durable-tasks/Orleans0108.cs" id="orleans0108_violation" language="csharp":::

## Rule description

A name which one run repeats identifies its later occurrences by their order: the first occurrence is `email`, and the second is `email$2`. Each identifier then depends on how many earlier call sites used the name.

When a deployment removes the welcome email while executions are pending, the replay of an execution which already sent both emails names the tips email `email`. That identifier holds the welcome email's record:

- For a local step, the replay returns the welcome email's recorded completion, and the tips email's own record, `email$2`, is never reached.
- For a call to another grain, the replay finds another request under the identifier and fails with a divergence error.

Distinct names keep each call site's identifier when other call sites are added, removed, or reordered.

For identifiers and versioning, see [Writing replay-safe workflows](https://github.com/dotnet/orleans/blob/main/src/Orleans.DurableTasks.Abstractions/README.md#writing-replay-safe-workflows).

## How to fix violations

**Give each call site its own name:**

:::code source="snippets/durable-tasks/Orleans0108.cs" id="orleans0108_fix" language="csharp":::

**Name a repeated call site once.** A call site in a loop keeps one name, and the runtime numbers its occurrences in order. To identify each occurrence by its input instead, derive the name from a stable value, such as `$"lesson-{index}"`:

:::code source="snippets/durable-tasks/Orleans0108.cs" id="orleans0108_fix_loop" language="csharp":::

## When to suppress

Suppress the diagnostic when the call sites can't both run in one execution, through a condition which the analyzer doesn't follow.
