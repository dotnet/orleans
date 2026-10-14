---
title: "ORLEANS0101: Produce non-deterministic values in recorded effects"
description: Understand ORLEANS0101, which reports durable task code which produces non-deterministic values in replayed code.
ms.date: 10/13/2026
ms.topic: reference
---

# ORLEANS0101: Produce non-deterministic values in recorded effects

| Property | Value |
| --- | --- |
| Severity | Warning |
| Category | Usage |
| Code fix | None |

## Cause

Code in a durable task body which can still reach a durable operation reads the clock, creates a GUID or a random number, reads a stopwatch or environment information, or otherwise produces a value which differs between runs.

## Rule description

A replay runs this code again, and it observes another value. When the value reaches a durable operation's arguments or identifier, or decides which operations run, the replay schedules different operations from the recorded run.

`DurableTask.Current.UtcNow` is the execution's logical time, which advances to each observed child's recorded completion time, so a replay observes the same times. `DurableTask.Once` records any other value, so a replay returns the recorded value.

For the replay model, see [Writing replay-safe workflows](https://github.com/dotnet/orleans/blob/main/src/Orleans.DurableTasks.Abstractions/README.md#writing-replay-safe-workflows).

## How to fix violations

Read time from `DurableTask.Current.UtcNow`. Produce other values in a `DurableTask.Once` effect, for example a GUID which identifies a payment request.

## When to suppress

Values which only reach log messages are excluded. Suppress the diagnostic for values which never reach durable operations, their identifiers, the decisions which select them, or the body's result.
