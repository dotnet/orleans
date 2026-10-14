---
title: "ORLEANS0113: Return a task from asynchronous code in durable task bodies"
description: Understand ORLEANS0113, which reports durable task code which uses asynchronous void code in a durable task body.
ms.date: 10/13/2026
ms.topic: reference
---

# ORLEANS0113: Return a task from asynchronous code in durable task bodies

| Property | Value |
| --- | --- |
| Severity | Warning |
| Category | Usage |
| Code fix | None |

## Cause

A durable task body declares or passes an `async void` method, local function, or lambda.

## Rule description

A durable task body observes the operations it awaits. Asynchronous void code continues after the body has moved on, so its durable operations run outside the body's order, and its failures escape the body.

For the replay model, see [Writing replay-safe workflows](https://github.com/dotnet/orleans/blob/main/src/Orleans.DurableTasks.Abstractions/README.md#writing-replay-safe-workflows).

## How to fix violations

Return `Task` or `DurableTask` from the asynchronous code, and await it.

## When to suppress

None in durable task bodies.
