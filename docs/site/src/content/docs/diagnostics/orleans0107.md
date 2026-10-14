---
title: "ORLEANS0107: Use durable operation names without '$'"
description: Understand ORLEANS0107, which reports durable task code which names a durable operation with '$'.
ms.date: 10/13/2026
ms.topic: reference
---

# ORLEANS0107: Use durable operation names without '$'

| Property | Value |
| --- | --- |
| Severity | Error |
| Category | Usage |
| Code fix | None |

## Cause

A constant name passed to `WithId` or `DurableTask.Once` contains `$`.

## Rule description

Generated identifiers contain `$`, for example `$child-1`, `$once:pay`, and `reserve$2`, the second occurrence of the name `reserve` in a run. Names without `$` never collide with generated identifiers, and the host rejects names which contain it.

For the replay model, see [Writing replay-safe workflows](https://github.com/dotnet/orleans/blob/main/src/Orleans.DurableTasks.Abstractions/README.md#writing-replay-safe-workflows).

## How to fix violations

Remove `$` from the name.

## When to suppress

None. The host rejects the name at run time.
