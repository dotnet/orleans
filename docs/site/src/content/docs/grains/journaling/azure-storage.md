---
title: Azure Storage providers for Journaling
description: Configure Azure Blob Storage or Azure Table Storage for experimental Orleans Journaling.
ms.date: 08/23/2026
ms.topic: how-to
---

# Azure Storage providers for Journaling

The pre-release [`Microsoft.Orleans.Journaling.AzureStorage`](https://www.nuget.org/packages/Microsoft.Orleans.Journaling.AzureStorage) package provides Azure Blob Storage and Azure Table Storage implementations. Its APIs carry diagnostic `ORLEANSEXP005`.

Use Microsoft Entra workload identity in hosted environments and grant the silo identity only the data-plane permissions required for the selected container or table.

## Named storage namespaces

Both Azure registration methods accept a name followed by the options delegate.
Register independent Blob clients/containers or Table clients/tables under
different names to configure multiple providers of the same backend. Named
options, storage, catalog, and state-manager factory share the same binding.
The unnamed overload configures default grain journaling; named registrations
leave that default independent.

For an account, container, table, or backend cutover, retain the old namespace
and its mapping under its original binding while new Durable Jobs shards use
a second binding. During draining, the old account still needs listing,
conditional ownership updates, journal mutations, and delete permissions.
Granting only read access prevents successful draining. See
[Migrate Durable Jobs storage](durable-jobs-migration.md) for the staged rollout,
full inventory, and retirement criteria.

## Use with Aspire

Aspire injects the Azure Storage connection and keyed SDK client. Configure the journaling provider section explicitly in the silo resource:

:::code language="csharp" source="../../host/snippets/aspire/AppHost/AppHostExamples.cs" id="azure_table_journaling_aspire":::

The example activates Azure Table journal storage. Use `AzureBlobStorage` as `ProviderType`, reference the blob resource service key, and register the corresponding keyed <xref:Azure.Storage.Blobs.BlobServiceClient> to activate Azure Blob journal storage.

## Azure Blob Storage

Configure <xref:Orleans.Journaling.AzureBlobStorageHostingExtensions.AddAzureBlobJournalStorage*> with an authenticated <xref:Azure.Storage.Blobs.BlobServiceClient>:

:::code language="csharp" source="./snippets/journaling/JournalingConfiguration.cs" id="configure_azure_blob":::

Each journal uses:

- An append blob at `wal/<journalId>` by default.
- Immutable checkpoint blobs at `checkpoints/<journalId>/<snapshotId>`.
- WAL metadata which identifies the current checkpoint, journal format, and optimistic-concurrency state.

Recovery reads the published checkpoint followed by the WAL tail. A replacement uploads the new checkpoint and then atomically publishes it through WAL metadata. The provider performs best-effort cleanup of obsolete checkpoints after publication when <xref:Orleans.Journaling.AzureBlobJournalStorageOptions.DeleteOldCheckpoints> is `true`, which is the default.

Customize <xref:Orleans.Journaling.AzureBlobJournalStorageOptions.GetWalBlobName> and <xref:Orleans.Journaling.AzureBlobJournalStorageOptions.GetCheckpointBlobName> to apply a tenant or application prefix. Keep the mapping stable or migrate every referenced blob and its metadata together.

Azure append blobs limit append-block size and block count. The provider accepts an encoded append batch up to 100 MiB, requests compaction after 49,000 committed blocks, and reserves additional headroom before the 50,000-block service limit.

The journal catalog selects append blobs in the `wal/` namespace of the configured container. Custom naming delegates participating in catalog discovery produce `wal/<journalId>` for each journal. The separate checkpoint namespace keeps checkpoints out of listing pages. Raw journal-id prefixes and conservative ASCII bounds narrow the native listing on both flat-namespace and hierarchical-namespace (HNS) accounts.

HNS recursive listings sort `/` before other characters. The catalog preserves each bound's shared listing prefix and widens the remaining suffix when punctuation or directory separators affect ordering. For example, an inclusive range from `a!` through `a/0` starts at `wal/a` and completes after crossing `wal/a0`. Every returned candidate is checked against the original ordinal range. Shared prefixes keep timestamp scans narrow; wider boundaries can transfer additional candidates. Both account types use the same listing algorithm and configured Blob client.

Catalog callers can request a metadata snapshot with each identity. Blob listings project the WAL's format, ETag, and caller-owned metadata in the listing response. The snapshot can replace a separate metadata read; conditional updates use its ETag to detect concurrent changes.

## Azure Table Storage

Configure <xref:Orleans.Journaling.AzureTableStorageHostingExtensions.AddAzureTableJournalStorage*> with an authenticated <xref:Azure.Data.Tables.TableServiceClient>:

:::code language="csharp" source="./snippets/journaling/JournalingConfiguration.cs" id="configure_azure_table":::

Each journal occupies one table partition:

- A header row stores the journal manifest, format, generation, and concurrency ETag.
- Ordered data rows store the encoded journal bytes for the published generation.
- Append operations commit new rows and the header update in one entity group transaction.
- Replacement writes a new generation and atomically changes the header to publish it. The provider performs best-effort cleanup of the previous generation when <xref:Orleans.Journaling.AzureTableJournalStorageOptions.DeleteOldGenerations> is `true`.

A single append batch is limited to 2 MiB by the provider's entity group transaction design. Snapshot replacements can exceed that size because rows are written before the header publishes the generation.

Compaction is requested at either <xref:Orleans.Journaling.AzureTableJournalStorageOptions.CompactionRowCountThreshold> (10,000 rows by default) or <xref:Orleans.Journaling.AzureTableJournalStorageOptions.CompactionSizeThreshold> (32 MiB by default).

The default partition mapping accepts printable ASCII journal ids (`0x20` through `0x7E`) and encodes each byte as two uppercase hexadecimal digits. It preserves ordinal ordering and prefixes for indexed catalog queries and supports journal ids up to 512 characters. Storage creation validates the id before accessing Azure. This restriction is specific to the default Table mapping.

Customize <xref:Orleans.Journaling.AzureTableJournalStorageOptions.GetPartitionKey> when a different partition layout is required. Custom mappings support other journal-id alphabets, remain unique per journal, and satisfy Azure Table partition-key constraints. Their catalog queries filter the canonical journal-id property and can require a table scan.

Metadata-enabled catalog queries select the header's format and caller-owned metadata together with its ETag. Identity-only queries retain their smaller projection. Each returned metadata snapshot has the same meaning as a direct metadata read and can become stale after it is listed.

## Optimistic concurrency

Both providers condition append, replace, and delete operations on the last observed ETag. Metadata-only conflicts receive a bounded in-place refresh and retry. A journal-content conflict raises <xref:Orleans.Storage.InconsistentStateException>, which permanently fences the Journaling state manager and requests grain deactivation. A fresh activation replays the durable journal.

Configure the metadata-only retry cap and backoff with the corresponding `MaxMetadataOnlyConflictRetries`, `MetadataOnlyConflictInitialBackoff`, and `MetadataOnlyConflictMaxBackoff` options.

## Backup and restore

Capture a consistent provider-level backup:

- For Blob Storage, preserve the WAL, the checkpoint named by WAL metadata, and all metadata required to interpret them.
- For Table Storage, preserve the partition header and every row in its published generation.

Restore the complete set before allowing silos to activate the grain. Validate representative journal replay and a subsequent compaction in an isolated environment.
