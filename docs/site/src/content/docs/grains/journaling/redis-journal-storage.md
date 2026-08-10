---
title: Redis journal storage
description: Configure Redis as a storage provider for Orleans Journaling durable state.
ms.date: 08/10/2026
ms.topic: how-to
---

# Redis journal storage

Orleans Journaling persists durable state changes as ordered journal data which is replayed to recover in-memory durable values and collections in <xref:Orleans.Journaling.DurableGrain> grains. It's a distinct mechanism from [Event Sourcing](../event-sourcing/index.md#event-sourcing-and-experimental-journaling), which uses `JournaledGrain<TState, TEvent>` and log-consistency providers.

The [`Microsoft.Orleans.Journaling.Redis`](https://www.nuget.org/packages/Microsoft.Orleans.Journaling.Redis) package stores that journal data in [Redis](https://redis.io/docs/latest/develop/data-types/). Orleans Journaling is experimental and its APIs are marked with diagnostic `ORLEANSEXP005`.

## Configure Redis journal storage

Configure the provider with <xref:Orleans.Journaling.RedisJournalStorageHostingExtensions.AddRedisJournalStorage*>:

:::code language="csharp" source="./snippets/redis-journaling/RedisJournalingConfiguration.cs" id="configure_redis_journaling":::

## Storage behavior

The provider stores journal data in Redis strings and journal metadata in Redis hashes. Per-journal reads and mutations use atomic Lua scripts. Catalog operations discover journals by scanning metadata keys on each connected primary Redis server.

<xref:Orleans.Journaling.RedisJournalStorageOptions> supports:

- <xref:Orleans.Journaling.RedisJournalStorageOptions.ConfigurationOptions> for the StackExchange.Redis connection.
- <xref:Orleans.Journaling.RedisJournalStorageOptions.CreateMultiplexer> to supply a shared or custom connection multiplexer.
- <xref:Orleans.Journaling.RedisJournalStorageOptions.KeyPrefix> to isolate journal keys. The default is `{ServiceId}/journaling`.
- <xref:Orleans.Journaling.RedisJournalStorageOptions.GetKeyName> to customize the journal key component.
- <xref:Orleans.Journaling.RedisJournalStorageOptions.CompactionThresholdBytes> to select when the provider requests compaction.
- <xref:Orleans.Journaling.RedisJournalStorageOptions.ReadChunkSize> to limit the size of recovery segments supplied to the journal consumer.

Changing the key prefix or key-name function doesn't migrate existing journals. Retain the previous mapping or migrate the stored data before deploying the change.

## Durability

Redis journal durability depends on the Redis [persistence](https://redis.io/docs/latest/operate/oss_and_stack/management/persistence/) and replication configuration. Configure persistence, such as append-only file (AOF) persistence with an appropriate `appendfsync` policy, according to the application's recovery-point requirements.

Use a distinct <xref:Orleans.Journaling.RedisJournalStorageOptions.KeyPrefix> when multiple Orleans services share a Redis deployment.
