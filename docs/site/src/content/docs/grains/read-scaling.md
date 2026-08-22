---
title: Scale grain reads
description: Choose interleaved reads or an application-managed read model for read-heavy Orleans grains.
ms.date: 08/22/2026
ms.topic: concept-article
---

# Scale grain reads

Read-heavy entities need a clear consistency boundary before they need replicas. Orleans supports two complementary patterns:

| Requirement | Pattern | Runtime outcome |
|---|---|---|
| One authoritative activation with more concurrent progress while reads await I/O | Mark query methods with <xref:Orleans.Concurrency.ReadOnlyAttribute> | Read-only requests can interleave with other read-only requests on the activation. |
| Parallel reads across multiple activations or services | Build a versioned read model and serve it through stateless workers or another query service | The command grain remains the single writer while independent readers scale across the cluster. |

Partitioning the entity into several grain keys is another option when the domain invariants can be divided by key. Each partition then has its own writer and activation.

## Interleave reads on one activation

Use a normal grain as the owner of the entity and mark query methods with <xref:Orleans.Concurrency.ReadOnlyAttribute>:

:::code language="csharp" source="../snippets/compiled/Grains/RequestsAndVersioningSnippets.cs" id="single_writer_interleaved_readers":::

The attribute is part of the grain interface because it controls request scheduling. Multiple incomplete `Get` calls can make progress together when they await the recommendation service. The activation still executes one turn at a time, so this pattern increases I/O concurrency rather than CPU parallelism.

`ReadOnly` is a promise that the method preserves grain state. The example captures an immutable, versioned snapshot before its first `await` and uses that snapshot for the rest of the response. This gives the request one coherent view even while turns from other read-only requests alternate on the activation.

The `Update` method remains an ordinary grain request and is the only operation which replaces the snapshot. Add grain persistence or another durable state mechanism when updates must survive activation loss. Await the durable write before replacing the in-memory snapshot and reporting success.

This pattern fits workloads where one activation can execute the CPU portion of the reads and the main latency comes from awaited calls. Measure the grain's request queue, call latency, and silo CPU to confirm that interleaving provides enough capacity.

## Scale reads with a read model

Use an application-managed read model when reads need parallel execution across activations:

1. A normal grain owns commands and validates the entity's invariants.
1. After committing a change, the command path publishes a versioned snapshot or event to a durable read-model pipeline.
1. A query API reads the materialized view. A <xref:Orleans.Concurrency.StatelessWorkerAttribute> grain can provide this API when location-transparent worker scaling is useful.
1. Each stateless worker reads the shared view or keeps a disposable local cache. Orleans creates worker activations according to demand and placement.

The read model defines the consistency contract. Include a monotonically increasing entity version so projections and caches can ignore older updates. Make projection updates idempotent, preserve ordering per entity, and reconcile gaps after delivery or process failures. Use an outbox or another resumable projection mechanism when committing command state and publishing the read-model update aren't one atomic operation.

For read-your-writes behavior, return the committed version from the command and wait until the read model reaches that version, or route that read to the authoritative grain. For eventual consistency, return the observed version so callers can make staleness visible where it matters.

External side effects execute on the command path, and readers consume committed data instead of replaying the write operation. Give retried command-side effects stable operation identifiers and idempotent handling. This keeps payment, messaging, and other side effects under the single writer's control.

Stateless worker activations have independent lifetimes and caches. The durable read model supplies the shared state, while the application supplies refresh, invalidation, versioning, and reconciliation. See [Stateless worker grains](stateless-worker-grains.md) for activation and scaling behavior.

## Choose the consistency boundary

Start with `[ReadOnly]` when a single activation provides enough CPU capacity and reads benefit from overlapping awaited work. Move query traffic to a read model when measurements show that the entity key remains hot or when the application already needs independently scaled projections.

Keep invariants and command ordering in the authoritative grain. Treat the read model as a versioned projection whose freshness contract is explicit to callers and operators.
