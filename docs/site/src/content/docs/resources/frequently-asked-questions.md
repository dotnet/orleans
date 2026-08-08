---
title: Frequently asked questions
description: Answers to common questions about Orleans.
ms.date: 08/08/2026
ms.topic: faq
---

# Frequently asked questions

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
