---
title: Redis journal storage
description: Configure Redis as a storage provider for experimental Orleans Journaling durable state.
ms.date: 08/21/2026
ms.topic: how-to
---

# Redis journal storage

Orleans Journaling persists durable state changes as ordered journal data which is replayed to recover in-memory durable values and collections in <xref:Orleans.Journaling.DurableGrain> grains. See the [Journaling overview](index.md) for its programming model and the [Event Sourcing comparison](../event-sourcing/index.md#event-sourcing-and-experimental-journaling) for the boundary between the two features.

The pre-release [`Microsoft.Orleans.Journaling.Redis`](https://www.nuget.org/packages/Microsoft.Orleans.Journaling.Redis) package stores journal data in [Redis](https://redis.io/docs/latest/develop/data-types/). Its APIs carry diagnostic `ORLEANSEXP005`.

## Configure Redis journal storage

Configure the provider with <xref:Orleans.Journaling.RedisJournalStorageHostingExtensions.AddRedisJournalStorage*>:

:::code language="csharp" source="./snippets/redis-journaling/RedisJournalingConfiguration.cs" id="configure_redis_journaling":::

The [runnable Redis Journaling sample](samples.md#run-the-redis-sample) uses the same provider with a <xref:Orleans.Journaling.DurableGrain>, acknowledges a durable value update, deactivates the grain, and verifies recovery on a new activation.

## Storage behavior

The provider stores journal data in Redis strings and journal metadata in Redis hashes. Per-journal reads and mutations use atomic Lua scripts. Catalog operations discover journals by scanning metadata keys on each connected primary Redis server.

Optimistic concurrency protects append, replace, and delete operations. A stale writer receives <xref:Orleans.Storage.InconsistentStateException>, and the Journaling state manager recovers the stored journal before processing later work. Concurrent reads observe either the journal before a replacement or the complete replacement.

<xref:Orleans.Journaling.RedisJournalStorageOptions> supports:

- <xref:Orleans.Journaling.RedisJournalStorageOptions.ConfigurationOptions> for the StackExchange.Redis connection.
- <xref:Orleans.Journaling.RedisJournalStorageOptions.CreateMultiplexer> to supply a shared or custom connection multiplexer.
- <xref:Orleans.Journaling.RedisJournalStorageOptions.KeyPrefix> to isolate journal keys. The default is `{ServiceId}/journaling`.
- <xref:Orleans.Journaling.RedisJournalStorageOptions.GetKeyName> to customize the journal key component.
- <xref:Orleans.Journaling.RedisJournalStorageOptions.CompactionThresholdBytes> to select when the provider requests compaction.
- <xref:Orleans.Journaling.RedisJournalStorageOptions.ReadChunkSize> to limit the size of recovery segments supplied to the journal consumer.

The key prefix and key-name function define the location of existing journals. Retain the previous mapping or migrate the stored data before deploying a new mapping.

The default compaction threshold is 128 MiB and the default recovery chunk size is 1 MiB. Compaction replaces the append history with a snapshot of the grain's current durable states.

Redis returns the journal string as one value. <xref:Orleans.Journaling.RedisJournalStorageOptions.ReadChunkSize> divides that value into replay segments after retrieval; capacity planning should account for the full journal value in the Redis client and silo process.

## Durability

Redis journal durability depends on the Redis [persistence](https://redis.io/docs/latest/operate/oss_and_stack/management/persistence/) and replication configuration. Configure persistence, such as append-only file (AOF) persistence with an appropriate `appendfsync` policy, according to the application's recovery-point requirements.

Use a distinct <xref:Orleans.Journaling.RedisJournalStorageOptions.KeyPrefix> when multiple Orleans services share a Redis deployment.

Back up each journal's string data and hash metadata consistently. Restore both under the same prefix and key mapping before allowing silos to activate the grain.

For capacity, upgrade, backup, and troubleshooting guidance, see [Operate and troubleshoot Orleans Journaling](operations.md).
