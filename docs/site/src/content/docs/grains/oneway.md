---
title: One-way grain calls
description: Use one-way requests for best-effort Orleans notifications.
ms.date: 08/02/2026
ms.topic: concept-article
---

# One-way grain calls

A regular grain call returns a task that completes when Orleans receives the method's response. The task carries a return value or exception and tells the caller that the request finished.

A method marked with <xref:Orleans.Concurrency.OneWayAttribute> returns to the caller after Orleans accepts the request for sending. The caller receives no completion signal, result, or exception:

:::code language="csharp" source="../snippets/compiled/Grains/ServicesAndObserversSnippets.cs" id="one_way_audit":::
One-way methods must return <xref:System.Threading.Tasks.Task> or <xref:System.Threading.Tasks.ValueTask>, not their generic forms.

## Delivery semantics

One-way calls are best effort:

- Completion of the returned task doesn't mean the target received or processed the request.
- Exceptions thrown by the target aren't returned to the caller.
- The caller can't distinguish successful processing from message loss or target failure.
- Cancellation and response timeout semantics don't provide useful completion guarantees because there is no response.

Use one-way calls only for notifications that are safe to lose and where the result isn't needed. Telemetry hints, cache invalidation hints, or redundant status signals can fit this model.

Don't use one-way calls for state changes that require confirmation, financial operations, workflow transitions, or any operation the caller might need to retry. Prefer a regular request-response call, a durable queue, or an Orleans streaming provider when delivery and recovery matter.

One-way calls can reduce response-message overhead, but treat them as an advanced optimization. Measure before replacing regular calls.
