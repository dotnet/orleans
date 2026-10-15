---
title: Grain request scheduling
description: Understand Orleans turn-based execution, interleaving, and reentrancy.
ms.date: 10/15/2026
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
`ReadOnly` is a scheduling promise. Don't mutate grain state from a read-only method.

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

To run work from another thread under the activation's scheduling, see [Run background work as a grain request](#run-background-work-as-a-grain-request).

Use <xref:System.Threading.Tasks.Task.Run*> only to isolate unavoidable synchronous blocking or CPU work from the Orleans scheduler. Don't access grain state from the thread-pool delegate. Await it and update grain state after execution resumes on the activation scheduler. See [External tasks and grains](external-tasks-and-grains.md).

## Run background work as a grain request

<xref:Orleans.GrainBaseExtensions.RunRequestAsync*> runs a callback as a request on the activation. Call it from any thread, such as a stream, messaging, or library callback, to apply results to grain state under the same scheduling as grain calls:

:::code language="csharp" source="../snippets/compiled/Grains/RequestsAndVersioningSnippets.cs" id="run_request_from_callback":::
With the default <xref:Orleans.Runtime.LocalRequestOptions>, the request:

- Follows the grain's reentrancy rules and queues behind the current request on a non-reentrant grain.
- Passes through incoming grain call filters, which see it as a method of `IGrainRequestInvoker` with no arguments.
- Starts a new call chain. <xref:Orleans.Runtime.RequestContext> values from the calling thread don't flow into the callback.
- Restarts the activation's idle collection period.
- Bypasses <xref:Orleans.Configuration.SiloMessagingOptions.MaxEnqueuedRequestsHardLimit>.

The callback can be synchronous, as in this example, or return a `Task` or `Task<TResult>`. The returned task completes with the callback's result or exception. The callback receives the cancellation token passed to `RunRequestAsync`, and cancelling it while the request is queued cancels the request. If the activation deactivates before the request runs, the task faults with <xref:Orleans.Runtime.OrleansMessageRejectionException>.

Pass the grain and other values through the `state` argument and use a `static` lambda, so the call doesn't allocate a closure. On a non-reentrant grain, awaiting the returned task from another request on the same activation deadlocks, because the queued request waits for the current one to complete.

### Configure the request

Pass <xref:Orleans.Runtime.LocalRequestOptions> to change how the request is scheduled. Set <xref:Orleans.Runtime.LocalRequestOptions.KeepAlive> to `false` for frequent background work that shouldn't keep an otherwise idle activation in memory. Set <xref:Orleans.Runtime.LocalRequestOptions.Interleave> to `true` to run the request between the turns of other requests, as with an `[AlwaysInterleave]` method:

:::code language="csharp" source="../snippets/compiled/Grains/RequestsAndVersioningSnippets.cs" id="run_request_options":::
Set <xref:Orleans.Runtime.LocalRequestOptions.EnforceQueueLimit> to `true` to apply backpressure to a producer. The request then counts against <xref:Orleans.Configuration.SiloMessagingOptions.MaxEnqueuedRequestsHardLimit>, and the returned task faults with <xref:Orleans.Runtime.LimitExceededException> when the activation's queue is full:

:::code language="csharp" source="../snippets/compiled/Grains/RequestsAndVersioningSnippets.cs" id="run_request_queue_limit":::
### Name the request

Call filters, logs, and traces observe the request as `IGrainRequestInvoker.{methodName}`, and tracing names its activity `IGrainRequestInvoker/{methodName}`. When the callback is a method group, such as `this.RunRequestAsync(RefreshAsync)`, the method's name is used. Otherwise, the request is named `RunRequestAsync`. Pass `methodName` to choose the name:

:::code language="csharp" source="../snippets/compiled/Grains/RequestsAndVersioningSnippets.cs" id="run_request_named":::
Call filters read the name through the call context:

:::code language="csharp" source="../snippets/compiled/Grains/RequestsAndVersioningSnippets.cs" id="run_request_filter":::
### Deliver a custom request

<xref:Orleans.Runtime.IGrainContext.ReceiveLocalRequest*> delivers any <xref:Orleans.Serialization.Invocation.IInvokable> to the activation with the same scheduling as `RunRequestAsync`. Call filters observe the request's own interface and method names. The activation completes the supplied <xref:Orleans.Serialization.Invocation.IResponseCompletionSource> with the request's response, or with an exception if the activation rejects the request:

:::code language="csharp" source="../snippets/compiled/Grains/RequestsAndVersioningSnippets.cs" id="receive_local_request":::
