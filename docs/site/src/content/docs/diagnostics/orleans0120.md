---
title: "ORLEANS0120: Name durable operations whose position can change"
description: Understand ORLEANS0120, which reports an unnamed durable operation in a loop, in a condition, or after another unnamed operation, whose generated identifier can shift, and fix it with WithId.
ms.date: 10/17/2026
ms.topic: reference
---

# ORLEANS0120: Name durable operations whose position can change

| Property | Value |
| --- | --- |
| Severity | Info |
| Category | Usage |
| Code fix | Available |

## Cause

A durable task body starts an operation without a name, and the operation is in a loop or a condition, or follows another unnamed operation of its kind in the body:

:::code source="snippets/durable-tasks/Orleans0120.cs" id="orleans0120_violation" language="csharp":::

The rule covers awaited, scheduled, and posted children without `WithId`, including detached starts and delays; awaited combinators, such as `WhenAll`, `WhenAny`, `Race`, and timeouts; and `DurableTask.RunInline` effects without a name. A signal's `WaitAsync` takes no identifier, and a combinator's inputs are identified by their index, so neither is reported. The first unnamed operation of each kind in a body is reported only when it's in a loop or a condition.

## Rule description

An unnamed operation takes the execution's next generated identifier. Children, effects, and detached starts share one count, `$child-1`, `$child-2`, and so on, and awaited combinators share another, such as `$when-all-1` and `$when-any-2`. A replay returns each recorded outcome to the operation with the same identifier, so a generated identifier is stable only while the unnamed operations before it run in the same number and order.

A loop or a condition changes that number from one execution to another, and a deployment which adds, removes, or reorders an unnamed operation changes it for the executions which are pending. In the example, the express branch and the number of items decide which identifier each price and the charge receives; a replay after a deployment which adds a step before the loop returns the recorded results to the wrong operations, or diverges from the recorded execution, which keeps the task pending until code which matches its record replays it.

For identifiers, see [Durable task identity and references](../grains/durable-tasks-identity.md), and for deployments, see [Version durable tasks](../grains/durable-tasks-versioning.md).

## How to fix violations

Name the operation with `WithId`, and pass a name to `DurableTask.RunInline`. A name identifies the operation by its call site, and a call site which a loop repeats keeps one name, whose later occurrences the runtime numbers, `price`, `price$2`, and so on:

:::code source="snippets/durable-tasks/Orleans0120.cs" id="orleans0120_fix" language="csharp":::

The code fix adds `WithId` with a name derived from the method which defines the operation, such as `charge` for `ChargeAsync`, and a suffix such as `charge-2` when the body already uses the name. It doesn't name `DurableTask.RunInline` effects. Fix all occurrences in a document, project, or solution at once.

A deployment which names operations of pending executions changes their identifiers, so name operations before executions record them, or branch the change on `DurableTask.GetVersion`.

## When to suppress

Suppress the diagnostic for a body whose unnamed operations run in a fixed order, such as a short sequence of steps which no deployment changes while executions are pending.
