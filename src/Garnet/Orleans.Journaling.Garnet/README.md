# Microsoft Orleans Journaling for embedded Garnet

## Introduction

Microsoft Orleans Journaling for embedded Garnet stores `Orleans.Journaling` journals in [Garnet](https://github.com/microsoft/garnet) partitions hosted inside Orleans silos. Journals hash into buckets, and a range map assigns ranges of buckets to partitions and partitions to silos. Every journal operation is one Garnet transaction procedure on the partition which owns the journal's bucket, and Garnet acknowledges it only after the operation is flushed to its append-only file (AOF), so acknowledged writes survive the loss of the process. With shared storage, partitions keep their AOF in Azure page blobs, so acknowledged writes also survive the loss of the machine, and another silo takes the partition over.

Ranges of buckets move between partitions online: a range is unavailable only for a brief cutover, and its journals follow in the background. This package is experimental. See [DESIGN.md](../DESIGN.md) for the protocol, its Quint specification, the decisions behind it, and measurements.

## Getting started

```shell
dotnet add package Microsoft.Orleans.Journaling.Garnet --prerelease
```

## Example: one partition, hosted by one silo

```csharp
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Journaling.Ranges;

var builder = Host.CreateApplicationBuilder(args);
builder.UseOrleans(silo =>
{
    silo.AddGarnetJournalStorage(options =>
    {
        // Holds the range map. Use AzureBlobRangeMetaStore for a durable, shared map.
        options.MetaStore = new InMemoryRangeMetaStore();

        // The silo hosts partition p0, which owns every bucket, in this directory's p0 subdirectory.
        options.DataDirectory = "/var/lib/orleans/journals";
    });
});
```

## Example: several partitions across silos

Every silo shares one MetaStore and configures the same initial map. The first silo to start writes it; each silo hosts the partitions assigned to its `HostId` and routes operations on other buckets to the silos hosting them.

```csharp
using System.Net;
using Azure.Storage.Blobs;
using Orleans.Journaling.Ranges;

var map = RangeMap.CreateUniform(4096,
[
    new(new PartitionId("p0"), new PartitionHosting(Host: "silo-a", Epoch: 0, StorageLocation: "p0")),
    new(new PartitionId("p1"), new PartitionHosting(Host: "silo-b", Epoch: 0, StorageLocation: "p1")),
]);

silo.AddGarnetJournalStorage(options =>
{
    options.MetaStore = new AzureBlobRangeMetaStore(new BlobClient(connectionString, "orleans-journals", "range-map.json"));
    options.InitialRangeMap = map;
    options.HostId = "silo-a";
    options.DataDirectory = "/var/lib/orleans/journals";

    // Partitions hosted by this silo listen on consecutive ports from 6380.
    options.ListenEndPoint = new IPEndPoint(IPAddress.Any, 6380);
});
```

Partition endpoints have no authentication, so expose them only on a trusted network.

## Example: partitions on shared storage, with failover

With `PartitionBlobContainer`, every partition stores its append-only file, hybrid log, and checkpoints as Azure page blobs, so any silo can host it. Silos are identified by their silo addresses; when Orleans membership declares a silo dead, another silo takes its partitions over and recovers them from the blobs.

```csharp
using Azure.Storage.Blobs;
using Orleans.Journaling.Ranges;

silo.AddGarnetJournalStorage(options =>
{
    options.MetaStore = new AzureBlobRangeMetaStore(new BlobClient(connectionString, "orleans-journals", "range-map.json"));
    options.PartitionBlobContainer = new BlobContainerClient(connectionString, "orleans-journal-partitions");

    // Optional: start with four partitions. A host which is not a silo address leaves the partition for the silos to place.
    options.InitialRangeMap = RangeMap.CreateUniform(4096,
        Enumerable.Range(0, 4).Select(i => KeyValuePair.Create(new PartitionId($"p{i}"), new PartitionHosting("unassigned", 0, $"p{i}"))));
});
```

## Behavior

- **Durability.** Garnet runs with `--aof --aof-commit-freq 0 --aof-commit-wait`. Replies, including replies to reads, are sent only after the AOF commit covering the operation is written to the device: on local disk, with write-through, unbuffered I/O; on shared storage, as page blob writes which Azure Storage has committed.
- **Failover.** With `PartitionBlobContainer`, a silo starts a partition by taking its storage over: it breaks the previous host's leases, after which the storage service rejects every write of the previous host, and only then recovers the partition. A previous host which is still running therefore acknowledges no operation and answers no read once the new host has taken over, and it stops serving the partition when it observes the new assignment. Operations retry while a partition fails over.
- **Consistency.** Each operation runs as one transaction procedure, and only the partition which holds a bucket open accepts operations on it. Writes are conditional on the state the storage instance last observed: the journal's content ETag, or the ETag of the deletion which left it absent. A stale writer fails with `InconsistentStateException`. Operations whose replies are lost are retried with the same ETags: a retry of an operation that already committed reports success without applying it again, and a retry which can no longer tell whether its earlier attempt committed, because other writers have changed the journal since, fails with `InconsistentStateException`. No operation applies twice.
- **Routing.** Each silo caches the range map. When a partition reports that a bucket moved in a newer map, the silo refreshes its map and retries; when a partition is unreachable or not yet serving, it retries with backoff. Operations which cannot complete within `OperationTimeout` fail with `TimeoutException`.
- **Migration.** A migration recorded in the range map moves a range from its source partition to its destination: the source seals the range's buckets, the destination opens them once the seals are recorded, and from then on the destination serves every operation. A journal's first access at the destination pulls it from the source; the destination's host consolidates the remaining journals in the background, records the migration done, and the source then deletes its copy. Silos which host partitions drive the migrations into them, and each step is recorded in the range map by compare-and-swap, so a silo restart or a duplicated driver resumes or repeats steps safely.
- **Deletion.** Deleting a journal leaves a tombstone, which keeps a migration from resurrecting the journal from its source and lets retried writes recognize the deletion. Hosting silos drop tombstones older than `TombstoneRetention` once their bucket's migration is done, and advance the bucket's collection generation; a retried write which observed the journal's absence before the dropped deletion fails with `InconsistentStateException` rather than applying again, whatever the silos' clocks.
- **Data model.** A journal's data is a list with one element per append, so appends do not copy earlier data. `ReplaceAsync` replaces the list with one element. `IsCompactionRequested` becomes `true` once the journal's appends since its last replacement reach `CompactionThresholdBytes` (8 MiB by default).
- **Catalog.** The provider implements `IJournalStorageCatalog` by listing each bucket's journal index at the partition which owns it, consolidating a bucket first while its migration is in progress.

## Configuration

| Option | Default | Description |
| --- | --- | --- |
| `MetaStore` | none (required) | Store holding the range map, shared by every silo: `AzureBlobRangeMetaStore`, or one `InMemoryRangeMetaStore` instance for tests. |
| `HostId` | the silo address with `PartitionBlobContainer`; otherwise generated and stored in `DataDirectory` | This silo's identifier in the map's hosting assignments. Set it to name hosts in `InitialRangeMap`; silos with an explicit `HostId` are not failed over automatically. |
| `InitialRangeMap` | one partition, `p0`, hosted by the first silo with a `DataDirectory` or `PartitionBlobContainer` | Map written to an empty MetaStore. Ignored once the MetaStore holds a map. |
| `DataDirectory` | none | Directory holding the partitions this silo hosts, each in the subdirectory named by its storage location. Required on silos which host partitions on local disk. |
| `PartitionBlobContainer` | none | Azure Storage container holding every partition's state as page blobs under its storage location, shared by every silo. Enables failover between silos. |
| `HostingInterval` | 1 s | How often a silo reads the range map to start and stop its partitions and to fail partitions over from dead silos. |
| `ListenEndPoint` | free loopback ports | Endpoint on which hosted partitions listen; with a non-zero port, partitions use consecutive ports from it. A wildcard address is published as the machine's host name. |
| `BucketCount` | 4096 | Number of buckets in the default initial map; a power of two, fixed when the map is created. |
| `CompactionThresholdBytes` | 8 MiB | Append volume at which compaction is requested. |
| `OperationTimeout` | 30 s | How long an operation retries while its partition is unreachable, its outcome is unknown, or its bucket is moving. |
| `TombstoneRetention` | 5 min | How long a hosting silo keeps a deleted journal's tombstone once its bucket's migration is done. Retries which arrive within it report their committed outcome; later ones fail with `InconsistentStateException`. Use more than the cluster's longest `OperationTimeout`, plus an allowance for clock skew between silos. |
| `MigrationInterval` | 1 s | How often a hosting silo checks for migrations into its partitions while it has none in progress. |
| `MarkerCollectionInterval` | 1 min | How often a hosting silo drops expired tombstones and absent markers. |
| `LogMemorySize` | `1g` | In-memory size of each hosted partition's hybrid log; older records spill to its directory. |
| `IndexMemorySize` | `64m` | Size of each hosted partition's hash index. |
| `CheckpointAofSize` | `256m` | AOF size at which a partition takes a checkpoint, compacting its log and truncating the AOF. |
