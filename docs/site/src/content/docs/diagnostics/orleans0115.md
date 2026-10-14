---
title: "ORLEANS0115: Log through a replay-safe logger"
description: Understand ORLEANS0115, which reports durable task code which logs through a logger which replays.
ms.date: 10/13/2026
ms.topic: reference
---

# ORLEANS0115: Log through a replay-safe logger

| Property | Value |
| --- | --- |
| Severity | Info |
| Category | Usage |
| Code fix | None |

## Cause

A durable task body logs, at a point from which it can still reach a durable operation, through a logger which no `ReplaySafe()` call created.

## Rule description

A replay re-runs this code and logs the event again. A logger which `ReplaySafe()` returns skips events while the task replays operations which it has already recorded, so each event is logged once.

For the replay model, see [Replay and determinism](../grains/durable-tasks.md#replay-and-determinism).

## How to fix violations

Log through `logger.ReplaySafe()`, or through a field, property, or local which a `ReplaySafe()` call initializes.

## When to suppress

Suppress the diagnostic for events which the application expects once per run, such as replay diagnostics.
