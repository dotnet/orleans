---
title: "ORLEANS0223: Assign new values to grain state instead of changing them in place"
description: Understand ORLEANS0223, which reports durable task code which changes an object that grain state holds in place, and make replay-safe updates to durable values, durable collections, and snapshot-based persistent state.
ms.date: 10/20/2026
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

Durable values and collections record each change which passes through their APIs, such as an assignment to `IDurableValue<T>.Value` or to an entry of a durable dictionary. The change commits with the grain's next journal write, and the task's access log records the operation, so a replay returns its outcome.

A change made in place to an object which a durable value or collection holds bypasses the API. The object in memory holds the change, and no record holds it until another change writes the whole value. In the example, the task completes and its result commits while the journal holds the wishlist without the new item. An activation which recovers the grain from the journal loses the item, and a replay of the body makes the change again.

`IPersistentState<T>` uses snapshot-based persistence: each journal write serializes the current state object, including assignments and in-place changes. Replayed code reads and changes the object again, so the recorded effect, step, or synchronous tail which encloses an access makes it replay-safe. The rule also recommends immutable values and assignment for this state in durable task code.

Value types, records whose members are init-only, and immutable collections change through assignment, which the API records, so the rule reports none of them.

For replay-safe state, see [Replay-safety rules](../grains/durable-tasks-replay-rules.md#state-values-and-external-work). For operation-recorded state, see [Durable state](../grains/journaling/durable-state.md).

## How to fix violations

**Store immutable values, and assign a new value.** For durable values and collection entries, a record with an immutable collection returns a changed copy, and the assignment records it:

:::code source="snippets/durable-tasks/Orleans0223.cs" id="orleans0223_fix" language="csharp":::

For snapshot-based persistent state, read and change the value in a [recorded effect](../grains/durable-tasks.md#recorded-effects-local-steps-and-calls-to-other-grains), a step, or the body's synchronous tail. Use the same boundary when assigning a new immutable value.

## When to suppress

Suppress the diagnostic for a member which the state holds in memory only, such as a cache which the serializer skips.
