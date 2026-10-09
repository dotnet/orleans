---
title: Graceful shutdown and upgrades
description: Scale in and deploy Orleans applications using graceful, rolling, or blue-green strategies.
ms.date: 08/02/2026
ms.topic: concept-article
---

# Graceful shutdown and upgrades

Orleans tolerates abrupt process loss, but a graceful shutdown reduces failed calls and avoids unnecessary recovery work. Correctness must never depend on graceful shutdown because a process, host, or network can fail without warning.

## Graceful shutdown and scale-in

For each instance being removed:

1. Stop admitting new application traffic and report not ready.
1. Stop application-specific background work from accepting new items.
1. Ask the .NET host to stop and await <xref:Microsoft.Extensions.Hosting.IHost.StopAsync*?displayProperty=nameWithType> or normal host termination.
1. Allow Orleans to leave cluster membership and deactivate or transfer runtime responsibilities.
1. Terminate the process only after the shutdown deadline expires.

The orchestrator termination grace period must exceed the .NET host shutdown timeout. Measure the actual duration under load and leave margin for provider latency.

Scale in gradually. Remove one failure domain at a time and wait until membership stabilizes, remaining silos absorb activations, and latency returns to normal. Don't reduce the cluster below its tested redundancy or capacity floor.

See [Shut down Orleans](../host/configuration-guide/shutting-down-orleans.md) for host APIs.

## Rolling upgrades

Use a rolling upgrade when old and new versions can safely share:

- Grain interfaces and serialized payloads.
- Persisted grain state.
- Reminder, stream, and provider schemas.
- External side effects and deduplication records.
- Cluster configuration and transport settings.

Before rollout:

1. Upgrade clients and silos in an order compatible with both versions.
1. Use additive contract changes first; don't reuse or renumber serializer field IDs.
1. Make storage migrations backward-compatible and independently reversible.
1. Verify the minimum ready-silo count and surge capacity.
1. Test rollback while both versions and both state formats exist.

Replace a bounded number of silos at a time. Pause when error rate, latency, membership churn, dependency load, or activation failures exceed the rollout threshold.

Orleans grain versioning can route calls among compatible grain implementations, but it doesn't make arbitrary application or storage changes compatible. See [Deploy new versions of grains](../grains/grain-versioning/deploying-new-versions-of-grains.md) and [Backward compatibility guidelines](../grains/grain-versioning/backward-compatibility-guidelines.md).

### Invocation retirement compatibility

Retiring receivers forward waiting requests using the established message protocol. Upgraded callers preserve the original callback across physical-target death, so a forwarded invocation can complete from its replacement activation within the original response deadline.

Forwarding status extends the existing diagnostic-status body with an optional destination. Its existing message header carries the forwarding count used to order route updates. Upgraded callers use the destination for target tracking and outside-client location hints. Released peers read the existing diagnostic fields, and legacy relays can omit the destination when they deserialize and reserialize a status. Callback preservation and receiver-owned forwarding provide invocation continuity through those relays. One-way requests, silo-bound system targets, migrations, and duplicate-activation recovery retain their established routing paths.

Upgrade hosted and grain callers before retiring their targets when invocation continuity is required. Hosted callers running Orleans 10.3.1 or 10.4.0 retain their released behavior: their physical-target death sweep can complete a forwarded call with a silo-unavailable error. Upgraded callers preserve that callback when a released receiver forwards the request. Released outside clients can await the legacy forwarding outcome through a stable gateway.

Qualify rolling upgrades with queued calls, cancellation, held replacement execution, membership views which jump directly to dead, and shutdown-budget expiration. Measure both successful handoffs and the longer failure latency of genuinely lost relocatable calls, which now remain bounded by their original response timeout.

### Uniform silo hash API compatibility

<xref:Orleans.Runtime.SiloAddress.GetUniformHashCodes*> returns an <xref:System.Collections.Immutable.ImmutableArray`1> which shares the silo's cached hash storage. Rebuild callers compiled against the earlier `uint[]` return type. Use `ToArray()` when a mutable buffer is needed, or `Sort()` to obtain a sorted immutable result while preserving the cached values.

### Distributed-directory upgrade compatibility

Rolling upgrades using the experimental [distributed grain directory](../host/grain-directory.md#strongly-consistent-in-cluster-directory) preserve both the partition count and the mapping from hash boundaries to partition numbers. Directory requests and ownership-transfer snapshots address those numbered partitions, so all silos must agree on their ranges.

Orleans 10.1 used 30 partitions per silo and assigned partition numbers after sorting each silo's uniform hashes by unsigned value. Orleans 10.2 changed the default count to one and assigned multiple partitions in hash-generation order.

When upgrading a distributed-directory cluster running Orleans 10.1, configure newer silos with <xref:Orleans.Configuration.GrainDirectoryOptions.PartitionsPerSilo> set to `30` and provide an application-defined <xref:Orleans.Configuration.GrainDirectoryOptions.GetPartitionBoundaries> delegate which returns each silo's uniform hashes in sorted order:

:::code language="csharp" source="../snippets/compiled/Deployment/DirectoryPartitioningSnippet.cs" id="legacy_directory_partitions":::

Sorting the immutable hash array preserves the silo's cached hash order and produces the existing partition identities. Retain this configuration after the upgrade and throughout rollback.

Clusters already using the Orleans 10.2-and-later generated-order mapping retain the default boundary function and their existing partition count. Qualify upgrades and rollback under sustained traffic, checking directory registration preservation and activation uniqueness. Changing an established cluster's partition count or boundary mapping requires a coordinated full-cluster restart or a blue-green cutover with controlled grain-state ownership.

## Blue-green upgrades

Use blue-green deployment when versions can't safely coexist in one Orleans cluster.

- Give blue and green distinct `ClusterId` values.
- Decide whether they can share grain storage. Sharing is safe only when both versions can read and write the same records without concurrent ownership or incompatible side effects.
- Route external traffic at the application ingress, not by placing a load balancer in front of silo endpoints.
- Keep the previous environment available until state compatibility and rollback constraints permit removal.

For stateful cutovers, use one of these explicit strategies:

- Quiesce writes, migrate state, validate, then switch traffic.
- Dual-write through an application-owned migration protocol with reconciliation.
- Use separate state stores and perform an offline transfer.

Never point two incompatible active clusters at the same mutable grain state and assume Orleans membership will coordinate them. Membership is scoped by cluster ID; it doesn't provide cross-cluster ownership.

## Rollback

A rollback plan must cover code, configuration, schema, credentials, and persisted state. If the new version has written data the old version can't read, reverting only the image isn't a rollback.

Stop a rollout when safety thresholds are exceeded. Preserve logs and traces from failed instances, restore capacity with a known-compatible version, and avoid repeated automated retries that churn membership.
