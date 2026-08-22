---
title: Stateless worker grains
description: Scale location-transparent worker pools with Orleans.
ms.date: 08/22/2026
ms.topic: concept-article
---

# Stateless worker grains

A normal grain identity has at most one activation in the cluster. A stateless worker identity can have multiple activations, allowing Orleans to scale independent work across compatible silos.

Apply <xref:Orleans.Concurrency.StatelessWorkerAttribute> to the implementation:

:::code language="csharp" source="../snippets/compiled/Grains/WorkersAndTimersSnippets.cs" id="image_worker":::
Call it like any other grain:

:::code language="csharp" source="../snippets/compiled/Grains/WorkersAndTimersSnippets.cs" id="call_image_worker":::
The key identifies a worker pool, not an individual activation. Consecutive calls to the same reference can run on different activations.

## Scaling behavior

Orleans prefers a local compatible activation. If all local activations are busy and the per-silo limit hasn't been reached, Orleans can create another. The default maximum is `Environment.ProcessorCount` activations per silo.

Set a limit explicitly:

:::code language="csharp" source="../snippets/compiled/Grains/WorkersAndTimersSnippets.cs" id="limited_image_worker":::
Idle workers are removed by default. The two-argument attribute constructor can disable idle-worker removal for specialized workloads.

## State and concurrency

"Stateless" means activations aren't individually addressable and no single activation owns authoritative state for the key. A worker can keep caches or other local state; each activation manages its own copy for its lifetime.

Stateless workers are non-reentrant by default. Add <xref:Orleans.Concurrency.ReentrantAttribute> only if their implementation is safe for request interleaving.

Good uses include CPU-bound transformations, local pre-aggregation, protocol adaptation, and replicated read caches backed by an application-managed read model. Keep authoritative entity state and command ordering in a normal grain or another explicit consistency boundary. See [Scale grain reads](read-scaling.md) for the single-writer and read-model patterns.

Stateless worker activations don't participate in grain migration.
