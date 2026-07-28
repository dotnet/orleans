---
title: Grain Directory Implementation
description: Explore the implementation of the grain directory in .NET Orleans.
ms.date: 05/23/2025
ms.topic: concept-article
---

# Grain directory implementation

## Overview and architecture

The grain directory in Orleans is a key-value store where the key is a grain identifier and the value is a registration entry pointing to an active silo that (potentially) hosts the grain.

While Orleans provides a default in-memory distributed directory implementation (described in this article), the grain directory system is designed to be pluggable. You can implement your own directory by implementing the `IGrainDirectory` interface and registering it with the silo's service collection. This allows for custom directory implementations that might use different storage backends or consistency models to better suit specific application requirements. Since the introduction of the new strong consistency directory, there's less need for external directory implementations, but the API remains for backward compatibility and flexibility. You can configure the grain directory on a per-grain-type basis.

To optimize performance, Orleans caches directory lookups locally within each silo. This means potentially remote directory reads are only necessary when the local cache entry is missing or invalid. This caching mechanism reduces network overhead and latency associated with grain location lookups.

Originally, Orleans implemented an eventually consistent directory structured as a distributed hash table. This was superseded by a strongly consistent directory in Orleans v9.0, based on the two-phase [Virtually Synchronous methodology](https://www.microsoft.com/en-us/research/publication/virtually-synchronous-methodology-for-dynamic-service-replication/). It's also structured as a distributed hash table but offers improved load balancing through virtual nodes. This article describes the latter, newer grain directory implementation.

## Distributed grain directory

The distributed grain directory in Orleans offers strong consistency, even load balancing, high performance, and fault tolerance. The implementation follows a two-phase design based on the [Virtual Synchrony methodology](https://www.microsoft.com/en-us/research/publication/virtually-synchronous-methodology-for-dynamic-service-replication/) with similarities to [Vertical Paxos](https://www.microsoft.com/en-us/research/publication/vertical-paxos-and-primary-backup-replication/).

Directory partitions have two modes of operation:

1. **Normal operation**: Partitions process requests locally without coordination with other hosts.
1. **View change**: Hosts coordinate with each other to transfer ownership of directory ranges.

The directory leverages Orleans' strong consistency cluster membership system, where configurations called "views" have monotonically increasing version numbers. As silos join and leave the cluster, successive views are created, resulting in changes to range ownership.

All directory operations include view coordination:

- Requests carry the caller's view number.
- Responses include the partition's view number.
- View number mismatches trigger synchronization.
- Requests automatically retry on view changes.

This ensures the correct owner of the directory partition processes all requests.

### Partitioning strategy

The directory is partitioned using a consistent hash ring, with ranges assigned to the active silos in the cluster. Grain identifiers are hashed to find the silo owning the section of the ring corresponding to its hash.

Each active silo owns a pre-configured number of ranges, defaulting to 30 ranges per silo. This is similar to the scheme used by [Amazon Dynamo](https://www.allthingsdistributed.com/files/amazon-dynamo-sosp2007.pdf) and [Apache Cassandra](https://docs.datastax.com/en/cassandra-oss/3.0/cassandra/architecture/archDataDistributeVnodesUsing.html), where multiple "virtual nodes" (ranges) are created for each physical node (host).

The size of a partition is determined by the distance between its hash and the hash of the next partition. It's possible for a range to be split among multiple silos during a view change. This adds complexity to the view change procedure, as each partition must potentially coordinate with multiple other partitions.

### View change procedure

Directory partitions (implemented in `GrainDirectoryPartition`) use versioned range locks to prevent invalid access to ranges during view changes. Range locks are created during a view change and released when the view change completes. These locks are analogous to the 'wedges' used in the Virtual Synchrony methodology.

When a view change occurs, a partition can either grow or shrink:

- If a new silo joins the cluster, existing partitions might shrink to make room.
- If a silo leaves the cluster, remaining partitions might grow to take over the orphaned ranges.

Directory registrations must transfer from the old owner to the new owner before requests can be served. The transfer process follows these steps:

1. The previous owner seals the range and creates a snapshot of its directory entries.
1. The new owner requests and applies the snapshot.
1. The new owner begins servicing requests for the range.
1. The previous owner is notified and deletes the snapshot.

### Recovery process

When a host crashes without properly handing off its directory partitions, the subsequent partition owners must perform recovery. This involves:

1. Querying all active silos in the cluster for their grain registrations.
1. Rebuilding the directory state for affected ranges.
1. Ensuring no duplicate grain activations occur.

Recovery is also necessary when cluster membership changes rapidly. While cluster membership guarantees monotonicity, it's possible for silos to miss intermediate membership views. In such cases:

- Snapshot transfers are abandoned.
- Recovery is performed instead of the normal partition-to-partition handover.
- The system maintains consistency despite missing intermediate states.

A future improvement to cluster membership might reduce or eliminate these scenarios by ensuring all silos see all views.
