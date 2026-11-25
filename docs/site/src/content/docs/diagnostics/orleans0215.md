---
title: "ORLEANS0215: Log through a replay-safe logger"
description: Understand ORLEANS0215, which reports log calls in durable task code which a replay runs again, and fix them with ReplaySafe().
ms.date: 10/15/2026
ms.topic: reference
---

# ORLEANS0215: Log through a replay-safe logger

| Property | Value |
| --- | --- |
| Severity | Info |
| Category | Usage |
| Code fix | None |

## Cause

A durable task body logs through an `ILogger`, with a `LoggerExtensions` method, `ILogger.Log`, or a `[LoggerMessage]` method, at a point from which it still reaches a durable operation, and the logger isn't one which `ReplaySafe()` returns:

:::code source="snippets/durable-tasks/Orleans0215.cs" id="orleans0215_violation" language="csharp":::

## Rule description

A replay runs the code before the body's last durable operation again, including its log calls. After the export in the example deactivates during its tenth partition, the replay logs `Exporting partition` for the nine completed partitions again, and logs repeat each time the task replays. Logs then overstate the work, and log-based metrics and alerts count it again.

A logger which `ReplaySafe()` returns skips events while the task replays: from the start of a run which finds recorded operations, until the run reaches an operation which no earlier run recorded. Each event is then logged by the run which first reaches it. When an activation ends before that run's write commits, the next run repeats the uncommitted work and its logs, so these logs suit diagnostics, and exactly-once metrics or audits belong in recorded results.

For replay-safe logging, see [Replay and determinism](../grains/durable-tasks.md#replay-and-determinism).

## How to fix violations

Wrap the logger with `ReplaySafe()`, typically once in the grain's constructor. The logger reads the running task when it logs, so ordinary grain methods use it too:

:::code source="snippets/durable-tasks/Orleans0215.cs" id="orleans0215_fix" language="csharp":::

Logs in a `DurableTask.RunInline` effect, in a step, or in the body's synchronous tail repeat only until their results commit: a replay returns a recorded result without running its code, and code whose record didn't commit before the activation ended runs, and logs, again.

## When to suppress

Suppress the diagnostic for logs which should record every run, such as debugging logs which show where replays happen.
