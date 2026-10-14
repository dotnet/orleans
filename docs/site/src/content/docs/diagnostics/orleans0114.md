---
title: "ORLEANS0114: Derive a signal name for each repeated wait"
description: Understand ORLEANS0114, which reports durable task code which waits for one signal name repeatedly.
ms.date: 10/13/2026
ms.topic: reference
---

# ORLEANS0114: Derive a signal name for each repeated wait

| Property | Value |
| --- | --- |
| Severity | Info |
| Category | Usage |
| Code fix | None |

## Cause

A durable task body waits for the same `DurableSignal<T>` inside a loop.

## Rule description

Each signal name of a task holds the first value which a sender delivers, so every wait for that name returns the same value. A loop which waits once per iteration derives a distinct signal for each iteration.

For the replay model, see [Replay and determinism](../grains/durable-tasks.md#replay-and-determinism).

## How to fix violations

Wait for `signal.For(name)`, with a name which identifies the iteration, and send to the same derived signal. For signal semantics, see [Signals](../grains/durable-tasks.md#signals).

## When to suppress

Suppress the diagnostic when the loop waits for the same value on purpose.
