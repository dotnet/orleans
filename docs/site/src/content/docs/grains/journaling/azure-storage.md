---
title: Azure Storage providers for Journaling
description: Configure Azure Blob Storage or Azure Table Storage for experimental Orleans Journaling.
ms.date: 08/21/2026
ms.topic: how-to
---

# Azure Storage providers for Journaling

The pre-release [`Microsoft.Orleans.Journaling.AzureStorage`](https://www.nuget.org/packages/Microsoft.Orleans.Journaling.AzureStorage) package provides Azure Blob Storage and Azure Table Storage implementations. Its APIs carry diagnostic `ORLEANSEXP005`.

Use Microsoft Entra workload identity in hosted environments and grant the silo identity only the data-plane permissions required for the selected container or table.

## Azure Blob Storage

Configure <xref:Orleans.Journaling.AzureBlobStorageHostingExtensions.AddAzureBlobJournalStorage*> with an authenticated <xref:Azure.Storage.Blobs.BlobServiceClient>:

:::code language="csharp" source="./snippets/journaling/JournalingConfiguration.cs" id="configure_azure_blob":::

Each journal uses:

- An append blob at `<journalId>/wal` by default.
- Immutable checkpoint blobs at `<journalId>/chk.<snapshotId>`.
- WAL metadata which identifies the current checkpoint, journal format, and optimistic-concurrency state.

Recovery reads the published checkpoint followed by the WAL tail. A replacement uploads the new checkpoint and then atomically publishes it through WAL metadata. The provider performs best-effort cleanup of obsolete checkpoints after publication when <xref:Orleans.Journaling.AzureBlobJournalStorageOptions.DeleteOldCheckpoints> is `true`, which is the default.

Customize <xref:Orleans.Journaling.AzureBlobJournalStorageOptions.GetWalBlobName> and <xref:Orleans.Journaling.AzureBlobJournalStorageOptions.GetCheckpointBlobName> to apply a tenant or application prefix. Keep the mapping stable or migrate every referenced blob and its metadata together.

Azure append blobs limit append-block size and block count. The provider accepts an encoded append batch up to 100 MiB, requests compaction after 49,000 committed blocks, and reserves additional headroom before the 50,000-block service limit.

The journal catalog discovers the default `/wal` naming shape. Preserve that suffix when custom names need catalog listing, or provide the application-specific discovery mechanism required by the caller.

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

Customize <xref:Orleans.Journaling.AzureTableJournalStorageOptions.GetPartitionKey> when a different partition layout is required. The mapping must remain unique per journal and satisfy Azure Table partition-key constraints.

## Optimistic concurrency

Both providers condition append, replace, and delete operations on the last observed ETag. Metadata-only conflicts receive a bounded in-place refresh and retry. A journal-content conflict raises <xref:Orleans.Storage.InconsistentStateException>, which causes the Journaling state manager to recover before later work.

Configure the metadata-only retry cap and backoff with the corresponding `MaxMetadataOnlyConflictRetries`, `MetadataOnlyConflictInitialBackoff`, and `MetadataOnlyConflictMaxBackoff` options.

## Backup and restore

Capture a consistent provider-level backup:

- For Blob Storage, preserve the WAL, the checkpoint named by WAL metadata, and all metadata required to interpret them.
- For Table Storage, preserve the partition header and every row in its published generation.

Restore the complete set before allowing silos to activate the grain. Validate representative journal replay and a subsequent compaction in an isolated environment.
