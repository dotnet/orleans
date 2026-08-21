---
title: Model collections of grains
description: Model bounded catalogs, partitioned indexes, query stores, paging, and bulk operations over Orleans grains.
ms.date: 08/21/2026
ms.topic: concept-article
---

# Model collections of grains

Orleans virtualizes individually addressed grains. Applications define collection membership explicitly and choose the storage and consistency model which matches each collection's access patterns.

A grain key identifies a logical grain, and <xref:Orleans.IGrainFactory.GetGrain*> can create a reference for any valid key. Therefore, a domain catalog or query store determines which keys currently represent application members. The runtime grain directory tracks active grain locations for request routing; application catalogs retain membership across deactivation and cluster restarts.

## Choose a collection shape

| Requirement | Model |
|---|---|
| A bounded aggregate owned by one domain entity | Store member keys in the owning grain's state |
| Lookup by a known partition key, such as tenant or category | Use one registry or index grain per partition |
| A large hash-partitioned map or set | Route each member to a shard using an application-defined stable hash |
| Ordered paging or range scans | Use range-partitioned index grains or an external query store |
| Ad hoc filtering, sorting, reporting, or full-text search | Query an external read model and resolve the returned grain keys |
| Incremental delivery of one query result | Return <xref:System.Collections.Generic.IAsyncEnumerable`1> from the registry, index, or query grain |

Collection state usually stores grain keys and the minimum fields needed for routing, filtering, or ordering. Resolve typed [grain references](grain-references.md) from those keys when invoking members. Persist a grain reference when retaining its interface relationship is useful.

## Choose grains or stored values

Model each member as a grain when it has an independent identity, behavior, concurrency boundary, or lifecycle. The collection retains member keys and routes operations to those grains.

Store ordinary serializable objects in an owning grain's state when they form a bounded aggregate whose operations run through that owner. This provides one activation and persistence boundary for the collection and its values. For large collections of data-oriented records, an external database provides scalable storage and query execution; grains can coordinate domain operations using the returned record identifiers.

## Keep bounded catalogs in one grain

An owning grain or registry grain is a direct model for a collection with a known operational bound. A non-reentrant owner processes one request at a time, and one awaited persistent state write provides an optimistic-concurrency boundary.

Return pages or streamed results instead of returning the complete collection. Size the bound using:

- The storage provider's record-size and request limits.
- The activation memory required to deserialize and index the collection.
- The time required to read, write, and serialize the full state record.
- The response size and timeout budget for callers.

When membership can grow continuously, partition the catalog before those limits become an operational constraint.

## Partition membership and indexes

Partitioned registry grains distribute collection state and request load. Select a routing rule which every caller can reproduce:

- **Domain partitioning** uses a stable value such as tenant, region, or product category.
- **Hash partitioning** spreads point lookups and unordered membership evenly.
- **Range partitioning** supports ordered traversal and range queries.

Each membership entry has one owning shard. Keep the shard count and hash algorithm stable, or include a routing version in the shard identity. A resharding workflow can write new-version entries, switch readers, and then retire old-version entries after reconciliation.

For a query spanning known shards, use a coordinator service or grain to issue calls with bounded concurrency and merge the results. Apply limits for shard fan-out, returned items, elapsed time, and per-call concurrency. This keeps one query from producing an unbounded number of grain calls.

## Define the consistency boundary

Collection membership and member state often occupy different records. Choose the authority and completion contract before implementing updates:

| Update model | Completion contract |
|---|---|
| Member data and membership share one grain state record | The awaited state write durably records both changes |
| Member and index use Orleans transactional state | An [Orleans transaction](transactions.md) commits both changes atomically |
| One external database owns member data and indexes | A database transaction commits the data and queryable index together |
| Grain state is authoritative and a separate read model is updated asynchronously | A durable event or outbox drives an idempotent projector; queries expose the documented projection lag |

For uniqueness, route every candidate value to one deterministic index shard. That shard durably reserves the value for one member key before the operation reports success. Include an operation identifier so retries reach the same outcome.

For asynchronous indexes, retain enough information to replay updates and reconcile the index against its authority. Metrics should report projection lag, failed updates, and reconciliation differences.

## Query and page results

Use a database or search service as the query authority when the workload needs ad hoc predicates, sorting, aggregation, or reporting. Query for grain keys and the fields needed to order the result, then resolve grain references for commands or current domain behavior. The [grain persistence](grain-persistence/index.md) abstraction reads and writes records by grain identity; a grain or application service can access a query-oriented database model directly.

Use continuation tokens for resumable paging. A token can carry the routing version, shard or partition, last ordering value, and last grain key. Treat tokens as opaque application contracts and version their encoded form.

[Response streaming](response-streaming.md) progressively delivers one live query result with pull-based flow control. A page with a continuation token provides a durable resume boundary when a caller must continue after cancellation, timeout, grain deactivation, or process failure.

## Examples

- The [Journaled Todo List registry grain](https://github.com/dotnet/orleans/blob/main/samples/JournaledTodoList/JournaledTodoList.WebApp/Grains/TodoListRegistryGrain.cs) maintains a bounded catalog of list identities and display names.
- The [Shopping Cart inventory grains](https://github.com/dotnet/orleans/blob/main/samples/ShoppingCart/Grains/InventoryGrain.cs) partition product membership by category, and the [inventory service](https://github.com/dotnet/orleans/blob/main/samples/ShoppingCart/Silo/Services/InventoryService.cs) merges the known partitions.
- The [Azure App Service inventory grain](https://github.com/dotnet/orleans/blob/main/samples/Deployment/AzureAppService/Grains/InventoryGrain.cs) returns inventory incrementally using response streaming.
