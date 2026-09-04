---
title: Response streaming with IAsyncEnumerable
description: Stream a grain call's response incrementally using IAsyncEnumerable<T>.
ms.date: 09/04/2026
ms.topic: concept-article
---

# Response streaming with IAsyncEnumerable

**Response streaming** lets a grain method return <xref:System.Collections.Generic.IAsyncEnumerable`1> so one caller can consume a logically single grain call's response incrementally. The caller addresses a grain as for any other grain call, and then pulls results as they're produced.

Use response streaming for a query or command whose results are naturally incremental. A response stream doesn't create a durable subscription, retain results, or multicast them to other consumers. For those capabilities, consider [Orleans Streams](../streaming/index.md).

## Define and implement a response-streaming method

Declare <xref:System.Collections.Generic.IAsyncEnumerable`1> directly on the grain interface. A cancellation token is optional, as with other grain methods:

:::code language="csharp" source="snippets/async-enumerable-results/StreamingGrain.cs" id="streaming_contract":::

An async iterator can produce each result with `yield return`. Apply <xref:System.Runtime.CompilerServices.EnumeratorCancellationAttribute> so the iterator observes cancellation requested while the caller enumerates it:

:::code language="csharp" source="snippets/async-enumerable-results/StreamingGrain.cs" id="streaming_implementation":::

## Consume a streamed response

Use `await foreach` to process each result. The response stream starts when the caller requests the first element, not when the grain method returns the enumerable:

:::code language="csharp" source="snippets/async-enumerable-results/StreamingConsumer.cs" id="consume_stream":::

Leaving an `await foreach` loop disposes its enumerator, including when the loop exits with `break` or an exception.

## Control response batching

Orleans batches synchronously available elements to reduce network round trips, up to 100 elements by default. Use <xref:Orleans.Runtime.AsyncEnumerableExtensions.WithBatchSize*> to change that limit:

:::code language="csharp" source="snippets/async-enumerable-results/StreamingConsumer.cs" id="configure_batch_size":::

Call `WithBatchSize` directly on the value returned by the grain method and before wrappers such as <xref:System.Threading.Tasks.TaskAsyncEnumerableExtensions.WithCancellation*>. After another operator wraps the enumerable, `WithBatchSize` has no Orleans request to configure and has no effect. A batch size of `1` sends one element per request.

Batching doesn't cause Orleans to read an unbounded number of elements ahead. The caller's next `MoveNextAsync` request drives production, and a batch contains only elements that become synchronously available, up to the configured limit.

## Cancel response streaming

Pass the caller's token to the grain method either as a method parameter or through `WithCancellation` (or both), so cancellation stops the async enumerator. Orleans links distinct tokens so cancellation of either stops the enumeration. Call `WithBatchSize` before `WithCancellation`:

:::code language="csharp" source="snippets/async-enumerable-results/StreamingConsumer.cs" id="cancel_stream":::

Cancellation is cooperative and surfaces to the caller as <xref:System.OperationCanceledException>. The response-streaming method must observe its token and pass it to cancellation-aware operations. See [Cancel Orleans grain calls](cancellation-tokens.md) for delivery and failure semantics.

## Handle an interrupted response stream

An exception thrown while producing the response stream propagates to the caller with its original exception type. The caller instead receives <xref:Orleans.Runtime.EnumerationAbortedException> if the grain deactivates during enumeration or the silo removes an enumerator which the caller left idle:

:::code language="csharp" source="snippets/async-enumerable-results/StreamingConsumer.cs" id="handle_interruption":::

Idle-enumerator cleanup runs periodically using <xref:Orleans.Configuration.MessagingOptions.ResponseTimeout> as its interval. Don't hold an enumerator open while doing unrelated long-running work. If processing an element can take a long time, decouple that work from pulling the next element or use a messaging abstraction with a lifetime independent of one grain call.

## Choose between response streaming and Orleans Streams

| Concern | Response streaming with `IAsyncEnumerable<T>` | Orleans Streams |
|---|---|---|
| Communication shape | Logically one grain call, one producer, and one caller | Multicast pub/sub with independent producers and subscribers |
| Lifetime | One live enumeration, ending on completion, disposal, cancellation, deactivation, or idle cleanup | Independent of any one grain call; subscriptions can survive activation changes |
| Flow control | Pull-based; `MoveNextAsync` drives production, with bounded batching | Provider-dependent delivery and buffering |
| Persistence and replay | None | Optional and provider-dependent |
| Best fit | Progressively return one command or query result | Publish events to multiple or long-lived subscriptions |

See [Choose an Orleans messaging abstraction](../streaming/streams-why.md) for observers, broadcast channels, and other alternatives.
