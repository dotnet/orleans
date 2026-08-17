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

Orleans is designed to scale out by distributing grain activations and request processing across silos. It doesn't impose an inherent upper limit on grain identities, activations, or silos. The practical operating envelope depends on the application workload and deployment environment.

Use production-like capacity tests to determine the appropriate cluster size and headroom for the application's performance and recovery objectives. See [Capacity planning and scaling](../deployment/capacity-planning.md).

### Is Orleans tied to Azure?

No. Orleans has optional Azure providers, but also includes providers for relational databases, DynamoDB, Redis, Cassandra, Consul, ZooKeeper, NATS, SQS, and other infrastructure. Applications can implement custom providers where needed.

### Can browsers or mobile apps connect directly to silos?

Don't expose Orleans silo or gateway endpoints directly to untrusted public clients. Put an authenticated application protocol such as HTTPS, SignalR, or another API layer in front of the cluster.

## Grains

### How large should a grain be?

Model a grain around a domain entity or consistency boundary. A grain is probably too large if one key becomes a throughput bottleneck or owns excessive state. It might be too small if one operation requires many chatty calls between grains. Measure representative workloads rather than relying on a fixed state-size or calls-per-second rule.

### Does Orleans replicate grain state automatically?

No. An ordinary stateful grain normally has one activation in the cluster. Volatile state is lost if that process fails. Durable recovery requires a configured storage provider and successful writes by the grain. Applications which need replicas or caches must design and operate them explicitly.

### How do I avoid hot grains?

An ordinary grain activation processes one turn at a time by default, so a single key can become a bottleneck even when the rest of the cluster has capacity. Partition work across keys, use staged or hierarchical aggregation, batch calls, or use stateless workers for suitable stateless operations. Moving an activation to a less-loaded host or closer to the grains it calls can improve throughput by reducing contention, RPC overhead, and latency, but placement alone doesn't partition a hot key or add concurrency to an ordinary grain activation.

For example, if many grains regularly report counters or statistics, hash each reporter's stable key across a controlled set of intermediate aggregator grains. Each intermediate grain combines updates and periodically sends partial results to a final aggregator. This distributes the reporting load and reduces the number of turns at the central grain; add another level if one stage still receives too much fan-in. Choose the shard count and reporting cadence from load tests, and design persistence, idempotency, or reconciliation when losing or repeating an update would matter.

### Can I choose where a grain activates?

Yes. Orleans includes placement strategies and supports custom placement. Prefer location transparency unless the application has a measured locality or resource requirement, since restrictive placement can reduce the runtime's options during failures and scaling.

### How do I deactivate a grain?

Usually, let Orleans deactivate idle activations. When a grain knows it should be removed after the current turn, it can call <xref:Orleans.Grain.DeactivateOnIdle*>.

## Failures and calls

### What happens when a silo fails during a call?

The call can fail or time out. After the cluster detects the failed silo, a later call can activate the grain on a healthy silo. The caller should use bounded retries only when the operation is safe to retry. Durable state is available only if it was written successfully to an available durable provider.

### Are grain calls exactly once?

No. Orleans uses at-most-once message delivery by default. Network failures can leave a caller uncertain whether an operation ran, so retryable operations should be idempotent or carry an application-level deduplication identity.

### What happens when grain code runs too long?

Orleans uses cooperative scheduling and doesn't preempt grain code. Long synchronous work blocks other turns on that scheduler. Keep turns short, await I/O, and move substantial CPU-bound work to an appropriate execution model.

### How do I upgrade an existing application?

Follow the [migration guide](../migration-guide.md), which contains version-specific upgrade history. Conceptual and tutorial documentation describes the supported APIs without repeating upgrade history.

### How should I retry failed grain calls?

Retry only transient failures and only when the operation is idempotent or carries an application-level deduplication identity. Use a bounded retry budget with backoff and jitter at an appropriate boundary. Don't retry deterministic validation, serialization, or concurrency failures. A timeout or connection loss can leave the outcome unknown, so reconcile business state when duplicate execution would be unsafe.

### Can reentrancy fix a grain call deadlock?

Sometimes a call cycle can't progress because a non-reentrant grain is waiting for a call which eventually calls back into it. Reentrancy can allow that callback to run, but it also permits interleaving and can violate state invariants. First remove synchronous blocking and redesign avoidable cycles. Use <xref:Orleans.Concurrency.ReentrantAttribute> or <xref:Orleans.Concurrency.AlwaysInterleaveAttribute> only for operations whose interleavings are understood and tested. See [Long-running or deadlocked grain turns](../host/monitoring/troubleshooting-symptom-catalog.md#long-running-or-deadlocked-grain-turns).

## Operations

### How many silos do I need?

There is no universal count. Keep enough silos to satisfy peak CPU, memory, connection, and dependency budgets after losing the largest planned failure domain and while a rollout is in progress. Measure with representative key distribution, payloads, fan-out, storage, streams, and failure recovery. See [Capacity planning](../deployment/capacity-planning.md).

### Why didn't adding silos improve throughput?

The bottleneck might be a hot grain, restrictive placement, a shared storage or stream provider, gateway admission, network bandwidth, or another dependency. An ordinary stateful grain has one activation and adding silos doesn't partition that key. Compare per-silo utilization, activation distribution, rejections, long-running turns, and dependency latency before scaling further.

### What determines Orleans cost?

Orleans itself has no license fee. Operational cost comes from compute, memory, network traffic, clustering and persistence providers, streams, telemetry, and redundancy. Grain boundaries affect cost: chatty cross-silo calls, large serialized payloads, frequent state writes, high activation counts, and high-cardinality telemetry can dominate. Measure a representative workload and include failure-domain spare capacity, rollout surge, backups, and observability retention.

### Which grain storage provider should I choose?

Choose from durability, consistency/concurrency behavior, latency, throughput and partition limits, regional availability, backup/restore, security, operational familiarity, and cost. Clustering, grain storage, reminders, and streams have different access patterns and don't need to share one backend. Validate the provider's failure and throttling behavior with your state size and key distribution.

### Does storage throttling only affect persistence calls?

No. A delayed state read can delay activation; a delayed write can hold a grain turn; retries consume more scheduler and backend capacity; and resulting queues can produce unrelated-looking request timeouts. Correlate storage latency, throttling, retries, activation rate, and request latency. See [Storage throttling](../host/monitoring/troubleshooting-symptom-catalog.md#storage-throttling).

### Can one Orleans cluster span multiple regions?

A single cluster requires reliable, sufficiently low-latency connectivity among silos and to its membership and directory dependencies. A wide-area network increases latency and the probability of partitions, and it expands the failure domain. Prefer independent regional clusters with explicit application-level routing, replication, ownership, and failover semantics unless testing proves that one stretched cluster meets the application's availability and consistency requirements.

### How do I prevent split brain?

Use one shared, durable membership provider per cluster, stable cluster identity, mutually reachable advertised endpoints, clock synchronization, and infrastructure which doesn't keep isolated silos serving indefinitely. During a suspected partition, isolate stale members before editing membership records. Applications which require cross-region survival should define ownership and reconciliation outside an assumed single cluster. See [Membership and directory churn](../host/monitoring/troubleshooting-symptom-catalog.md#membership-and-directory-churn).

### Can membership churn create duplicate grain activations?

During failure detection and directory convergence, transient duplicate activations can occur. The runtime resolves directory conflicts, but external side effects and custom directory implementations still need safe semantics. Grain storage concurrency tokens protect against blind conflicting writes, not duplicate non-storage side effects. Design important side effects to be idempotent or deduplicated and investigate repeated duplication as a membership, network, or directory health symptom.

### What's the difference between a grain timer and a reminder?

A timer belongs to an activation and stops when that activation deactivates or fails. A reminder is durable through the configured reminder provider and can reactivate a grain after a silo or activation restart. Neither is a precision real-time scheduler, and application work should tolerate delay and repetition. See [Reminder and timer timing](../host/monitoring/troubleshooting-symptom-catalog.md#reminder-and-timer-timing).

### Why did reminders or timers run late?

Scheduler pressure, CPU or GC pauses, a callback which takes longer than its period, silo restarts, membership changes, and reminder-provider latency can all delay callbacks. Determine which mechanism is in use, compare callback duration with its period, and correlate timing with runtime and provider health. Use durable application state to reconcile time-sensitive business work.

### How do I make rolling upgrades safe?

Keep adjacent versions compatible in both directions for grain interfaces, serializers, stored state, queued/streamed payloads, provider schemas, and configuration. Preserve stable serializer member IDs and aliases, limit rollout concurrency, maintain surge capacity, and define rollback before deployment. Test old-to-new and new-to-old calls plus reads of existing data. See [Graceful shutdown and upgrades](../deployment/upgrades.md).

### Why did serialization start failing after deployment?

Mixed versions might disagree about a type contract, serializer registration, member ID, alias, or package version, or the new version might be unable to read persisted or queued old data. Preserve the failing type and payload source, stop the rollout, and use a compatible reader or rollback before migrating data. See [Serialization failures after a version change](../host/monitoring/troubleshooting-symptom-catalog.md#serialization-failures-after-a-version-change).

### What should I collect before restarting an unhealthy silo?

Capture UTC timestamps, deployment and configuration changes, logs from relevant clients and silos, traces, metrics, membership views, advertised endpoints, provider health, and platform events. For scheduler, CPU, or memory problems, capture a bounded process trace or dump when safe. Redact secrets and grain state. Start with the [Orleans symptom and signal catalog](../host/monitoring/troubleshooting-symptom-catalog.md).
