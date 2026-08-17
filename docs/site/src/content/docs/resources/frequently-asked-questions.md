---
title: Frequently asked questions
description: Answers to common questions about developing and operating Orleans applications.
ms.date: 08/12/2026
ms.topic: faq
---

# Frequently asked questions

For symptom-based diagnosis, start with the [troubleshooting symptom catalog](../host/monitoring/troubleshooting-symptom-catalog.md), then follow the [incident runbook](../host/monitoring/troubleshooting.md). For task-oriented setup and deployment recipes, see the [how-to guide index](../how-to/index.md).

## Availability and support

### Can I use Orleans in my project?

Yes. Orleans is open source under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE), and official packages are published on [NuGet.org](https://www.nuget.org/profiles/Orleans).

### Is Orleans production ready?

Yes. Orleans began in Microsoft Research and has powered production services since 2011. The project is developed in the open at [dotnet/orleans](https://github.com/dotnet/orleans).

### Which .NET target frameworks does Orleans support?

Orleans libraries target `net8.0` and `net10.0`.

### Where do I get help?

Use [GitHub issues](https://github.com/dotnet/orleans/issues) for reproducible bugs and feature proposals. Use [GitHub Discussions](https://github.com/dotnet/orleans/discussions) or [Discord](https://aka.ms/orleans-discord) for questions and design conversations.

## Hosting

### Is Orleans a server product?

Orleans is a set of .NET libraries used to build an application. An application hosts one or more Orleans silos in processes which it deploys and operates.

### Where can Orleans run?

Orleans runs wherever its supported .NET targets run, including Linux and Windows hosts, containers, Kubernetes, Azure services, AWS, other clouds, on-premises environments, and developer machines.

### How many grains or silos can a cluster contain?

Grain identities form a virtual address space, and Orleans activates grains on demand across available silos. Practical active-grain and silo counts depend on the workload and deployment environment.

Use production-like capacity tests to determine the appropriate cluster size and headroom for the application's performance and recovery objectives. See [Capacity planning and scaling](../deployment/capacity-planning.md).

### Is Orleans tied to Azure?

Orleans runs across clouds and hosting environments. Its provider ecosystem includes Azure services, relational databases, DynamoDB, Redis, Cassandra, Consul, ZooKeeper, NATS, SQS, and custom providers.

### Can browsers or mobile apps connect directly to silos?

Place an authenticated application protocol such as HTTPS, SignalR, or another API layer between public clients and Orleans silo or gateway endpoints.

## Grains

### How large should a grain be?

Model a grain around a domain entity or consistency boundary. A grain is probably too large if one key becomes a throughput bottleneck or owns excessive state. It might be too small if one operation requires many chatty calls between grains. Measure representative workloads rather than relying on a fixed state-size or calls-per-second rule.

### Does Orleans replicate grain state automatically?

An ordinary stateful grain normally has one activation in the cluster. Volatile state follows that process lifetime. A configured storage provider and successful grain writes provide durable recovery. Applications design and operate any replicas or caches required by their workload.

### How do I avoid hot grains?

An ordinary grain activation processes one turn at a time by default, so a single key can become a bottleneck even when the rest of the cluster has capacity. Partition work across keys, use staged or hierarchical aggregation, batch calls, or use stateless workers for suitable stateless operations. Moving an activation to a less-loaded host or closer to the grains it calls can improve throughput by reducing contention, RPC overhead, and latency. Partitioning the key adds concurrency.

For example, if many grains regularly report counters or statistics, hash each reporter's stable key across a controlled set of intermediate aggregator grains. Each intermediate grain combines updates and periodically sends partial results to a final aggregator. This distributes the reporting load and reduces the number of turns at the central grain; add another level if one stage still receives too much fan-in. Choose the shard count and reporting cadence from load tests, and design persistence, idempotency, or reconciliation when losing or repeating an update would matter.

### Can I choose where a grain activates?

Yes. Orleans includes placement strategies and supports custom placement. Prefer location transparency unless the application has a measured locality or resource requirement, since restrictive placement can reduce the runtime's options during failures and scaling.

### How do I deactivate a grain?

Usually, let Orleans deactivate idle activations. When a grain knows it should be removed after the current turn, it can call <xref:Orleans.Grain.DeactivateOnIdle*>.

## Failures and calls

### What happens when a silo fails during a call?

The call can fail or time out. After the cluster detects the failed silo, a later call can activate the grain on a healthy silo. The caller should use bounded retries only when the operation is safe to retry. Durable state is available only if it was written successfully to an available durable provider.

### Are grain calls exactly once?

Orleans uses at-most-once message delivery by default. Network failures can leave a caller uncertain whether an operation ran, so retryable operations should be idempotent or carry an application-level deduplication identity.

### What happens when grain code runs too long?

Orleans uses cooperative scheduling. A grain turn runs until it yields or completes, and long synchronous work delays other turns on that scheduler. Keep turns short, await I/O, and move substantial CPU-bound work to an appropriate execution model.

### How do I upgrade an existing application?

Follow the [migration guide](../migration-guide.md), which contains version-specific upgrade history. Conceptual and tutorial documentation describes the supported APIs without repeating upgrade history.

### How should I retry failed grain calls?

Retry transient failures when the operation is idempotent or carries an application-level deduplication identity. Send deterministic validation, serialization, and concurrency failures directly to their handling or reconciliation paths. Use a bounded retry budget with backoff and jitter at an appropriate boundary. A timeout or connection loss can leave the outcome unknown, so reconcile business state when duplicate execution would be unsafe.

### Can reentrancy fix a grain call deadlock?

A call cycle can stall when a non-reentrant grain awaits a call which eventually returns through that grain. Reentrancy can allow that callback to run and also permits interleaving, so the grain's state invariants must cover those transitions. Remove synchronous blocking, redesign avoidable cycles, and use <xref:Orleans.Concurrency.ReentrantAttribute> or <xref:Orleans.Concurrency.AlwaysInterleaveAttribute> for operations whose interleavings are understood and tested. See [Long-running or deadlocked grain turns](../host/monitoring/troubleshooting-symptom-catalog.md#long-running-or-deadlocked-grain-turns).

## Operations

### How many silos do I need?

Derive the silo count from peak CPU, memory, connection, and dependency budgets, planned failure-domain loss, rollout surge, and representative load tests. Measure with representative key distribution, payloads, fan-out, storage, streams, and failure recovery. See [Capacity planning](../deployment/capacity-planning.md).

### Why didn't adding silos improve throughput?

The bottleneck might be a hot grain, restrictive placement, a shared storage or stream provider, gateway admission, network bandwidth, or another dependency. Adding silos expands distributed capacity, while an ordinary stateful grain retains one activation for its key. Compare per-silo utilization, activation distribution, rejections, long-running turns, and dependency latency before scaling further.

### What determines Orleans cost?

Orleans uses the MIT license. Deployment cost comes from compute, memory, network traffic, clustering and persistence providers, streams, telemetry, and redundancy. Grain boundaries affect cost: chatty cross-silo calls, large serialized payloads, frequent state writes, high activation counts, and high-cardinality telemetry can dominate. Measure a representative workload and include failure-domain spare capacity, rollout surge, backups, and observability retention.

### Which grain storage provider should I choose?

Choose from durability, consistency/concurrency behavior, latency, throughput and partition limits, regional availability, backup/restore, security, operational familiarity, and cost. Clustering, grain storage, reminders, and streams can use separate backends selected for their access patterns. Validate the provider's failure and throttling behavior with your state size and key distribution.

### Does storage throttling only affect persistence calls?

Storage throttling propagates into activation, grain-turn, scheduler, backend, and request latency. A delayed state read delays activation; a delayed write holds a grain turn; retries consume scheduler and backend capacity; and resulting queues produce request timeouts. Correlate storage latency, throttling, retries, activation rate, and request latency. See [Storage throttling](../host/monitoring/troubleshooting-symptom-catalog.md#storage-throttling).

### Can one Orleans cluster span multiple regions?

A single cluster requires reliable, sufficiently low-latency connectivity among silos and to its membership and directory dependencies. A wide-area network increases latency and the probability of partitions, and it expands the failure domain. Prefer independent regional clusters with explicit application-level routing, replication, ownership, and failover semantics unless testing proves that one stretched cluster meets the application's availability and consistency requirements.

### How do I prevent split brain?

Use one shared, durable membership provider per cluster, stable cluster identity, mutually reachable advertised endpoints, clock synchronization, and infrastructure that detects, isolates, and terminates stale silos within the planned convergence interval. During a suspected partition, isolate stale members before editing membership records. Applications which require cross-region survival should define ownership and reconciliation across clusters. See [Membership and directory churn](../host/monitoring/troubleshooting-symptom-catalog.md#membership-and-directory-churn).

### Can membership churn create duplicate grain activations?

During failure detection and directory convergence, transient duplicate activations can occur. The runtime resolves directory conflicts, while applications provide safe semantics for external side effects and custom directory implementations. Grain storage concurrency tokens detect conflicting storage writes and surface provider conflicts; applications provide idempotency or deduplication for external side effects. Investigate repeated duplication as a membership, network, or directory health symptom.

### What's the difference between a grain timer and a reminder?

A timer provides activation-scoped scheduling and ends with that activation. A reminder stores its schedule through the configured reminder provider and can reactivate a grain after a silo or activation restart. Both provide best-effort scheduled callbacks, and application work should tolerate delay and repetition. See [Reminder and timer timing](../host/monitoring/troubleshooting-symptom-catalog.md#reminder-and-timer-timing).

### Why did reminders or timers run late?

Scheduler pressure, CPU or GC pauses, a callback which takes longer than its period, silo restarts, membership changes, and reminder-provider latency can all delay callbacks. Determine which mechanism is in use, compare callback duration with its period, and correlate timing with runtime and provider health. Use durable application state to reconcile time-sensitive business work.

### How do I make rolling upgrades safe?

Keep adjacent versions compatible in both directions for grain interfaces, serializers, stored state, queued/streamed payloads, provider schemas, and configuration. Preserve stable serializer member IDs and aliases, limit rollout concurrency, maintain surge capacity, and define rollback before deployment. Test old-to-new and new-to-old calls plus reads of existing data. See [Graceful shutdown and upgrades](../deployment/upgrades.md).

### Why did serialization start failing after deployment?

Mixed versions might disagree about a type contract, serializer registration, member ID, alias, or package version, or the new version might be unable to read persisted or queued old data. Preserve the failing type and payload source, stop the rollout, and use a compatible reader or rollback before migrating data. See [Serialization failures after a version change](../host/monitoring/troubleshooting-symptom-catalog.md#serialization-failures-after-a-version-change).

### What should I collect before restarting an unhealthy silo?

Capture UTC timestamps, deployment and configuration changes, logs from relevant clients and silos, traces, metrics, membership views, advertised endpoints, provider health, and platform events. For scheduler, CPU, or memory problems, capture a bounded process trace or dump when safe. Redact secrets and grain state. Start with the [Orleans symptom and signal catalog](../host/monitoring/troubleshooting-symptom-catalog.md).
