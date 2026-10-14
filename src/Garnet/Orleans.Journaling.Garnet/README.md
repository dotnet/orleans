# Microsoft Orleans Journaling for embedded Garnet

## Introduction

Microsoft Orleans Journaling for embedded Garnet stores `Orleans.Journaling` journals in [Garnet](https://github.com/microsoft/garnet) partitions hosted inside Orleans silos. Journals hash into buckets, and a range map assigns ranges of buckets to partitions and partitions to silos. Every journal operation is one Garnet transaction procedure on the partition which owns the journal's bucket, and Garnet acknowledges it only after the operation is flushed to its append-only file (AOF), so acknowledged writes survive the loss of the process.

This package is experimental. The current version uses a static range map: ranges do not yet move between partitions. See [DESIGN.md](../DESIGN.md) for the protocol, the decisions behind it, and the planned online repartitioning.

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

Every silo shares one MetaStore and configures the same initial map. The first silo to start writes it; each silo hosts the partitions assigned to its `HostId`, which defaults to the silo name, and routes operations on other buckets to the silos hosting them.

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

## Behavior

- **Durability.** Garnet runs with `--aof --aof-commit-freq 0 --aof-commit-wait`. Replies, including replies to reads, are sent only after the AOF commit covering the operation is written to the device with write-through, unbuffered I/O.
- **Consistency.** Each operation runs as one transaction procedure, and only the partition which holds a bucket open accepts operations on it. Writes are conditional on the content ETag the storage instance last observed, or on the journal's absence, so a stale writer fails with `InconsistentStateException`. Operations whose replies are lost are retried with the same ETags, and a retry of an operation that already committed reports success without applying it again.
- **Routing.** Each silo caches the range map. When a partition reports that a bucket moved in a newer map, the silo refreshes its map and retries; when a partition is unreachable or not yet serving, it retries with backoff. Operations which cannot complete within `OperationTimeout` fail with `TimeoutException`.
- **Data model.** A journal's data is a list with one element per append, so appends do not copy earlier data. `ReplaceAsync` replaces the list with one element. `IsCompactionRequested` becomes `true` once the journal's appends since its last replacement reach `CompactionThresholdBytes` (8 MiB by default).
- **Catalog.** The provider implements `IJournalStorageCatalog` by listing each bucket's journal index at the partition which owns it.

## Configuration

| Option | Default | Description |
| --- | --- | --- |
| `MetaStore` | none (required) | Store holding the range map, shared by every silo: `AzureBlobRangeMetaStore`, or one `InMemoryRangeMetaStore` instance for tests. |
| `HostId` | silo name | This silo's identifier in the map's hosting assignments. Must be stable across restarts. |
| `InitialRangeMap` | one partition, `p0`, hosted by the first silo with a `DataDirectory` | Map written to an empty MetaStore. Ignored once the MetaStore holds a map. |
| `DataDirectory` | none | Directory holding the partitions this silo hosts, each in the subdirectory named by its storage location. Required on silos which host partitions. |
| `ListenEndPoint` | free loopback ports | Endpoint on which hosted partitions listen; with a non-zero port, partitions use consecutive ports from it. A wildcard address is published as the machine's host name. |
| `BucketCount` | 4096 | Number of buckets in the default initial map; a power of two, fixed when the map is created. |
| `CompactionThresholdBytes` | 8 MiB | Append volume at which compaction is requested. |
| `OperationTimeout` | 30 s | How long an operation retries while its partition is unreachable, its outcome is unknown, or its bucket is moving. |
| `LogMemorySize` | `1g` | In-memory size of each hosted partition's hybrid log; older records spill to its directory. |
| `IndexMemorySize` | `64m` | Size of each hosted partition's hash index. |
| `CheckpointAofSize` | `256m` | AOF size at which a partition takes a checkpoint, compacting its log and truncating the AOF. |
