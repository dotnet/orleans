---
title: Grain persistence
description: Persist Orleans grain state using IPersistentState and storage providers.
ms.date: 08/02/2026
ms.topic: overview
---

# Grain persistence

Orleans grain persistence stores application state independently of a grain activation. When an activation starts, Orleans reads its configured state records before calling <xref:Orleans.Grain.OnActivateAsync*>. The grain explicitly writes changes when the operation's durability point is reached.

Persistence is intentionally a record-oriented abstraction, not an object-relational mapper. A grain can use multiple named state records, use different providers for different records, or access a database directly when it needs queries or data models that don't fit grain storage.

## Choose a provider

Officially maintained providers are available from [NuGet](https://www.nuget.org/packages?q=Orleans+Persistence) and include:

| Provider | Package | Typical use |
|---|---|---|
| [Azure Table and Blob Storage](azure-storage.md) | [`Microsoft.Orleans.Persistence.AzureStorage`](https://www.nuget.org/packages/Microsoft.Orleans.Persistence.AzureStorage) | Azure-hosted state records |
| [Azure Cosmos DB for NoSQL](azure-cosmos-db.md) | [`Microsoft.Orleans.Persistence.Cosmos`](https://www.nuget.org/packages/Microsoft.Orleans.Persistence.Cosmos) | Globally distributed Azure NoSQL storage |
| [Amazon DynamoDB](dynamodb-storage.md) | [`Microsoft.Orleans.Persistence.DynamoDB`](https://www.nuget.org/packages/Microsoft.Orleans.Persistence.DynamoDB) | AWS-hosted key-value storage |
| [Google Cloud Firestore](google-firestore-storage.md) | [`Microsoft.Orleans.Persistence.Firestore`](https://www.nuget.org/packages/Microsoft.Orleans.Persistence.Firestore) | Google Cloud-hosted document storage |
| [ADO.NET](relational-storage.md) | [`Microsoft.Orleans.Persistence.AdoNet`](https://www.nuget.org/packages/Microsoft.Orleans.Persistence.AdoNet) | SQL Server, MySQL/MariaDB, PostgreSQL, Oracle, and SQLite |
| [Redis](redis-storage.md) | [`Microsoft.Orleans.Persistence.Redis`](https://www.nuget.org/packages/Microsoft.Orleans.Persistence.Redis) | Low-latency state backed by Redis |
| [Memory](memory-storage.md) | `Microsoft.Orleans.Persistence.Memory` | Tests and disposable development environments |

Choose based on durability, availability, latency, record-size limits, operational tooling, and cost. Memory storage isn't durable across cluster restarts. Redis expiration should only be used for state that is intentionally ephemeral.

## Use persistent state

Inject <xref:Orleans.Runtime.IPersistentState`1> into the grain constructor and identify both the state record and provider with <xref:Orleans.Runtime.PersistentStateAttribute>:

:::code language="csharp" source="./snippets/persistence/GrainExamples.cs" id="user_grain_multiple_states":::

The state isn't available inside the constructor. Orleans loads it before <xref:Orleans.Grain.OnActivateAsync*>. From then on:

- <xref:Orleans.Core.IStorage`1.State> contains the in-memory value.
- <xref:Orleans.Core.IStorage.RecordExists> indicates whether the provider read an existing record.
- <xref:Orleans.Core.IStorage.Etag> contains the provider's concurrency token, when supported.
- <xref:Orleans.Core.IStorage.ReadStateAsync*> replaces the in-memory value with the latest stored value.
- <xref:Orleans.Core.IStorage.WriteStateAsync*> persists the current value.
- <xref:Orleans.Core.IStorage.ClearStateAsync*> clears or deletes the record according to provider configuration.

Each operation also has a <xref:System.Threading.CancellationToken> overload. A provider can implement cancellation, but the default interface implementation delegates to the overload without a token.

> [!IMPORTANT]
> Mutating <xref:Orleans.Core.IStorage`1.State> only changes the activation's in-memory copy. Await <xref:Orleans.Core.IStorage.WriteStateAsync*> before returning when the caller must observe a durable result.

## Configure named state

Configure every provider name referenced by `[PersistentState]` on the silo:

:::code language="csharp" source="./snippets/persistence/StorageConfiguration.cs" id="configure_managed_identity":::

The state name distinguishes records owned by the same grain. The provider name selects a keyed <xref:Orleans.Storage.IGrainStorage> registration. Different records aren't required to share a provider or backing store.

## Consistency and atomicity

A storage operation applies to one state record. Providers use the record's <xref:Orleans.Core.IStorage.Etag> for optimistic concurrency where the backend supports it. A write or clear with a stale ETag fails with <xref:Orleans.Storage.InconsistentStateException> rather than overwriting a newer value.

The following aren't one atomic operation:

- Writes to two <xref:Orleans.Runtime.IPersistentState`1> instances on the same grain.
- Writes to state owned by different grains.
- A storage write and an external side effect, such as publishing a message.

If an operation requires atomic updates across multiple grain states, use [Orleans transactions](../transactions.md) and transactional state. For storage plus messaging, design an application-level outbox, inbox, or idempotency protocol.

## Failure semantics

### Activation reads

If the initial read fails, activation fails and Orleans doesn't call <xref:Orleans.Grain.OnActivateAsync*>. The request that caused activation receives the failure. A bad or missing provider configuration results in <xref:Orleans.Storage.BadProviderConfigException>.

### Explicit reads, writes, and clears

Storage failures fault the returned task. Await each operation so that failures reach the grain call and its caller. After a failed write, don't assume that the stored value changed. The precise outcome depends on the provider and underlying service failure.

An <xref:Orleans.Storage.InconsistentStateException> means another writer changed the record since this activation last read it. Don't blindly retry the same write with the stale state. Re-read, re-evaluate the command against the new state, and write only if the operation is still valid.

For transient service failures, retries belong at an application boundary that understands idempotency. Prefer retrying the original command with an operation identifier over retrying an arbitrary storage write. Bound retries, add backoff, and preserve the exception when the retry budget is exhausted. Orleans doesn't automatically retry <xref:Orleans.Runtime.IPersistentState`1> operations for the application.

## State and schema evolution

Persistence outlives activations and deployments. Treat the stored representation as a versioned contract:

1. Add members in a backward-compatible form and preserve defaults for missing data.
1. Deploy readers that accept both old and new representations before writing only the new representation.
1. Don't rename, remove, or reinterpret stored members without a migration plan.
1. Test deserialization using data written by the currently deployed version.
1. Retain old event types and transition behavior when using event sourcing.

Storage providers expose <xref:Orleans.Storage.IGrainStorageSerializer> through their options. The default provider serializer uses JSON. A custom serializer can implement explicit envelopes, version fields, or migrations, but changing serializers doesn't migrate existing records automatically.

<span id="redis-grain-persistence"></span>

Redis configuration has moved to [Redis grain persistence](redis-storage.md).

<span id="memory-storage"></span>

Memory storage configuration has moved to [Memory grain persistence](memory-storage.md).

## Legacy grain state base class

The <xref:Orleans.Grain`1> base class and <xref:Orleans.Providers.StorageProviderAttribute> remain supported for compatibility, but new code should use <xref:Orleans.Runtime.IPersistentState`1>. Constructor injection supports multiple state records and makes the storage dependency explicit.

## Implement a storage provider

Custom providers implement <xref:Orleans.Storage.IGrainStorage>. Register a named provider using Orleans' storage registration helper, which uses .NET keyed services:

```csharp
siloBuilder.Services.AddGrainStorage<MyGrainStorage>(
    "custom",
    (services, name) => new MyGrainStorage(name));
```

Providers must:

- Populate <xref:Orleans.IGrainState`1.State>, <xref:Orleans.IGrainState`1.RecordExists>, and <xref:Orleans.IGrainState`1.ETag> when reading.
- Preserve optimistic-concurrency semantics and throw <xref:Orleans.Storage.InconsistentStateException> on an ETag conflict.
- Complete each returned task only when the storage operation has completed.
- Surface backend failures instead of returning success.
- Define and document whether <xref:Orleans.Storage.IGrainStorage.ClearStateAsync*> deletes or resets a record.
