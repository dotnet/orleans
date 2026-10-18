---
title: "ORLEANS0115: Log through a replay-safe logger"
description: Understand ORLEANS0115, which reports log calls in durable task code which a replay runs again, and fix them with ReplaySafe().
ms.date: 10/15/2026
ms.topic: reference
---

# ORLEANS0115: Log through a replay-safe logger

| Property | Value |
| --- | --- |
| Severity | Info |
| Category | Usage |
| Code fix | None |

## Cause

A durable task body logs through an `ILogger`, with a `LoggerExtensions` method, `ILogger.Log`, or a `[LoggerMessage]` method, at a point from which it still reaches a durable operation, and the logger isn't one which `ReplaySafe()` returns:

:::code source="snippets/durable-tasks/Orleans0115.cs" id="orleans0115_violation" language="csharp":::

## Rule description

A replay runs the code before the body's last durable operation again, including its log calls. After the export in the example deactivates during its tenth partition, the replay logs `Exporting partition` for the nine completed partitions again, and logs repeat each time the task replays. Logs then overstate the work, and log-based metrics and alerts count it again.

A logger which `ReplaySafe()` returns skips events while the task replays: from the start of a run which finds recorded operations, until the run reaches an operation which no earlier run recorded. Each event is then logged once.

For replay-safe logging, see [Replay diagnostics and logging](https://github.com/dotnet/orleans/blob/main/src/Orleans.DurableTasks.Abstractions/README.md#replay-diagnostics-and-logging).

## How to fix violations

Wrap the logger with `ReplaySafe()`, typically once in the grain's constructor. The logger reads the running task when it logs, so ordinary grain methods use it too:

:::code source="snippets/durable-tasks/Orleans0115.cs" id="orleans0115_fix" language="csharp":::

Logs in a `DurableTask.RunInline` effect, in a step, or in the body's synchronous tail don't repeat, because a replay returns their recorded results without running them.

## When to suppress

Suppress the diagnostic for logs which should record every run, such as debugging logs which show where replays happen.
