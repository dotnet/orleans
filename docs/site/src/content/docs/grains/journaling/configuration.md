---
title: Configure Orleans Journaling
description: Select an experimental Orleans Journaling storage provider and journal format.
ms.date: 08/21/2026
ms.topic: how-to
---

# Configure Orleans Journaling

Configure one journal storage provider on every silo that can activate a <xref:Orleans.Journaling.DurableGrain>. Provider registration also adds the core Journaling services, durable-state keyed services, JSON format, and legacy Orleans binary reader.

The Journaling packages are pre-release alpha packages and their APIs carry diagnostic `ORLEANSEXP005`.

## Choose a storage provider

| Provider | Package | Storage model | Compaction trigger |
| --- | --- | --- | --- |
| [Azure Blob Storage](azure-storage.md#azure-blob-storage) | `Microsoft.Orleans.Journaling.AzureStorage` | Append blob plus immutable checkpoint blobs | Append-blob block budget |
| [Azure Table Storage](azure-storage.md#azure-table-storage) | `Microsoft.Orleans.Journaling.AzureStorage` | One partition per journal with header and ordered data rows | Row count or journal bytes |
| [Redis](redis-journal-storage.md) | `Microsoft.Orleans.Journaling.Redis` | String journal plus hash metadata | Journal bytes |

Select a provider based on atomic-write limits, replay latency, durability configuration, backup tooling, maximum hot-grain size, and operational familiarity.

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

<xref:Orleans.Journaling.HostingExtensions.AddJournalStorage*> registers core services and resolves an <xref:Orleans.Journaling.IJournalStorageProvider>. Runtime tests and disposable development hosts can register <xref:Orleans.Journaling.VolatileJournalStorageProvider> directly; its contents live in process memory.

Use the same durable provider category in staging that production uses so recovery, compaction, concurrency, and backup procedures receive realistic validation.
