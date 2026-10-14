---
title: "ORLEANS0108: Give each durable operation call site its own name"
description: Understand ORLEANS0108, which reports durable task code which repeats a durable operation name at another call site.
ms.date: 10/13/2026
ms.topic: reference
---

# ORLEANS0108: Give each durable operation call site its own name

| Property | Value |
| --- | --- |
| Severity | Warning |
| Category | Usage |
| Code fix | None |

## Cause

Two call sites in one durable task body pass the same constant name to `WithId`, or to `DurableTask.Once`, and one run can reach both of them.

## Rule description

A name which one run repeats identifies its later occurrences by their order, as `name$2`, `name$3`, and so on. When a deployment reorders the call sites, or a run skips one of them, each recorded identifier refers to another call site, and the replay diverges. Distinct names keep each call site's identifier stable.

A call site in a loop repeats its name in each iteration, which identifies iterations by their order.

For the replay model, see [Replay and determinism](../grains/durable-tasks.md#replay-and-determinism).

## How to fix violations

Give each call site its own name.

## When to suppress

Suppress the diagnostic when the call sites are alternatives which one run never reaches together, and the analyzer can't prove it.
