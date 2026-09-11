---
title: Scale grain reads
description: Choose interleaved reads or an application-managed read model for read-heavy Orleans grains.
ms.date: 09/05/2026
ms.topic: concept-article
---

# Scale grain reads

Read-heavy entities scale from an explicit consistency boundary. Orleans provides request interleaving, addressable grains, and stateless worker pools that applications can combine into these patterns:

| Requirement | Pattern | Runtime outcome |
|---|---|---|
| One authoritative activation with more concurrent progress while reads await I/O | Mark query methods with <xref:Orleans.Concurrency.ReadOnlyAttribute> | Read-only requests can interleave with other read-only requests on the activation. |
| A fixed, addressable set of read replicas | Route queries across normal reader grains with application-selected keys | Each replica has one activation, and the application controls the replica count, routing, and versioned updates. |
| Demand-based reads across multiple activations or services | Build a versioned read model and serve it through stateless workers or another query service | The command grain remains the single writer while independent readers scale across the cluster. |

Partitioning the entity into several grain keys is another option when the domain invariants can be divided by key. Each partition then has its own writer and activation.

## Interleave reads on one activation

Use a normal grain as the owner of the entity and mark query methods with <xref:Orleans.Concurrency.ReadOnlyAttribute>:

:::code language="csharp" source="../snippets/compiled/Grains/RequestsAndVersioningSnippets.cs" id="single_writer_interleaved_readers":::

The attribute is part of the grain interface because it controls request scheduling. Multiple incomplete `Get` calls can make progress together when they await the recommendation service. The activation continues to execute one turn at a time, giving I/O-heavy reads more concurrency while preserving single-threaded grain execution.

`ReadOnly` is a promise that the method preserves grain state. The example captures an immutable, versioned snapshot before its first `await` and uses that snapshot for the rest of the response. This gives the request one coherent view even while turns from other read-only requests alternate on the activation.

The `Update` method remains an ordinary grain request and is the only operation which replaces the snapshot. Add grain persistence or another durable state mechanism when updates must survive activation loss. Await the durable write before replacing the in-memory snapshot and reporting success.

This pattern fits workloads where one activation can execute the CPU portion of the reads and the main latency comes from awaited calls. Measure the grain's request queue, call latency, and silo CPU to confirm that interleaving provides enough capacity.

## Scale reads across replicas

Use an application-managed read model when reads need parallel execution across activations. A normal grain remains the authoritative command endpoint and the only component which assigns entity versions and validates invariants.

Choose a query topology based on the required scaling behavior:

- **Addressable reader grains** provide a fixed replica pool. Select stable replica keys, route each query to one key, and send every committed update or invalidation to the configured replicas. Each reader grain has one activation and can recover its view from the durable read model after activation.
- **Stateless worker grains** provide a demand-based worker pool. Each activation reads the durable view or maintains a disposable local cache. Orleans prefers compatible local workers and creates additional activations according to demand and the configured per-silo limit.
- **An external query service** provides independent storage and capacity controls. The command grain publishes committed snapshots or events, and the service materializes query-oriented views.

### Define the version contract

The read model's protocol defines its consistency guarantee:

1. The command grain assigns a monotonically increasing version and commits the state and version together.
1. The command path publishes a snapshot or event carrying that version. Use an outbox or another resumable projection mechanism to durably record publication alongside the command state.
1. Projection handlers process duplicate versions idempotently. A snapshot can replace any older snapshot. An incremental projection applies the next expected version and starts recovery when a later version reveals a gap.
1. Reader caches replace their local value only with a newer version. Update or invalidation notifications accelerate convergence; activation, cache-miss, and periodic refresh paths compare against the durable view so that replaced workers and missed notifications converge.
1. Every query returns the version it observed. This makes the freshness boundary available to callers and telemetry.

For read-your-writes behavior, return the committed version from the command as a version fence. The query path waits until its observed version reaches that fence or routes the read to the authoritative grain. Eventual-consistency paths return immediately with the observed version.

Projection handlers materialize committed state, while external side effects remain on the command path. Give retried command-side effects stable operation identifiers and idempotent handling. This keeps payment, messaging, and other effects under the single writer's ordering boundary.

Stateless worker activations have independent lifetimes and caches. The durable read model supplies the shared state, and the version protocol supplies refresh, invalidation, ordering, and reconciliation. See [Stateless worker grains](stateless-worker-grains.md) for activation and scaling behavior.

## Choose the scaling boundary

Start with `[ReadOnly]` when a single activation provides enough CPU capacity and reads benefit from overlapping awaited work. Use addressable reader grains when the application needs a known replica set. Use stateless workers or an external query service when measurements show that the entity key remains hot and readers need independent capacity.

Keep invariants and command ordering in the authoritative grain. Treat the read model as a versioned projection whose freshness contract is explicit to callers and operators.
