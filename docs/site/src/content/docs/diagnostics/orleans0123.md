---
title: "ORLEANS0123: Assign new values to grain state instead of changing them in place"
description: Understand ORLEANS0123, which reports durable task code that changes an object held by journaled or persistent grain state in place, and fix it by assigning a new immutable value.
ms.date: 10/17/2026
ms.topic: reference
---

# ORLEANS0123: Assign new values to grain state instead of changing them in place

| Property | Value |
| --- | --- |
| Severity | Warning |
| Category | Usage |
| Code fix | None |

## Cause

Code in a durable task body, including its effects and steps, changes an object which grain state holds instead of assigning a new value: it sets a member, increments it, or calls a collection method such as `Add` or `Clear` on `IDurableValue<T>.Value`, `IPersistentState<T>.State`, an entry of a durable dictionary or list, or an object which they reach:

:::code source="snippets/durable-tasks/Orleans0123.cs" id="orleans0123_violation" language="csharp":::

A local which a declaration initializes with such a value, and which no code assigns again, is followed too. Assignments to the state itself, such as `_cart.Value = cart`, changes through the state's own API, such as a durable list's `Add`, `with` expressions, and immutable collections, which return changed copies, aren't reported.

## Rule description

Journaled grain state records each change which passes through its API, such as an assignment to `IDurableValue<T>.Value` or `IPersistentState<T>.State`, or to an entry of a durable dictionary, and commits the record with the task's next journal write.

A change made in place to an object which the state holds bypasses the API. No record holds it until another change writes the whole value, so the state in memory differs from the state which the journal recovers after a deactivation. In the example, no record holds the added item: the basket in memory has it, and the basket which the grain recovers after a deactivation doesn't.

For journaled state, see [Durable state](../grains/journaling/durable-state.md).

## How to fix violations

Store immutable values, such as records and immutable collections, and assign a new value to change one:

:::code source="snippets/durable-tasks/Orleans0123.cs" id="orleans0123_fix" language="csharp":::

## When to suppress

Suppress the diagnostic for an object which grain state holds only as a cache which no journal write includes.
