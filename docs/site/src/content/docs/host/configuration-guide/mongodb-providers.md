---
title: MongoDB providers
description: Configure MongoDB for Orleans clustering, document-based grain persistence, and durable reminders.
ms.date: 10/13/2026
ms.topic: how-to
---

# MongoDB providers

Orleans provides MongoDB integrations for these capabilities:

| Capability | Package |
|---|---|
| Cluster membership and external-client gateway discovery | `Microsoft.Orleans.Clustering.MongoDB` |
| Grain state persistence | `Microsoft.Orleans.Persistence.MongoDB` |
| Durable reminders | `Microsoft.Orleans.Reminders.MongoDB` |

Install the capability packages used by the application. Configure a reusable
MongoDB client with a connection string supplied through application configuration
or a secret store. Register the driver's `IMongoClient` as a singleton to share
its connection pool across the providers. Use `MongoClientSettings` to configure
credentials, TLS, connection pools, and timeouts.

Options are in `Orleans.Clustering.MongoDB`, `Orleans.Persistence.MongoDB`, and
`Orleans.Reminders.MongoDB`. Each provider's `ClientName` selects a keyed
`IMongoClient` registration; an unset or whitespace value uses the unkeyed client.
The application or dependency injection container owns client disposal.

## Configure a silo

The following source-based example configures clustering, BSON document
persistence, and reminders:

:::code language="csharp" source="../snippets/mongodb/MongoDBConfiguration.cs" id="mongodb_silo":::

Membership is partitioned by Orleans `ClusterId`. Reminders are partitioned by
`ServiceId`. Persistence is isolated by database, collection prefix, state name,
and grain key. Use a separate database or collection prefix for applications that
must have independent grain state. Keep identifiers and prefixes stable when
accessing an existing deployment.

## Configure providers from application configuration

The packages register `MongoDB` as the `ProviderType` for silo/client clustering,
grain storage, and reminders. Register a shared MongoDB `IMongoClient` directly
or through the host's Aspire MongoDB integration, then supply
the provider options under the corresponding Orleans configuration sections:

:::code language="csharp" source="../snippets/mongodb/MongoDBConfiguration.cs" id="mongodb_configuration":::

Named grain-storage sections select storage provider names; use `Default` for
default grain storage. Configuration binding supports database names, collection
prefixes, client names, membership strategies, and reminder index management.

## Use Aspire

Aspire provides MongoDB hosting resources and a MongoDB client integration with
health checks and tracing. The following AppHost references one MongoDB database
for clustering, grain storage, and reminders:

:::code language="csharp" source="../snippets/mongodb/AppHost/Program.cs" id="mongodb_aspire_apphost":::

The silo uses the `Aspire.MongoDB.Driver` package and registers the client under
the database resource's name:

:::code language="csharp" source="../snippets/mongodb/MongoDBConfiguration.cs" id="mongodb_aspire_silo":::

An external client uses the same registration:

:::code language="csharp" source="../snippets/mongodb/MongoDBConfiguration.cs" id="mongodb_aspire_client":::

The AppHost emits `ProviderType=MongoDBDatabase` and `ServiceKey=orders`.
The provider packages recognize both `MongoDB` and `MongoDBDatabase`. `ServiceKey`
selects the keyed `IMongoClient`; when `DatabaseName` is omitted, it is derived
from the matching registered `IMongoDatabase`. An explicit database name takes
precedence. If `ClientName` and `ServiceKey` are both configured, they must match.

Aspire's `AddMongoDBClient` registration also works with the unkeyed providers.
The example uses the default single-document membership strategy, which runs
against Aspire's local MongoDB server. Configure a transaction-capable deployment
when selecting `Multiple`. Keep resource names aligned between AppHost references
and client registrations.

## Configure an external client

External clients discover active gateways using the clustering package:

:::code language="csharp" source="../snippets/mongodb/MongoDBConfiguration.cs" id="mongodb_client":::

Configure the client and silos with matching `ClusterId`, database, collection
prefix, and membership strategy.

## Choose a membership strategy

<xref:Orleans.Clustering.MongoDB.MongoDBMembershipStrategy> controls
the membership collection format:

| Strategy | Runtime behavior | Deployment requirement |
|---|---|---|
| `SingleDocument` (default) | Stores the membership rows and table version for one cluster in a single atomically updated document. | Capacity for the cluster membership document within MongoDB's document-size limit. |
| `Multiple` | Stores one document per silo and atomically updates the silo row and table version in a transaction. | A replica set or transaction-capable sharded cluster. |
| `MultipleDeprecated` | Reads and writes the contributed provider's legacy per-silo collection format. | Retained for existing deployments using that format. |

Use `SingleDocument` for small clusters or `Multiple` for transactional,
per-silo membership storage. Provision a replica set for local tests that exercise
`Multiple`; a single-node replica set supports MongoDB transactions.

`Multiple` reads the membership rows and table version in one snapshot
transaction with a fresh session for each operation and an explicit primary read
preference, including when the shared client prefers secondary reads. Membership async methods
propagate cancellation to MongoDB commands, cursor reads, and transactions.
New per-silo document IDs use a versioned encoding for both IPv4 and IPv6,
separate from the legacy format, and preserve complete native IPv6 addresses. Existing records
with legacy IPv4-mapped IDs remain readable and are updated in place using the
stored full silo address to distinguish identities.

## Select a persistence format

<xref:Orleans.Persistence.MongoDB.MongoDBGrainStorageOptions.GrainStateSerializer>
selects the MongoDB state serializer:

| Serializer | Stored state |
|---|---|
| <xref:Orleans.Providers.MongoDB.StorageProviders.Serializers.JsonGrainStateSerializer> (default) | A BSON document produced using Newtonsoft.Json and the configured Orleans JSON settings. |
| <xref:Orleans.Providers.MongoDB.StorageProviders.Serializers.BsonGrainStateSerializer> | A native, queryable BSON document using MongoDB serializers, attributes, and class maps. |
| <xref:Orleans.Providers.MongoDB.StorageProviders.Serializers.BinaryGrainStateSerializer> | Orleans-serialized bytes stored in BSON. |

An explicitly configured serializer takes precedence over a keyed
<xref:Orleans.Providers.MongoDB.StorageProviders.Serializers.IGrainStateSerializer>
registered for the storage provider's name, followed by the globally registered
serializer. For example, configure binary serialization for stream pub-sub state:

:::code language="csharp" source="../snippets/mongodb/MongoDBConfiguration.cs" id="mongodb_serializers":::

BSON document serialization supports application-owned MongoDB class maps.
Orleans stream pub-sub state uses the JSON or binary serializer.

The JSON serializer supports object, scalar, and array roots. Its legacy BSON
date strings use the invariant Gregorian calendar and time separators across cultures.
Its field-name
mapping encodes a leading `$` in a JSON property name as `__`. JSON property
names beginning with `__` are reserved and rejected before a write reaches MongoDB,
including nested properties. Use JSON property-name attributes or a naming
strategy to choose another name for those members; native BSON serialization
supports application-owned document mappings.

Persistence documents contain `_id` (grain key), `_etag` (optimistic-concurrency
token), and `_doc` (serialized state). The default collection prefix is `Grains`;
the collection suffix is the state name, or the state type name when the state
name is `state`. Keep the serializer, prefix, and key generator stable across
rolling upgrades.

Successful writes advance the ETag. Clears remove the document, recreate the state
using the registered Orleans activator, and reset its ETag and existence metadata. Writes and
clears reject stale ETags with <xref:Orleans.Storage.InconsistentStateException>,
preserving the caller's metadata on failure. A `*` ETag requests an unconditional
write or clear. Configure acknowledged MongoDB writes so the provider can observe
these outcomes.
Refresh reads replace the state and its ETag together after successful deserialization;
a deserialization failure preserves the previous state, ETag, and existence metadata.
Storage and reminder cancellation-token overloads propagate cancellation through
lazy collection/index initialization, reads, writes, removals, and conflict lookups.
A cancelled write may already have reached MongoDB; refresh state before retrying
when its outcome is uncertain.

## Migrate the contributed package

Replace the `Orleans.Providers.MongoDB` package reference with the capability
packages listed above. The provider retains the `Orleans.Providers.MongoDB.*`
implementation and serializer namespaces, provider builder method names, collection names, and stored document formats.
Keep database names, collection prefixes, membership strategies, serializers,
and key generators unchanged to access existing data.

Update option imports to the provider-specific namespaces listed above. Replace
`UseMongoDBClient`, `AddMongoDBClient` service-collection calls, and custom
`IMongoClientFactory` implementations with registered driver clients. Register
named clients using keyed DI services and select them with `ClientName`.
Aspire's host-builder `AddMongoDBClient` and `AddKeyedMongoDBClient` methods remain
the client-registration APIs for Aspire applications.

Persistence writes and clears enforce the supplied ETag. Applications should
retain the ETag returned by the most recent read or write when clearing state.

The MongoDB driver dependency is updated with the migration. Review custom BSON
serialization mappings and verify a backup-and-restore rehearsal with
representative existing documents before deployment.

Reminders use the `<CollectionPrefix>OrleansReminderV2` collection. The provider
creates a service-ID/grain-hash index and removes obsolete hash indexes.
New reminder document IDs encode the service, grain, and reminder components
separately. Existing underscore-delimited IDs remain readable, updatable in place,
and removable with their ETags; stored identity fields protect against ambiguous
legacy IDs. During rollout, pause new reminder creation until every reminder-table
writer uses the updated provider so point operations consistently understand the
new ID encoding.
<xref:Orleans.Reminders.MongoDB.MongoDBRemindersOptions.CreateIndexes>
defaults to `true`. Set it to `false` when administrators manage indexes or the
application identity has data-only permissions. Administrators should provision
the compound index `{ ServiceId: 1, GrainHash: 1 }` for reminder range queries;
the provider then performs data operations while administrators own index creation
and legacy-index cleanup.
Existing deployments can use `PurgeLegacyIndexes` after all silos using older
reminder query shapes have been upgraded, allowing the remaining legacy indexes
to be reclaimed safely.

The reminder provider validates grain IDs and nonempty reminder names before
writing. A stored document missing required identity fields produces a visible
read error. If such an error occurs, inspect and repair the affected document,
and review application BSON conventions and MongoDB command interceptors to
identify the writer responsible for the corruption.

## Operational guidance

- Grant the application identity permissions for provider-owned collections,
  their indexes, and transactions. Use TLS and MongoDB authentication for
  production connections.
- Keep membership and reminders near the silo deployment to bound database
  latency on liveness and reminder operations. Monitor MongoDB request failures,
  transaction aborts, connection-pool saturation, and query latency.
- Back up grain state and reminders according to the application's recovery
  requirements. Restrict direct writes to provider documents to preserve
  membership, reminder, and ETag invariants.
- `CreateShardKeyForCosmos` issues a hashed `_id` sharding command for deployments
  using the compatible Cosmos DB for MongoDB API. Enable it for databases whose
  sharding configuration requires that command and grants permission to execute it.
- Exercise silo loss, database failover, credential rotation, stale ETags, and
  rolling upgrades against the deployment's MongoDB topology.
