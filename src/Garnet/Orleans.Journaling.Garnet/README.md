# Microsoft Orleans Journaling for embedded Garnet

## Introduction

Microsoft Orleans Journaling for embedded Garnet stores `Orleans.Journaling` journals in [Garnet](https://github.com/microsoft/garnet) partitions hosted inside Orleans silos. Every journal operation is one Garnet transaction procedure, and Garnet acknowledges it only after the operation is flushed to its append-only file (AOF), so acknowledged writes survive the loss of the process.

This package is experimental. The current version hosts one partition per cluster. See [DESIGN.md](../DESIGN.md) for the protocol, the decisions behind it, and the planned range-partitioned design.

## Getting started

```shell
dotnet add package Microsoft.Orleans.Journaling.Garnet --prerelease
```

## Example: host the partition in a silo

```csharp
using System.Net;
using Orleans.Hosting;
using Orleans.Journaling;

var builder = Host.CreateApplicationBuilder(args);
builder.UseOrleans(silo =>
{
    silo.AddGarnetJournalStorage(options =>
    {
        // The partition's AOF and checkpoints. The directory must persist across restarts.
        options.DataDirectory = "/var/lib/orleans/journals";
    });
});
```

Other silos connect to the hosting silo's partition, which listens on `ListenEndPoint`:

```csharp
// On the hosting silo.
silo.AddGarnetJournalStorage(options =>
{
    options.DataDirectory = "/var/lib/orleans/journals";
    options.ListenEndPoint = new IPEndPoint(IPAddress.Any, 6380);
});

// On every other silo.
silo.AddGarnetJournalStorage(options =>
{
    options.ConfigurationOptions = StackExchange.Redis.ConfigurationOptions.Parse("journal-host:6380");
});
```

The partition's RESP endpoint has no authentication, so expose it only on a trusted network.

## Behavior

- **Durability.** Garnet runs with `--aof --aof-commit-freq 0 --aof-commit-wait`. Replies, including replies to reads, are sent only after the AOF commit covering the operation is written to the device with write-through, unbuffered I/O.
- **Consistency.** Each operation runs as one transaction procedure. Writes are conditional on the content ETag the storage instance last observed, or on the journal's absence, so a stale writer fails with `InconsistentStateException`. Operations whose replies are lost are retried with the same ETags, and a retry of an operation that already committed reports success without applying it again.
- **Data model.** A journal's data is a list with one element per append, so appends do not copy earlier data. `ReplaceAsync` replaces the list with one element. `IsCompactionRequested` becomes `true` once the journal's appends since its last replacement reach `CompactionThresholdBytes` (8 MiB by default).
- **Catalog.** The provider implements `IJournalStorageCatalog` by listing each bucket's journal index.

## Configuration

| Option | Default | Description |
| --- | --- | --- |
| `DataDirectory` | none | Hosts a partition in this silo, with its durable state in this directory. |
| `ConfigurationOptions` | none | Connects to a partition hosted by another silo. Set exactly one of this and `DataDirectory`. |
| `BucketCount` | 4096 | Number of buckets journals hash into; a power of two, fixed when the partition is first initialized. |
| `CompactionThresholdBytes` | 8 MiB | Append volume at which compaction is requested. |
| `OperationTimeout` | 30 s | How long an operation retries while its partition is unreachable or its outcome is unknown. |
| `LogMemorySize` | `1g` | In-memory size of the partition's hybrid log; older records spill to `DataDirectory`. |
| `IndexMemorySize` | `64m` | Size of the partition's hash index. |
| `CheckpointAofSize` | `256m` | AOF size at which the partition takes a checkpoint, compacting its log and truncating the AOF. |
| `ListenEndPoint` | free loopback port | Endpoint on which the embedded partition accepts connections. |
