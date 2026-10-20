---
title: "ORLEANS0223: Assign new values to grain state instead of changing them in place"
description: Understand ORLEANS0223, which reports durable task code which changes an object that journaled grain state holds in place, and fix it with immutable values which the code assigns.
ms.date: 10/19/2026
ms.topic: reference
---

# ORLEANS0223: Assign new values to grain state instead of changing them in place

| Property | Value |
| --- | --- |
| Severity | Warning |
| Category | Usage |
| Code fix | None |

## Cause

Durable task code changes an object which grain state holds, in place: it assigns, increments, or decrements a member or an element of the object, or calls a method which changes a collection, such as `Add` or `Clear`. The object is reached through `IDurableValue<T>.Value`, `IPersistentState<T>.State`, or an entry of a durable dictionary or list, directly or through a local which holds it:

:::code source="snippets/durable-tasks/Orleans0223.cs" id="orleans0223_violation" language="csharp":::

## Rule description

Journaled grain state records each change which passes through its API, such as an assignment to `IDurableValue<T>.Value` or `IPersistentState<T>.State`, or to an entry of a durable dictionary. The change commits with the grain's next journal write.

A change made in place to an object which the state holds bypasses the API. The object in memory holds the change, and no record holds it until another change writes the whole value. In the example, the task completes and its result commits while the journal holds the wishlist without the new item. An activation which recovers the grain from the journal loses the item.

Value types, records whose members are init-only, and immutable collections change through assignment, which the API records, so the rule reports none of them.

For replay-safe state, see [Replay-safety rules](../grains/durable-tasks-replay-rules.md#state-values-and-external-work).

## How to fix violations

**Store immutable values, and assign a new value.** A record with an immutable collection returns a changed copy, and the assignment records it:

:::code source="snippets/durable-tasks/Orleans0223.cs" id="orleans0223_fix" language="csharp":::

## When to suppress

Suppress the diagnostic for a member which the state holds in memory only, such as a cache which the serializer skips.
