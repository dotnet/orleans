---
title: Load balancing
description: Learn how .NET Orleans manages load balancing.
ms.date: 05/23/2025
ms.topic: concept-article
---

# Load balancing

Load balancing, in a broad sense, is one of the pillars of the Orleans runtime. The Orleans runtime tries to keep everything balanced, as balancing maximizes resource usage, avoids hotspots, leads to better performance, and helps with elasticity. Load balancing in Orleans applies in multiple places. Below is a non-exhaustive list of where the runtime performs balancing:

1. The default actor placement strategy is random; new activations are placed randomly across silos. This results in balanced placement and prevents hotspots for most scenarios.
1. A more advanced <xref:Orleans.Runtime.ActivationCountBasedPlacement> strategy tries to equalize the number of activations on all silos, resulting in a more even distribution across silos. This is especially important for elasticity.
1. The grain directory service builds on top of a distributed hash table, which is inherently balanced. The directory service maps grains to activations. Each silo owns part of the global mapping table, and this table is globally partitioned in a balanced way across all silos using consistent hashing with virtual buckets.
1. Clients connect to all gateways and spread their requests across them in a balanced way.
1. The reminder service is a distributed, partitioned runtime service. The assignment of which silo is responsible for serving which reminder is balanced across all silos via consistent hashing, just like the grain directory.
1. Performance-critical components within a silo are partitioned, and the work across them is locally balanced. This allows the silo runtime to fully utilize all available CPU cores and avoid in-silo bottlenecks. This applies to all local resources: allocation of work to threads, sockets, dispatch responsibilities, queues, etc.
1. The <xref:Orleans.Streams.QueueBalancerBase> balances the responsibility of pulling events from persistence queues across silos in the cluster.

Balancing doesn't necessarily mean loss of locality. You can achieve balance while still maintaining good locality. For example, when balancing involves sharding/partitioning, you can partition responsibility for a certain logical task while maintaining locality within each partition. This applies to both local and distributed balancing.
