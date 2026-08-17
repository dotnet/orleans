---
title: Capacity planning and scaling
description: Size and scale Orleans clusters using measured workload and saturation signals.
ms.date: 08/17/2026
ms.topic: concept-article
---

# Capacity planning and scaling

Orleans distributes activations and work across silos. Capacity follows the active workload: grains can be idle, CPU-intensive, memory-intensive, hot, or blocked on dependencies.

## Plan for scale

Orleans distributes grain activations and request processing across silos. Applications address grain identities directly, Orleans creates activations on demand, and adding silos increases capacity for workload that partitions across activations.

The practical operating envelope is application- and environment-dependent. Determine it using representative tests of the workload, host resources, network, storage and clustering providers, external dependencies, and recovery requirements. Activation resource consumption begins with a grain identity's first use, so size the cluster from active workload and resource use.

For the mechanisms which support scale-out, see [Topology, networking, and clustering](networking.md), [Cluster membership protocol](../implementation/cluster-management.md), and [Grain directory architecture](../implementation/grain-directory.md).

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

Activation and deactivation throughput varies with provider latency, state size, application lifecycle code, contention, and silo shape. A cold call can include directory lookup and registration, placement, object construction, persistent-state reads, and application activation callbacks. Deactivation can include application callbacks, cleanup, and directory updates.

Test these paths separately:

- **Warm steady state:** The target activations already exist.
- **Cold-start burst:** Many previously inactive grain identities receive their first call.
- **Sustained churn:** Activations are collected and soon needed again.
- **Recovery:** A restart or silo loss causes concurrent reactivation and state reads.

Record the built-in aggregate activation and deactivation counters and latencies together with directory, storage, CPU, allocation, garbage collection, and request-latency signals. Add application instrumentation around grain lifecycle code when analysis requires rates or latencies by grain type. If churn is the bottleneck, remove unnecessary work from <xref:Orleans.Grain.OnActivateAsync*>, batch dependency access, and tune [activation collection](../host/configuration-guide/activation-collection.md) for the affected grain types. Retaining activations trades memory for fewer cold starts.

Model a storage page as a grain when it forms an independently addressed consistency boundary. A scan which immediately cold-activates many pages pays activation, messaging, and backend access for each page. Compare that design with coarser grains, bounded batches using the storage backend's range or batch APIs, or a dedicated indexing service. Bound fan-out in every design.

## Size silos

Choose a repeatable CPU and memory size, then measure:

- CPU saturation and scheduler delay.
- Allocation rate, garbage collection pause time, and working set.
- Activation count and per-activation memory.
- Request queues, rejection or load-shedding rate, and tail latency.
- Network connections and throughput.
- Provider latency and throttling.

Leave headroom for activation redistribution and traffic after losing a host or failure domain. Choose silo sizes which balance restart and reactivation blast radius against per-process runtime, connection, and membership overhead.

CPU limits can throttle a process even when node CPU appears available. Memory limits can terminate a process at the platform boundary before managed allocation reports an out-of-memory exception. Set requests from observed steady-state use and set limits with an understood platform policy and tested behavior.

## Find the operating envelope

Use the production runtime version, host shape, network, providers, serialization settings, and representative state. A useful capacity test proceeds as follows:

1. Define latency percentiles, completed throughput, error and timeout rates, and recovery objectives.
1. At a fixed cluster size, increase offered load until one objective fails or a resource saturates.
1. Repeat with larger cluster sizes and use completed throughput alongside submitted work to select the operating envelope.
1. Repeat cold-start, hot-key, burst, scale-out, scale-in, rolling-upgrade, dependency-throttling, and silo-loss scenarios.
1. Select an operating point below the first sustained bottleneck and reserve headroom for the required failure domain.

Correlate client-visible results with per-silo CPU, scheduler delay, memory, garbage collection, network, activation distribution, request queues, rejected work, and provider latency or throttling. A balanced cluster average can hide one saturated silo or storage partition. Use the [observability signals](../host/monitoring/signals.md) emitted by the deployed Orleans version.

## Scale out and in

Scale out before saturation. Base scaling decisions on correlated trends in sustained CPU, scheduler delay, tail latency, activation pressure, gateway load shedding, and application queue depth.

Adding a silo expands the candidate set for later resource-optimized placement decisions. Existing active grains continue running on their current silos. New activations can use the added capacity immediately after membership converges, and grains which are collected or otherwise deactivated can use it when they reactivate. The experimental activation rebalancer can migrate eligible grains to reduce count and memory skew; the experimental activation repartitioner instead migrates eligible grains to improve call locality. See [Grain placement and migration](../grains/grain-placement.md#scale-out-and-scale-in).

Account for:

- Time to schedule a host, start the process, join membership, and warm caches.
- Capacity and connection impact on clustering and storage providers.
- State-read and serialization load when many grains activate on new or remaining silos.
- Placement constraints and grains that concentrate work.
- The minimum number of silos required across failure domains.

Scale in more slowly than scale out. Select one instance at a time where possible, use [graceful shutdown](upgrades.md#graceful-shutdown-and-scale-in), and wait for cluster health to stabilize. Ordinary activations on the departing silo deactivate during shutdown. Leave enough capacity for remaining silos to handle reactivation, state loading, and redirected traffic.

## Handle overload

Unlimited queues convert overload into high latency and memory pressure. Apply:

- Admission control at application ingress.
- Bounded queues and concurrency.
- Request deadlines that include downstream calls.
- Client-gateway request rejection and stream queue flow control through <xref:Orleans.Configuration.LoadSheddingOptions>.
- Per-tenant or per-key limits where one workload can starve others.

Set <xref:Orleans.Configuration.LoadSheddingOptions.LoadSheddingEnabled> to `true` to activate runtime load shedding. Crossing its CPU or memory threshold marks the silo overloaded, enables client-gateway request rejection, and makes resource-optimized placement favor non-overloaded candidates. Stream queue flow control uses CPU thresholds to pause reads. Configure thresholds below hard platform limits, validate rejection and recovery behavior under load, and use a hosting-platform autoscaler to adjust cluster capacity.

Retries consume capacity. Include retry traffic in the load model and use exponential backoff with jitter, a retry budget, and an end-to-end deadline.

## Choose a tenant topology

Choose a tenant topology from the required isolation and operational boundaries:

| Topology | Benefits | Costs and risks |
|---|---|---|
| Shared cluster | Pools spare capacity and reduces the number of deployments and runtime dependencies. | Tenants share failure, deployment, provider, and capacity domains. Applications enforce tenant quotas and noisy-neighbor controls through admission, concurrency, and resource policies. |
| Cluster per tenant | Separates capacity, failures, deployments, credentials, and provider namespaces. | Adds baseline resource cost and operational work for upgrades, monitoring, recovery, and fleet-wide changes. |
| Sharded tenant pools | Limits blast radius and fleet size while retaining some capacity pooling. | Requires tenant placement, shard-capacity management, and a tenant-migration strategy. |

In a shared cluster, partition hot work by tenant and key, apply per-tenant admission and concurrency limits, and test the most skewed tenant behavior. Use separate clusters when security, data residency, independent upgrades, or fault and resource isolation require a hard boundary. A hybrid of multiple tenant pools is often preferable to assuming either one unbounded cluster or one deployment per small tenant.

## Revisit the model

Review capacity after changes to grain state, placement, serializers, providers, runtime versions, host sizes, or traffic shape. Keep a tested emergency capacity procedure which preserves identity, networking, and provider limits.
