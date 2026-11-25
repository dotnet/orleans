---
title: Grain directory migration
description: Upgrade Orleans grain directory configurations while preserving activation and partition identities.
ms.date: 10/21/2026
ms.topic: how-to
---

# Grain directory migration

<a id="local-to-distributed-directory-upgrade"></a>
<a id="legacy-to-distributed-directory-upgrade"></a>

Orleans 11.0.0-preview.1 uses the distributed grain directory by default. For an Orleans 10 cluster using `LegacyGrainDirectory`, separate the runtime upgrade from the directory migration: first upgrade every runtime while retaining the legacy directory, then migrate the directory in a second rollout.

For clusters already using the distributed directory, follow [Upgrade existing distributed-directory clusters](#upgrade-existing-distributed-directory-clusters) to retain their partition configuration.

Qualify the exact runtime and application versions using the [deployment compatibility guidance](deployment-and-rollback.md#compatibility-boundary) before choosing a rolling runtime upgrade.

## Prepare and defer migration

1. Inventory the default and named directories selected by every grain type. Keep named-provider selections and cluster identity unchanged during this procedure.
1. Qualify serialized contracts, persistence providers, and deployment configuration under representative traffic. Verify successful calls during rollout, graceful silo departure, and duplicate resolution and directory integrity after completion.
1. Persist state needed after reactivation and budget capacity for activation recovery. During the mixed-directory phase, temporary duplicate activations can run concurrently. Use persistence concurrency controls and idempotent external operations to tolerate concurrent activations and retries.
1. Roll out the new runtime with <xref:Orleans.Hosting.CoreHostingExtensions.UseLegacyGrainDirectory*> configured on **every replacement silo**. This retains the legacy directory while the runtime versions coexist. Keep that configuration on scale-out instances too.
1. Wait until every silo runs the new runtime, membership is stable, and grain-call and activation behavior match the baseline. Applications can remain in this all-legacy configuration until ready for the directory migration.

`UseLegacyGrainDirectory` defers migration. Choose it before the directory migration starts. Use its API reference for the configuration surface; the released package used by the hosting snippets predates this API.

## Migrate the directory

1. For this legacy-to-distributed rollout, keep <xref:Orleans.Configuration.GrainDirectoryOptions.PartitionsPerSilo> at `1` and retain the default <xref:Orleans.Configuration.GrainDirectoryOptions.GetPartitionBoundaries> function.
1. Remove `UseLegacyGrainDirectory` from the deployment configuration. The runtime default selects the distributed directory; an explicit call to <xref:Orleans.Hosting.CoreHostingExtensions.AddDistributedGrainDirectory*> also selects it.
1. Replace a bounded number of silos at a time, preserving the tested ready-silo and surge-capacity floor. Gracefully stop each departing silo, and allow membership and directory ownership transfers to complete before the next replacement.
1. During each mixed-directory stage, exercise existing grains and newly activated grains and monitor call failures and activation churn. Temporary duplicate activations are accepted during this phase.
1. Continue until every silo uses the distributed directory, then allow membership, range recovery, and duplicate resolution to converge. Verify one registered activation per single-activation grain and matching registrations for active grains. Keep the distributed selection and partition configuration on later deployments and scale-out instances.

Compatibility system targets bridge the two directory protocols during this transition. A distributed-default silo serves remote requests from legacy-directory silos through its distributed directory. Legacy-directory silos expose endpoints for distributed partition requests and activation recovery. Keep named-provider selections consistent throughout both rollouts.

For workloads which require exclusive activation ownership throughout the cutover, use a coordinated full-cluster restart instead of a rolling directory migration: gracefully stop every silo, then start the distributed-default deployment.

## Watch the migration

Compare grain-call error rate and latency, activation churn and failures, duplicate-activation reports, membership churn, and directory registration errors with the pre-upgrade baseline. Observe directory snapshot transfers, range recovery, and range-lock duration alongside silo departures.

Temporary mixed-phase duplicates should resolve once the upgrade completes. The final healthy checkpoint has an all-distributed cluster, a stable membership view, completed recovery, successful calls to existing and new grains, and one registered activation per single-activation grain.

Pause replacements when an application safety or availability threshold is exceeded. Preserve directory and membership logs and retain sufficient distributed-directory capacity. Investigate duplicate activations or registration mismatches which persist after the all-distributed cluster has converged.

## Recovery and one-way migration

Once the cluster begins using the distributed default, continue toward an all-distributed cluster. Returning that cluster to the legacy directory is unsupported. Decide the recovery procedure and qualify the deployment before introducing the first distributed-default silo.

Recover application state using the qualified recovery procedure while retaining the distributed directory and its partition configuration. Include compatible application images, storage changes, and recovery points in that plan.

## Upgrade existing distributed-directory clusters

Rolling runtime upgrades of an existing distributed-directory cluster preserve both the partition count and the mapping from hash boundaries to partition numbers. Directory requests and ownership-transfer snapshots address those numbered partitions, so all silos must agree on their ranges.

Orleans 10.1 used 30 partitions per silo and assigned partition numbers after sorting each silo's uniform hashes by unsigned value. Orleans 10.2 changed the default count to one and assigned multiple partitions in hash-generation order.

When upgrading a distributed-directory cluster running Orleans 10.1, configure newer silos with <xref:Orleans.Configuration.GrainDirectoryOptions.PartitionsPerSilo> set to `30` and an application-defined <xref:Orleans.Configuration.GrainDirectoryOptions.GetPartitionBoundaries> delegate which returns each silo's uniform hashes in sorted order:

:::code language="csharp" source="../snippets/compiled/Deployment/DirectoryPartitioningSnippet.cs" id="legacy_directory_partitions":::

Sorting the immutable hash array preserves the silo's cached hash order and produces the existing partition identities. Retain this configuration after the upgrade and in every compatible distributed-directory recovery deployment.

Clusters already using the Orleans 10.2-and-later generated-order mapping retain the default boundary function and their existing partition count. Qualify runtime upgrades and compatible application recovery under sustained traffic, checking directory registration preservation and activation uniqueness. Changing an established cluster's partition count or boundary mapping requires a coordinated full-cluster restart or a blue-green cutover with controlled grain-state ownership.

## Rebuild uniform-hash callers

<a id="uniform-silo-hash-api-compatibility"></a>

<xref:Orleans.Runtime.SiloAddress.GetUniformHashCodes*> returns an <xref:System.Collections.Immutable.ImmutableArray`1> which shares the silo's cached hash storage. Rebuild callers compiled against the `uint[]` return type. Use `ToArray()` when a mutable buffer is needed, or `Sort()` to obtain a sorted immutable result while preserving the cached values.

For the directory's current configuration and architecture, see [Grain directories](../host/grain-directory.md) and [Grain directory architecture](../implementation/grain-directory.md).
