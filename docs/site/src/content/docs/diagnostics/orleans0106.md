---
title: "ORLEANS0106: Run each durable task definition once"
description: Understand ORLEANS0106, which reports an async DurableTask definition which code runs more than once, and fix it by creating a definition for each run.
ms.date: 10/15/2026
ms.topic: reference
---

# ORLEANS0106: Run each durable task definition once

| Property | Value |
| --- | --- |
| Severity | Warning |
| Category | Usage |
| Code fix | None |

## Cause

A local variable holds a definition which an `async DurableTask` method in the same compilation returned, and code runs it a second time: it awaits, schedules, or posts the variable twice, or in a loop which doesn't create the definition:

:::code source="snippets/durable-tasks/Orleans0106.cs" id="orleans0106_violation" language="csharp":::

## Rule description

An `async DurableTask` method returns a single-use definition: it holds the method's state machine, which runs once. The first iteration of the loop runs the definition. The second iteration's run throws `InvalidOperationException` with the message `A deferred durable task definition can execute only once.`, which fails the task after its first reminder.

For definitions, see [Starting and observing tasks](https://github.com/dotnet/orleans/blob/main/src/Orleans.DurableTasks.Abstractions/README.md#starting-and-observing-tasks).

## How to fix violations

**Call the method for each run.** Each call creates a definition for one run:

:::code source="snippets/durable-tasks/Orleans0106.cs" id="orleans0106_fix" language="csharp":::

**Keep a factory instead of a definition.** A local function or a delegate which calls the method creates a definition each time the body runs it:

:::code source="snippets/durable-tasks/Orleans0106.cs" id="orleans0106_fix_factory" language="csharp":::

## When to suppress

Suppress the diagnostic when the paths which reach the second run are mutually exclusive, so each execution runs the definition once.
