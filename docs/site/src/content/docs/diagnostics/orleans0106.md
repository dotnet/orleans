---
title: "ORLEANS0106: Run each durable task definition once"
description: Understand ORLEANS0106, which reports durable task code which runs a single-use durable task definition more than once.
ms.date: 10/13/2026
ms.topic: reference
---

# ORLEANS0106: Run each durable task definition once

| Property | Value |
| --- | --- |
| Severity | Warning |
| Category | Usage |
| Code fix | None |

## Cause

A local variable holds the definition which an `async DurableTask` method returned, and the code uses the variable more than once, or uses it in a loop which doesn't declare it.

## Rule description

An `async DurableTask` method returns a single-use definition. Calling the method again creates a definition for another run.

For the replay model, see [Writing replay-safe workflows](https://github.com/dotnet/orleans/blob/main/src/Orleans.DurableTasks.Abstractions/README.md#writing-replay-safe-workflows).

## How to fix violations

Call the method once for each run, for example inside the loop.

## When to suppress

None.
