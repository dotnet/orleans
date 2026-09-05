---
title: Grain request scheduling
description: Understand Orleans turn-based execution, interleaving, and reentrancy.
ms.date: 08/02/2026
ms.topic: concept-article
---

# Grain request scheduling

Each grain activation executes one turn at a time. Orleans runs a request until it completes or reaches an incomplete `await`, then later schedules its continuation as another turn. Two turns never execute in parallel on the same activation.

By default, an activation doesn't start a different request while the current request is incomplete. This non-reentrant model makes mutable grain state easier to reason about:

:::code language="csharp" source="../snippets/compiled/Grains/RequestsAndVersioningSnippets.cs" id="serialized_counter_grain":::
Although the method yields at `await`, another request doesn't observe or change `_value` before this request completes.

## Avoid blocking

Never synchronously block on incomplete tasks from grain code. `.Result`, `.Wait()`, `WaitAll`, and `GetAwaiter().GetResult()` can deadlock an activation and consume thread-pool threads. Use `await`.

Normal `await` resumes grain code on the activation scheduler. Don't use `ConfigureAwait(false)` in grain methods because the continuation can escape the grain scheduler. General-purpose libraries can use it internally; grain code returns to the grain scheduler when it awaits the library normally.

## Interleaving and reentrancy

Interleaving lets Orleans start or resume another request while an earlier request is incomplete. The activation is still single-threaded, but turns from different requests can alternate. Re-check assumptions about mutable state after every `await`.

| Mechanism | Scope |
|---|---|
| <xref:Orleans.Concurrency.ReentrantAttribute> | All requests to the grain class can interleave. |
| <xref:Orleans.Concurrency.AlwaysInterleaveAttribute> | The marked interface method can interleave with any request. |
| <xref:Orleans.Concurrency.ReadOnlyAttribute> | Marked read-only methods can interleave with other read-only methods. |
| <xref:Orleans.Concurrency.MayInterleaveAttribute> | A predicate inspects each request. |
| <xref:Orleans.Runtime.RequestContext.AllowCallChainReentrancy> | A scoped call chain can call back into an activation already in that chain. |

Use the narrowest mechanism that solves the problem. Reentrancy can improve throughput for I/O-heavy grains and prevent cyclic call deadlocks, but it also allows state to change between turns.

### Reentrant grains

:::code language="csharp" source="../snippets/compiled/Grains/RequestsAndVersioningSnippets.cs" id="reentrant_catalog_grain":::
Code in different requests doesn't run simultaneously, but multiple incomplete calls can make progress by alternating turns.

### Method-level interleaving

:::code language="csharp" source="../snippets/compiled/Grains/RequestsAndVersioningSnippets.cs" id="interleaving_method_attributes":::
`ReadOnly` is a scheduling promise. Active read-only requests can interleave with one another and collectively exclude writable requests until every read-only request completes, unless another interleaving policy permits overlap. Keep grain state unchanged from a read-only method.

### Predicate-based interleaving

`MayInterleave` names a predicate that accepts <xref:Orleans.Serialization.Invocation.IInvokable>. Use its accessor methods; there is no `Arguments` property:

:::code language="csharp" source="../snippets/compiled/Grains/RequestsAndVersioningSnippets.cs" id="may_interleave_grain":::
Keep predicates deterministic, fast, and side-effect free.

### Call-chain reentrancy

Use call-chain reentrancy when a known call path must call back into the initiating activation:

:::code language="csharp" source="../snippets/compiled/Grains/RequestsAndVersioningSnippets.cs" id="call_chain_reentrancy":::
The scope permits callbacks associated with that call chain until it is disposed. It is narrower than marking the entire grain reentrant.

## Timers and external work

Grain timer callbacks participate in scheduling. Callbacks don't interleave by default; configure `GrainTimerCreationOptions.Interleave` when intentional.

Use <xref:System.Threading.Tasks.Task.Run*> only to isolate unavoidable synchronous blocking or CPU work from the Orleans scheduler. Don't access grain state from the thread-pool delegate. Await it and update grain state after execution resumes on the activation scheduler. See [External tasks and grains](external-tasks-and-grains.md).
