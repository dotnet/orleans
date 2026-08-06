---
title: Stateless worker grains
description: Scale location-transparent worker pools with Orleans.
ms.date: 08/02/2026
ms.topic: concept-article
---

# Stateless worker grains

A normal grain identity has at most one activation in the cluster. A stateless worker identity can have multiple activations, allowing Orleans to scale independent work across compatible silos.

Apply <xref:Orleans.Concurrency.StatelessWorkerAttribute> to the implementation:

```csharp
public interface IImageWorker : IGrainWithStringKey
{
    Task<byte[]> Resize(byte[] image, int width);
}

[StatelessWorker]
public sealed class ImageWorkerGrain : Grain, IImageWorker
{
    public Task<byte[]> Resize(byte[] image, int width)
    {
        return Task.FromResult(image);
    }
}
```

Call it like any other grain:

```csharp
IImageWorker worker =
    grainFactory.GetGrain<IImageWorker>("default");

byte[] resized = await worker.Resize(image, width: 320);
```

The key identifies a worker pool, not an individual activation. Consecutive calls to the same reference can run on different activations.

## Scaling behavior

Orleans prefers a local compatible activation. If all local activations are busy and the per-silo limit hasn't been reached, Orleans can create another. The default maximum is `Environment.ProcessorCount` activations per silo.

Set a limit explicitly:

```csharp
[StatelessWorker(maxLocalWorkers: 4)]
public sealed class ImageWorkerGrain : Grain, IImageWorker
{
}
```

Idle workers are removed by default. The two-argument attribute constructor can disable idle-worker removal for specialized workloads.

## State and concurrency

"Stateless" means activations aren't individually addressable and no single activation owns authoritative state for the key. A worker can keep caches or other local state, but that state isn't coordinated with other activations and can disappear at any time.

Stateless workers are non-reentrant by default. Add <xref:Orleans.Concurrency.ReentrantAttribute> only if their implementation is safe for request interleaving.

Good uses include CPU-bound transformations, local pre-aggregation, protocol adaptation, and replicated read caches. Don't use stateless workers for entity state that requires single-writer consistency.

Stateless worker activations don't participate in grain migration.
