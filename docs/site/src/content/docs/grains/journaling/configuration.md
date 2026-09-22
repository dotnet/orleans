---
title: Configure Orleans Journaling
description: Select an experimental Orleans Journaling storage provider and journal format.
ms.date: 08/21/2026
ms.topic: how-to
---

# Configure Orleans Journaling

Configure a default journal storage provider on every silo that hosts grains or activation-scoped features using <xref:Orleans.Journaling.IDurableStateManager>. Additional named providers give journal consumers independent storage namespaces alongside default grain journaling. Provider registration also adds the core Journaling services, durable-state factories and keyed services, activation lifecycle integration, JSON format, and Orleans binary reader.

The Journaling packages are pre-release alpha packages and their APIs carry diagnostic `ORLEANSEXP005`.

## Configure grain-scoped composition

The standard <xref:Orleans.Journaling.JournalingHostingExtensions.AddJournaling*> registration supplies one scoped `IDurableStateManager` per activation, using the existing activation service scope. The manager enrolls itself in the grain lifecycle when constructed with the activation's <xref:Orleans.Runtime.IGrainContext>. Inject that manager into an ordinary <xref:Orleans.Grain>, an application-owned grain base, or an activation-scoped feature. Declare states through its `GetOrAdd` helpers or keyed injection during construction or synchronous activation setup. Recovery at <xref:Orleans.Runtime.GrainLifecycleStage.SetupState> then completes before <xref:Orleans.Grain.OnActivateAsync*> and requests.

Storage registration makes these services available across the silo. Per-grain journal reads and writes are triggered by activations which resolve the manager directly or through a durable-state dependency. Unrelated grain activations retain their existing persistence behavior.

<xref:Orleans.Journaling.DurableGrain> provides optional protected helpers for the same scoped manager. The default manager implements both `IDurableStateManager` and the independent <xref:Orleans.Journaling.IJournaledStateManager> contract. Custom grain manager replacements provide both contracts and their registrations as appropriate, and establish lifecycle enrollment in their constructor or registration factory before resolution returns. Reusable features compose their own lifecycle work through a [shared activation setup action](../grain-lifecycle.md#shared-activation-setup).

<xref:Orleans.Journaling.IJournaledStateManagerFactory.CreateStandalone*> returns the journal-owner contract for explicitly supplied state machine components. The caller constructs and registers components, initializes the journal, and disposes the owner. Component dependencies and their lifetimes remain caller-owned. Standalone journal owners use no manager-owned DI scope.

See [Use durable state](durable-state.md) for source-backed constructor injection and feature setup examples, and [Activation and recovery](runtime-behavior.md#activation-and-recovery) for lifecycle ordering.

## Choose a storage provider

| Provider | Package | Storage model | Compaction trigger |
| --- | --- | --- | --- |
| [Azure Blob Storage](azure-storage.md#azure-blob-storage) | `Microsoft.Orleans.Journaling.AzureStorage` | Append blob plus immutable checkpoint blobs | Append-blob block budget |
| [Azure Table Storage](azure-storage.md#azure-table-storage) | `Microsoft.Orleans.Journaling.AzureStorage` | One partition per journal with header and ordered data rows | Row count or journal bytes |
| [Redis](redis-journal-storage.md) | `Microsoft.Orleans.Journaling.Redis` | String journal plus hash metadata | Journal bytes |

Select a provider based on atomic-write limits, replay latency, durability configuration, backup tooling, maximum hot-grain size, and operational familiarity.

## Select named providers

The Blob, Table, Redis, S3, and volatile registration APIs accept a provider name.
Each named registration binds its options, storage, catalog, and state-manager
factory to one physical journal namespace. The unnamed overloads configure the
`Default` binding. Registrations under other names leave that default unchanged.

For a named backend registration, supply both the provider name and the options
delegate. Pass `configure: null` when options are configured separately.

The `Default` storage binding retains the unnamed backend-options configuration
pipeline, including option delegates registered before or after convenience
methods and each `ConfigureAll` delegate applied once. Non-default storage names
use their corresponding named backend options.
The storage binding name and the backend options name are therefore distinct
for the default binding.

An unkeyed singleton <xref:Orleans.Journaling.IJournalStorageProvider> customization supplies
storage for both grain activations and the `Default` keyed factory. The default
catalog and lifecycle bindings use that provider too, so replacement providers
implement the catalog and lifecycle contracts declared by the registered provider type.

A provider name identifies storage, rather than a journal format or an individual
durable state. Register each selected physical namespace once and keep its
account, container, table, bucket, key prefix, and naming functions stable while
it contains work.

Configure each journal storage binding under `Orleans:Journaling:{name}`.
`ProviderType` selects its storage provider, and `ServiceKey` selects its Azure
or Redis client from dependency injection. Orleans passes each entry's name and
configuration section to the registered provider builder. Use `Default` as the
name for grain journaling; other names configure additional storage bindings.

For example, these entries configure the default journal and an archive with
separate registered Redis clients and key prefixes:

```json
{
  "Orleans": {
    "Journaling": {
      "Default": {
        "ProviderType": "Redis",
        "ServiceKey": "primary-redis",
        "KeyPrefix": "primary-journal"
      },
      "archive": {
        "ProviderType": "Redis",
        "ServiceKey": "archive-redis",
        "KeyPrefix": "archive-journal"
      }
    }
  }
}
```

The equivalent environment-variable key for the archive client is
`Orleans__Journaling__archive__ServiceKey`.

For Durable Jobs, <xref:Orleans.Hosting.DurableJobsExtensions.UseJournaledDurableJobs*>
selects the journaled implementation.
<xref:Orleans.Hosting.DurableJobsOptions.ActiveProviderName> selects the provider
for new shards and defaults to `Default`.
<xref:Orleans.Hosting.DurableJobsOptions.DrainingProviderNames> selects additional
providers containing existing shards and is empty by default. All selected
bindings require storage, catalog, and factory services and are validated at
startup. Bindings remain fixed for that process's lifetime.

See [Migrate Durable Jobs storage](durable-jobs-migration.md) for named selection,
deployment staging, and full-namespace retirement checks. The runnable sample
linked from that guide exercises the new APIs against packages built from this
repository. The journaling snippet projects reference the current repository
sources so their configuration examples use the APIs in the current checkout.

## Configure the JSON format

JSON Lines is the default write format. <xref:Orleans.Journaling.Json.JsonJournalHostingExtensions.UseJsonJournalFormat*> registers source-generated metadata for application types used as durable keys, values, collection items, persistent state, or durable task results:

:::code language="csharp" source="./snippets/journaling/JournalingConfiguration.cs" id="configure_json_format":::

The format emits UTF-8 JSON Lines with one complete journal entry per line and `application/jsonl` metadata where the provider supports content types.

Serializer naming policies affect application payload values. Journal command names and record structure remain fixed by the format.

## Migrate a journal format

Providers expose the persisted format key as <xref:Orleans.Journaling.IJournalMetadata.FormatKey> and <xref:Orleans.Journaling.JournalMetadata.FormatKey>. Recovery selects the stored reader independently of the configured write format. When they differ, the next write creates a full snapshot using the configured format and updates the metadata. <xref:Orleans.Journaling.Json.JsonLinesJournalFormat.JournalFormatKey> supplies the JSON Lines format key.

Use this deployment sequence:

1. Back up the journal data and provider metadata as one recoverable unit.
1. Deploy binaries that retain readers and command codecs for the stored format.
1. Configure the new write format on every silo which can activate the grain type.
1. Exercise representative grains and confirm migration compactions succeed.
1. Retain the previous reader through the rollback window and until retired state streams are removed.

The Orleans binary format key is `orleans-binary`. Configure it explicitly while maintaining an existing binary journal:

:::code language="csharp" source="./snippets/journaling/JournalingConfiguration.cs" id="configure_binary_format":::

An unknown stored format key or incompatible payload fails recovery and leaves the journal unchanged.

## Configure state retirement

Named states which disappear from a grain remain recoverable during a grace period:

:::code language="csharp" source="./snippets/journaling/JournalingConfiguration.cs" id="configure_retirement":::

The default minimum is seven days. Removal is persisted by a compaction after the period expires. Set the period to cover deployment rollout, observation, and rollback.

## Development storage

<xref:Orleans.Journaling.JournalingHostingExtensions.AddJournaling*> registers the core services, formats, durable-state factories, and lifecycle integration. Register storage through a provider-specific method or the generic <xref:Orleans.Journaling.JournalingHostingExtensions.AddJournalStorage*> method. Runtime tests and disposable development hosts can use <xref:Orleans.Journaling.JournalingHostingExtensions.AddVolatileJournalStorage*> with a provider name. Its contents live in process memory, so use persistent emulator storage to validate restart recovery and provider migration.

Use the same durable provider category in staging that production uses so recovery, compaction, concurrency, and backup procedures receive realistic validation.
