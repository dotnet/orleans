---
title: Grain directory architecture
description: Compare the default LocalGrainDirectory DHT with the experimental distributed directory.
ms.date: 08/02/2026
ms.topic: concept-article
---

# Grain directory architecture

The grain directory maps a grain identity to an activation address. It is on the critical path when a caller has no usable cached address and when the runtime creates, moves, or removes an activation. Placement chooses a silo; the directory coordinates which activation address is authoritative.

Orleans uses `LocalGrainDirectory` by default. `DistributedGrainDirectory` is experimental and must be enabled explicitly.

## Default: `LocalGrainDirectory` <a name="overview-and-architecture"></a>

`LocalGrainDirectory` partitions registrations over the membership ring. Hashing a grain identity selects the silo whose local `LocalGrainDirectoryPartition` is authoritative for that key. This follows the broad consistent-hashing distributed-hash-table model described by [Chord](https://pdos.csail.mit.edu/papers/chord:sigcomm01/chord_sigcomm.pdf), adapted to Orleans membership and activation semantics. Each silo also keeps non-authoritative cache entries to avoid repeated remote lookups.

```mermaid
flowchart LR
    Caller[Calling silo]
    Cache[Local address cache]
    Ring[Membership hash ring]
    Owner[Owning LocalGrainDirectory]
    Partition[LocalGrainDirectoryPartition]
    Activation[Target activation]

    Caller --> Cache
    Cache -->|miss or invalid| Ring
    Ring --> Owner
    Owner --> Partition
    Partition -->|activation address| Caller
    Caller --> Activation
```

The default directory preserves these invariants:

- a directory key is derived from the grain identity, not its current location;
- the membership view determines the authoritative owner;
- local cache entries are hints and can be invalidated;
- registration detects competing single-activation addresses;
- a failed or deactivated silo's addresses are removed or rejected; and
- partition ownership transfers as the membership ring changes.

Message forwarding and invalidation repair stale caches. Forwarding is bounded and is not an application-level retry policy.

Source: [`LocalGrainDirectory`](https://github.com/dotnet/orleans/blob/main/src/Orleans.Runtime/GrainDirectory/LocalGrainDirectory.cs) and [`LocalGrainDirectoryPartition`](https://github.com/dotnet/orleans/blob/main/src/Orleans.Runtime/GrainDirectory/LocalGrainDirectoryPartition.cs).

## Directory selection

The runtime resolves a directory per grain type. The unnamed default resolves to `LocalGrainDirectory` unless the silo has explicitly replaced it. A named implementation of <xref:Orleans.GrainDirectory.IGrainDirectory> can be registered and selected using grain-type metadata.

Custom directories own their consistency, availability, and cleanup behavior. They should define what concurrent registration means, how failed silos are removed, and whether stale reads are possible. The surrounding message router cannot turn an eventually consistent custom directory into a strongly consistent one.

## Experimental: `DistributedGrainDirectory` <a name="distributed-grain-directory"></a>

<xref:Orleans.Hosting.CoreHostingExtensions.AddDistributedGrainDirectory*?displayProperty=nameWithType> opts into a view-synchronous directory marked with compiler warning **`ORLEANSEXP003`**:

It is not the default. The experimental status allows its API and protocol to evolve.

<a name="partitioning-strategy"></a>
The implementation divides the hash ring into configurable ranges, analogous to the virtual-node partitioning described by [Dynamo](https://www.allthingsdistributed.com/files/amazon-dynamo-sosp2007.pdf). <xref:Orleans.Configuration.GrainDirectoryOptions.PartitionsPerSilo?displayProperty=nameWithType> defaults to **1**, not 30. A partition normally serves requests locally. During a membership view change, old and new owners coordinate range locks, snapshots, and ownership transfer. The design applies the [virtually synchronous methodology for dynamic service replication](https://www.microsoft.com/en-us/research/publication/virtually-synchronous-methodology-for-dynamic-service-replication/) and has similarities to [Vertical Paxos and primary-backup replication](https://www.microsoft.com/en-us/research/publication/vertical-paxos-and-primary-backup-replication/).

<a name="view-change-procedure"></a>
```mermaid
sequenceDiagram
    participant Old as Previous range owner
    participant New as New range owner
    participant Clients as Directory callers

    Old->>Old: Seal range for new view
    Clients->>Old: Request with view number
    Old-->>Clients: Synchronize or retry in newer view
    New->>Old: Request range snapshot
    Old-->>New: Registrations and version
    New->>New: Install snapshot and open range
    New-->>Old: Transfer complete
    Old->>Old: Delete transferred snapshot
```

<a name="recovery-process"></a>
Requests and responses carry view information. A range cannot serve a request under an incompatible ownership view. If an orderly transfer is impossible, the new owner recovers registrations by querying active silos rather than assuming the failed owner's state.

API: <xref:Orleans.Hosting.CoreHostingExtensions.AddDistributedGrainDirectory*?displayProperty=nameWithType> and <xref:Orleans.Configuration.GrainDirectoryOptions>. Implementation: [hosting registration](https://github.com/dotnet/orleans/blob/main/src/Orleans.Runtime/Hosting/CoreHostingExtensions.cs) and [`DistributedGrainDirectory`](https://github.com/dotnet/orleans/blob/main/src/Orleans.Runtime/GrainDirectory/DistributedGrainDirectory.cs).

### Range-aware activation storage

The activation directory uses a concurrent hash table whose buckets cover contiguous portions of the 32-bit hash ring. Point lookup, insertion, replacement, expected-value removal, and range enumeration access the same entries. A concrete struct implementing `IConsistentHashComparer<GrainId>` supplies key equality and the stable unsigned `GrainId.GetUniformHashCode()` hash shared by silos. The JIT can specialize comparer calls for that struct. Hashes are inexpensive to compute from the grain type and key's cached hashes, so each table entry stores its key, value, and next link. Internal callers can reuse a precomputed comparer hash for point lookup or enumerate every entry at a hash coordinate.

Recovery enumerates buckets intersecting the requested range and computes hashes only for entries in boundary buckets, using the exclusive-start, inclusive-end convention. Fully covered buckets contribute all their entries. Wrapped ranges visit each bucket once. The table grows by splitting hash prefixes, refining range selection as the activation population increases. For uniformly distributed activations, selective queries examine matching entries plus entries from at most two boundary buckets. A concentrated hash distribution increases boundary scanning and write contention within the affected buckets.

Enumeration captures one table generation and observes concurrent changes to that table. Resizing publishes a new table after copying entries under the writer locks, while readers can finish traversing the prior generation. Writers check the table generation after acquiring their bucket's lock and retry when it changes. The recovery membership watermark advances before enumeration, and the activation-registration protocol ensures that registrations racing with recovery complete at an appropriate membership version. Expected-value removal preserves a newer context when an older context finishes deactivating. Ownership handoff transfers registration records; running contexts continue through their activation lifecycle.

## Tradeoffs

| Property | Default `LocalGrainDirectory` | Experimental `DistributedGrainDirectory` |
| --- | --- | --- |
| Status | Default | Opt-in, `ORLEANSEXP003` |
| Ownership | Membership consistent-hash ring | Versioned ranges over membership views |
| Normal lookup | Owner partition plus per-silo cache | Owner partition plus view coordination |
| View change | Partition split/merge and cache repair | Sealed ranges and snapshot transfer |
| Recovery emphasis | Duplicate detection and invalidation | Explicit range recovery |
| Configuration | Existing default behavior | <xref:Orleans.Configuration.GrainDirectoryOptions.PartitionsPerSilo?displayProperty=nameWithType>, default 1 |

More partitions can improve ownership granularity but increase transfer and coordination work. This page documents the mechanism; any production rollout of an experimental component should include compatibility, failure, and load testing.
