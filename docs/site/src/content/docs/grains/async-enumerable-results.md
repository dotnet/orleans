---
title: Stream grain results with IAsyncEnumerable
description: Return IAsyncEnumerable<T> from an Orleans grain method to stream one call's results.
ms.date: 08/08/2026
ms.topic: concept-article
---

# Stream grain results with IAsyncEnumerable

A grain method can return <xref:System.Collections.Generic.IAsyncEnumerable`1> to deliver a sequence to one caller without materializing the full result first. The caller addresses a grain as for any other grain call, and then pulls results as they're produced.

Use this pattern for a query or command whose results are naturally incremental. It remains a single live grain call: it doesn't create a durable subscription, retain results, or multicast them to other consumers. For those capabilities, consider [Orleans streams](../streaming/index.md).

## Define and implement a streaming method

Declare <xref:System.Collections.Generic.IAsyncEnumerable`1> directly on the grain interface. A cancellation token is optional, as with other grain methods:

:::code language="csharp" source="snippets/async-enumerable-results/StreamingGrain.cs" id="streaming_contract":::

An async iterator can produce each result with `yield return`. Apply <xref:System.Runtime.CompilerServices.EnumeratorCancellationAttribute> so the iterator observes cancellation requested while the caller enumerates it:

:::code language="csharp" source="snippets/async-enumerable-results/StreamingGrain.cs" id="streaming_implementation":::

## Consume the results

Use `await foreach` to process each result. The remote enumeration starts when the caller requests the first element, not when the grain method returns the enumerable:

:::code language="csharp" source="snippets/async-enumerable-results/StreamingConsumer.cs" id="consume_stream":::

Leaving an `await foreach` loop disposes its enumerator, including when the loop exits with `break` or an exception.

## Control batching

Orleans batches synchronously available elements to reduce network round trips, up to 100 elements by default. Use <xref:Orleans.Runtime.AsyncEnumerableExtensions.WithBatchSize*> to change that limit:

:::code language="csharp" source="snippets/async-enumerable-results/StreamingConsumer.cs" id="configure_batch_size":::

Call `WithBatchSize` directly on the value returned by the grain method and before wrappers such as <xref:System.Threading.Tasks.TaskAsyncEnumerableExtensions.WithCancellation*>. After another operator wraps the enumerable, `WithBatchSize` has no Orleans request to configure and has no effect. A batch size of `1` sends one element per request.

Batching doesn't cause Orleans to read an unbounded number of elements ahead. The caller's next `MoveNextAsync` request drives production, and a batch contains only elements that become synchronously available, up to the configured limit.

## Cancel enumeration

Supply a token as a grain method argument, through `WithCancellation`, or both. Orleans links distinct tokens so cancellation of either stops the enumeration. Call `WithBatchSize` first when using both extensions:

:::code language="csharp" source="snippets/async-enumerable-results/StreamingConsumer.cs" id="cancel_stream":::

Cancellation is cooperative and surfaces to the caller as <xref:System.OperationCanceledException>. The iterator must observe its token and pass it to cancellation-aware operations. See [Cancel Orleans grain calls](cancellation-tokens.md) for delivery and failure semantics.

## Handle interrupted enumeration

An exception thrown by the iterator propagates to the caller with its original exception type. The caller instead receives <xref:Orleans.Runtime.EnumerationAbortedException> if the grain deactivates during enumeration or the silo removes an enumerator which the caller left idle:

:::code language="csharp" source="snippets/async-enumerable-results/StreamingConsumer.cs" id="handle_interruption":::

Idle-enumerator cleanup runs periodically using <xref:Orleans.Configuration.MessagingOptions.ResponseTimeout> as its interval. Don't hold an enumerator open while doing unrelated long-running work. If processing an element can take a long time, decouple that work from pulling the next element or use a messaging abstraction with a lifetime independent of one grain call.

## Choose between IAsyncEnumerable and Orleans streams

| Concern | `IAsyncEnumerable<T>` grain method | Orleans stream |
|---|---|---|
| Communication shape | One grain call, one producer, and one caller | Multicast pub/sub with independent producers and subscribers |
| Lifetime | One live enumeration, ending on completion, disposal, cancellation, deactivation, or idle cleanup | Independent of any one grain call; subscriptions can survive activation changes |
| Flow control | Pull-based; `MoveNextAsync` drives production, with bounded batching | Provider-dependent delivery and buffering |
| Persistence and replay | None | Optional and provider-dependent |
| Best fit | Progressively return one command or query result | Publish events to multiple or long-lived subscriptions |

See [Choose an Orleans messaging abstraction](../streaming/streams-why.md) for observers, broadcast channels, and other alternatives.
