---
title: Capacity planning and scaling
description: Size and scale Orleans clusters using measured workload and saturation signals.
ms.date: 08/12/2026
ms.topic: concept-article
---

# Capacity planning and scaling

Orleans distributes activations and work, but application behavior determines capacity. Grain count alone isn't a sizing metric: grains can be idle, CPU-intensive, memory-intensive, hot, or blocked on dependencies.

## Understand scale limits

Orleans doesn't impose a configured numeric maximum on the number of grain identities, active grain activations, or silos in a cluster. This isn't a guarantee that any cluster size or throughput is practical. The supported operating envelope ends where the application can no longer meet its latency, throughput, availability, or recovery objectives.

A possible grain identity consumes no activation or grain-directory entry until the grain is used. Therefore, millions of addressable identities can cost less than a much smaller set of simultaneously active, stateful, or frequently called grains. Measure active activations and their resource use instead of sizing from the number of keys which can exist.

Adding silos doesn't make every workload scale linearly:

- A single ordinary grain activation processes one turn at a time by default. Adding silos doesn't divide a hot grain's work.
- Every silo participates in membership and maintains a cluster view. Every advertised silo endpoint must be reachable from every other silo.
- Membership changes can move grain-directory ownership and other partitioned runtime responsibilities.
- Inter-silo calls, serialization, gateways, clustering, storage, reminders, streams, and telemetry can reach their limits before silo CPU or memory.
- Skewed keys, placement constraints, and shared dependencies can leave some silos saturated while others have capacity.

These costs don't establish a universal maximum, but they make very large clusters and frequent membership changes distinct test scenarios. Don't extrapolate a thousand-silo design or a failure-recovery target from a small, steady cluster. For the relevant control-plane behavior, see [Topology, networking, and clustering](networking.md), [Cluster membership protocol](../implementation/cluster-management.md), and [Grain directory architecture](../implementation/grain-directory.md).

## Establish a workload model

Measure production-like mixes of:

- Calls per second and latency objectives by operation.
- Active and total grain counts by type.
- Grain state size, read/write frequency, and serialization cost.
- Hot-key concentration and fan-out to other grains or services.
- Timers, reminders, streams, and background work.
- Payload sizes and client gateway traffic.
- Dependency latency, throttling, and connection limits.

Load tests should include bursts, one-silo loss, rolling replacement, dependency slowdown, and recovery after backlog accumulation.

## Measure activation throughput

There is no portable activations-per-second or deactivations-per-second limit. A cold call can include directory lookup and registration, placement, object construction, persistent-state reads, and application activation callbacks. Deactivation can include application callbacks, cleanup, and directory updates. Provider latency, state size, application lifecycle code, contention, and the number and size of silos therefore change the result.

Test these paths separately:

- **Warm steady state:** The target activations already exist.
- **Cold-start burst:** Many previously inactive grain identities receive their first call.
- **Sustained churn:** Activations are collected and soon needed again.
- **Recovery:** A restart or silo loss causes concurrent reactivation and state reads.

Record activation and deactivation rate and latency by grain type together with directory, storage, CPU, allocation, garbage collection, and request-latency signals. If churn is the bottleneck, remove unnecessary work from <xref:Orleans.Grain.OnActivateAsync*>, batch dependency access, and tune [activation collection](../host/configuration-guide/activation-collection.md) for the affected grain types. Retaining activations trades memory for fewer cold starts.

Don't model every storage page or scan item as a grain solely to parallelize a data operation. A grain per page can be appropriate when each page is an independently addressed consistency boundary, but a scan which immediately cold-activates many pages pays activation, messaging, and backend access for each page. Compare that design against coarser grains, bounded batches using the storage backend's range or batch APIs, or a dedicated indexing service. Bound fan-out in every design.

## Size silos

Choose a repeatable CPU and memory size, then measure:

- CPU saturation and scheduler delay.
- Allocation rate, garbage collection pause time, and working set.
- Activation count and per-activation memory.
- Request queues, rejection or load-shedding rate, and tail latency.
- Network connections and throughput.
- Provider latency and throttling.

Leave headroom for activation redistribution and traffic after losing a host or failure domain. Avoid very large silos when their restart and reactivation blast radius is unacceptable. Avoid very small silos when per-process runtime, connection, and membership overhead dominates.

CPU limits can throttle a process even when node CPU appears available. Memory limits can terminate a process without a managed out-of-memory exception. Set requests from observed steady-state use and set limits only with an understood platform policy and tested behavior.

## Find the operating envelope

Use the production runtime version, host shape, network, providers, serialization settings, and representative state. A useful capacity test proceeds as follows:

1. Define latency percentiles, completed throughput, error and timeout rates, and recovery objectives.
1. At a fixed cluster size, increase offered load until one objective fails or a resource saturates.
1. Repeat with larger cluster sizes and compare completed work, not only submitted work.
1. Repeat cold-start, hot-key, burst, scale-out, scale-in, rolling-upgrade, dependency-throttling, and silo-loss scenarios.
1. Select an operating point below the first sustained bottleneck and reserve headroom for the required failure domain.

Correlate client-visible results with per-silo CPU, scheduler delay, memory, garbage collection, network, activation distribution, request queues, rejected work, and provider latency or throttling. A balanced cluster average can hide one saturated silo or storage partition. Use the [observability signals](../host/monitoring/signals.md) emitted by the deployed Orleans version.

## Scale out and in

Scale out before saturation. Signals can include sustained CPU, scheduler delay, tail latency, activation pressure, gateway load shedding, and application queue depth. Don't scale on a single noisy metric.

Account for:

- Time to schedule a host, start the process, join membership, and warm caches.
- Capacity and connection impact on clustering and storage providers.
- Placement constraints and grains that concentrate work.
- The minimum number of silos required across failure domains.

Scale in more slowly than scale out. Select one instance at a time where possible, use [graceful shutdown](upgrades.md#graceful-shutdown-and-scale-in), and wait for cluster health to stabilize.

## Handle overload

Unlimited queues convert overload into high latency and memory pressure. Apply:

- Admission control at application ingress.
- Bounded queues and concurrency.
- Request deadlines that include downstream calls.
- Load shedding before the process becomes unresponsive.
- Per-tenant or per-key limits where one workload can starve others.

Retries consume capacity. Include retry traffic in the load model and use exponential backoff with jitter, a retry budget, and an end-to-end deadline.

## Choose a tenant topology

The choice between a shared cluster and a cluster per tenant is primarily an isolation and operations decision, not a grain-count limit:

| Topology | Benefits | Costs and risks |
|---|---|---|
| Shared cluster | Pools spare capacity and reduces the number of deployments and runtime dependencies. | Tenants share failure, deployment, provider, and capacity domains. Orleans doesn't automatically enforce tenant quotas or prevent noisy neighbors. |
| Cluster per tenant | Separates capacity, failures, deployments, credentials, and provider namespaces. | Adds baseline resource cost and operational work for upgrades, monitoring, recovery, and fleet-wide changes. |
| Sharded tenant pools | Limits blast radius and fleet size while retaining some capacity pooling. | Requires tenant placement, shard-capacity management, and a tenant-migration strategy. |

In a shared cluster, partition hot work by tenant and key, apply per-tenant admission and concurrency limits, and test the most skewed tenant behavior. Use separate clusters when security, data residency, independent upgrades, or fault and resource isolation require a hard boundary. A hybrid of multiple tenant pools is often preferable to assuming either one unbounded cluster or one deployment per small tenant.

## Revisit the model

Review capacity after changes to grain state, placement, serializers, providers, runtime versions, host sizes, or traffic shape. Keep a tested emergency capacity procedure that doesn't bypass identity, networking, or provider limits.
