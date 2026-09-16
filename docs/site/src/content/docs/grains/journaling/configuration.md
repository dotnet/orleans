---
title: Configure Orleans Journaling
description: Select an experimental Orleans Journaling storage provider and journal format.
ms.date: 08/21/2026
ms.topic: how-to
---

# Configure Orleans Journaling

Configure a default journal storage provider on every silo that can activate a <xref:Orleans.Journaling.DurableGrain>. Additional named providers give journal consumers independent storage namespaces alongside default grain journaling. Provider registration also adds the core Journaling services, durable-state keyed services, JSON format, and Orleans binary reader.

The Journaling packages are pre-release alpha packages and their APIs carry diagnostic `ORLEANSEXP005`.

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
it contains work. A configuration-based `GrainJournaling` provider name selects
the journal registration; `ServiceKey` selects the Azure or Redis client from
dependency injection.

For Durable Jobs, <xref:Orleans.Hosting.DurableJobsExtensions.UseJournaledDurableJobs*>
selects the journaled implementation.
<xref:Orleans.Hosting.DurableJobsOptions.WriteProviderName> selects the provider
for new shards and defaults to `Default`.
<xref:Orleans.Hosting.DurableJobsOptions.DrainingProviderNames> selects additional
providers containing existing shards and is empty by default. All selected
bindings require storage, catalog, and factory services and are validated at
startup. Bindings remain fixed for that process's lifetime.

See [Migrate Durable Jobs storage](durable-jobs-migration.md) for named selection,
deployment staging, and full-namespace retirement checks. The runnable sample
linked from that guide exercises the new APIs against packages built from this
repository; existing published snippet packages predate named-provider selection.

## Configure the JSON format

JSON Lines is the default write format. Register source-generated metadata for every application type used as a durable key, value, collection item, persistent state, or durable task result:

:::code language="csharp" source="./snippets/journaling/JournalingConfiguration.cs" id="configure_json_format":::

The format emits UTF-8 JSON Lines with one complete journal entry per line and `application/jsonl` metadata where the provider supports content types.

Serializer naming policies affect application payload values. Journal command names and record structure remain fixed by the format.

## Migrate a journal format

Providers persist a format key with journal metadata. Recovery selects the stored reader independently of the configured write format. When they differ, the next write creates a full snapshot using the configured format and updates the metadata.

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

<xref:Orleans.Journaling.HostingExtensions.AddJournalStorage*> registers core services and resolves an <xref:Orleans.Journaling.IJournalStorageProvider>. Runtime tests and disposable development hosts can use <xref:Orleans.Journaling.HostingExtensions.AddVolatileJournalStorage*> with a provider name. Its contents live in process memory, so use persistent emulator storage to validate restart recovery and provider migration.

Use the same durable provider category in staging that production uses so recovery, compaction, concurrency, and backup procedures receive realistic validation.
