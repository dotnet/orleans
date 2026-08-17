---
title: Capacity planning and scaling
description: Size and scale Orleans clusters using measured workload and saturation signals.
ms.date: 08/17/2026
ms.topic: concept-article
---

# Capacity planning and scaling

Orleans distributes activations and work, but application behavior determines capacity. Grain count alone isn't a sizing metric: grains can be idle, CPU-intensive, memory-intensive, hot, or blocked on dependencies.

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

## Scale out and in

Scale out before saturation. Signals can include sustained CPU, scheduler delay, tail latency, activation pressure, gateway load shedding, and application queue depth. Don't scale on a single noisy metric.

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
- <xref:Orleans.Configuration.LoadSheddingOptions> at the client gateway and stream providers before the process becomes unresponsive.
- Per-tenant or per-key limits where one workload can starve others.

Set <xref:Orleans.Configuration.LoadSheddingOptions.LoadSheddingEnabled> to `true` to activate runtime load shedding. It marks a silo overloaded after the configured CPU or memory threshold is exceeded, allows supported ingress work to be rejected, and causes resource-optimized placement to choose other candidates for new activations. Configure thresholds below hard platform limits, validate rejection and recovery behavior under load, and use a hosting-platform autoscaler to adjust cluster capacity.

Retries consume capacity. Include retry traffic in the load model and use exponential backoff with jitter, a retry budget, and an end-to-end deadline.

## Revisit the model

Review capacity after changes to grain state, placement, serializers, providers, runtime versions, host sizes, or traffic shape. Keep a tested emergency capacity procedure that doesn't bypass identity, networking, or provider limits.
