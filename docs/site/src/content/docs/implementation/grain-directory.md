---
title: Grain directory architecture
description: Understand the default view-synchronous distributed directory and the legacy local-directory DHT.
ms.date: 08/21/2026
ms.topic: concept-article
---

# Grain directory architecture

The grain directory maps a grain identity to an activation address. It is on the critical path when a caller has no usable cached address and when the runtime creates, moves, or removes an activation. Placement chooses a silo; the directory coordinates which activation address is authoritative.

Orleans uses `DistributedGrainDirectory` by default, with versioned range ownership and coordinated membership-view transitions. Applications can retain `LocalGrainDirectory` before migrating by selecting <xref:Orleans.Hosting.CoreHostingExtensions.UseLocalGrainDirectory*>.

## Legacy: `LocalGrainDirectory` <a name="overview-and-architecture"></a>

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

The local directory preserves these invariants:

- a directory key is derived from the grain identity, not its current location;
- the membership view determines the authoritative owner;
- local cache entries are hints and can be invalidated;
- registration detects competing single-activation addresses;
- a failed or deactivated silo's addresses are removed or rejected; and
- partition ownership transfers as the membership ring changes.

Message forwarding and invalidation repair stale caches. Forwarding is bounded and is not an application-level retry policy.

Source: [`LocalGrainDirectory`](https://github.com/dotnet/orleans/blob/main/src/Orleans.Runtime/GrainDirectory/LocalGrainDirectory.cs) and [`LocalGrainDirectoryPartition`](https://github.com/dotnet/orleans/blob/main/src/Orleans.Runtime/GrainDirectory/LocalGrainDirectoryPartition.cs).

## Directory selection

The runtime resolves a directory per grain type. The unnamed default resolves to `DistributedGrainDirectory` unless the silo has explicitly replaced it. A named implementation of <xref:Orleans.GrainDirectory.IGrainDirectory> can be registered and selected using grain-type metadata.

Custom directories own their consistency, availability, and cleanup behavior. They should define what concurrent registration means, how failed silos are removed, and whether stale reads are possible. The surrounding message router cannot turn an eventually consistent custom directory into a strongly consistent one.

## Default: `DistributedGrainDirectory` <a name="distributed-grain-directory"></a>

The default directory is view-synchronous, with versioned range ownership and coordinated membership-view transitions. <xref:Orleans.Hosting.CoreHostingExtensions.AddDistributedGrainDirectory*?displayProperty=nameWithType> selects it explicitly or registers a named alias. Aliases share one directory instance and one lifecycle participant per silo.

<a name="partitioning-strategy"></a>
The implementation divides the hash ring into configurable ranges, analogous to the virtual-node partitioning described by [Dynamo](https://www.allthingsdistributed.com/files/amazon-dynamo-sosp2007.pdf). <xref:Orleans.Configuration.GrainDirectoryOptions.PartitionsPerSilo?displayProperty=nameWithType> defaults to **1**. A partition normally serves requests locally. During a membership view change, old and new owners coordinate range locks, snapshots, and ownership transfer. The design applies the [virtually synchronous methodology for dynamic service replication](https://www.microsoft.com/en-us/research/publication/virtually-synchronous-methodology-for-dynamic-service-replication/) and has similarities to [Vertical Paxos and primary-backup replication](https://www.microsoft.com/en-us/research/publication/vertical-paxos-and-primary-backup-replication/).

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

## Local-to-distributed transition

During migration, compatibility system targets bridge the two directory protocols. A distributed-default silo serves legacy remote-directory requests through its distributed directory, so old callers and new callers reach the same authority. Local-directory silos expose compatibility endpoints for distributed partition requests and activation recovery. Directory registration and handoff retain each live activation's authoritative address as ownership moves.

The local-to-distributed transition is one-way. First upgrade all silo runtimes while explicitly retaining the local directory, then migrate the directory in a separate rolling deployment. Keep one partition per silo and the default boundary function during that migration. Existing experimental distributed-directory clusters retain their original partition identities, including the sorted 30-partition mapping used by earlier experimental releases. The [upgrade procedure](../host/grain-directory.md#local-to-distributed-directory-upgrade) defines deployment prerequisites and checkpoints.

## Tradeoffs

| Property | `LocalGrainDirectory` | Default `DistributedGrainDirectory` |
| --- | --- | --- |
| Selection | <xref:Orleans.Hosting.CoreHostingExtensions.UseLocalGrainDirectory*?displayProperty=nameWithType> before migration | Runtime default |
| Ownership | Membership consistent-hash ring | Versioned ranges over membership views |
| Normal lookup | Owner partition plus per-silo cache | Owner partition plus view coordination |
| View change | Partition split/merge and cache repair | Sealed ranges and snapshot transfer |
| Recovery emphasis | Duplicate detection and invalidation | Explicit range recovery |
| Configuration | Explicit migration deferral | <xref:Orleans.Configuration.GrainDirectoryOptions.PartitionsPerSilo?displayProperty=nameWithType>, default 1 |

More partitions can improve ownership granularity but increase transfer and coordination work. Production rollouts should include compatibility, failure, and load testing.
