---
title: "ORLEANS0114: Derive a signal name for each repeated wait"
description: Understand ORLEANS0114, which reports a wait for one durable signal name inside a loop, and fix it with DurableSignal<T>.For.
ms.date: 10/15/2026
ms.topic: reference
---

# ORLEANS0114: Derive a signal name for each repeated wait

| Property | Value |
| --- | --- |
| Severity | Info |
| Category | Usage |
| Code fix | None |

## Cause

A durable task body calls `WaitAsync` or `TryWaitAsync` on a `DurableSignal<T>` inside a loop, and the signal isn't derived with `For`:

:::code source="snippets/durable-tasks/Orleans0114.cs" id="orleans0114_violation" language="csharp":::

## Rule description

A task records the first value which a sender delivers for each signal name, and returns that value to every wait for the name, including waits during replay. A later value for the same name is rejected as a conflict when it differs, and reported as a duplicate when it's equal.

In the example, the first review rejects the contract. Every later round's wait returns that rejection at once, so the negotiation ends after one review, and the reviewers' later decisions are rejected.

For signals, see [Signals](../grains/durable-tasks.md#signals).

## How to fix violations

Derive a name for each occurrence with `DurableSignal<T>.For`, from a value which identifies it, such as the round number. Senders deliver each decision to the same derived signal, such as `ContractGrain.Reviewed.For("round-2")`:

:::code source="snippets/durable-tasks/Orleans0114.cs" id="orleans0114_fix" language="csharp":::

## When to suppress

Suppress the diagnostic for a loop which waits for the same one-time event in each iteration on purpose, for example one which rechecks a condition after the event arrived.
