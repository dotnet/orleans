---
title: Redis grain persistence
description: Configure Redis as an Orleans grain storage provider.
ms.date: 08/07/2026
ms.topic: how-to
---

# Redis grain persistence

The [`Microsoft.Orleans.Persistence.Redis`](https://www.nuget.org/packages/Microsoft.Orleans.Persistence.Redis) package stores grain state in [Redis](https://redis.io/docs/latest/develop/data-types/). The provider uses atomic Redis scripts and ETags to reject stale writes.

## Configure a named provider

Configure a provider with <xref:Orleans.Hosting.RedisSiloBuilderExtensions.AddRedisGrainStorage*>:

:::code language="csharp" source="./snippets/persistence/StorageConfiguration.cs" id="configure_redis":::

The provider name must match the storage name in `[PersistentState]`.

## Configure the default provider

Use <xref:Orleans.Hosting.RedisSiloBuilderExtensions.AddRedisGrainStorageAsDefault*> when grains should use Redis without specifying a storage name:

:::code language="csharp" source="./snippets/persistence/StorageConfiguration.cs" id="configure_redis_default":::

## Storage behavior

<xref:Orleans.Persistence.RedisStorageOptions> supports:

- <xref:Orleans.Persistence.RedisStorageOptions.ConfigurationOptions> for the StackExchange.Redis connection.
- <xref:Orleans.Persistence.RedisStorageOptions.DeleteStateOnClear> to delete the Redis key instead of resetting the record.
- <xref:Orleans.Persistence.RedisStorageOptions.EntryExpiry> to expire records.
- <xref:Orleans.Persistence.RedisStorageOptions.CreateMultiplexer> to supply a shared or custom `IConnectionMultiplexer`.
- <xref:Orleans.Persistence.RedisStorageOptions.GetStorageKey> to customize key generation.
- <xref:Orleans.Persistence.RedisStorageOptions.GrainStorageSerializer> to customize the stored representation.

The default key format is `{ServiceId}/state/{grainId}/{grainType}`.

> [!WARNING]
> Set <xref:Orleans.Persistence.RedisStorageOptions.EntryExpiry> only for intentionally ephemeral state, such as tests. Expiration removes state independently of the grain lifecycle and can permit duplicate activations.

Changing the key function or serializer doesn't migrate existing Redis entries. Plan a data migration or retain compatibility with the previous representation.
