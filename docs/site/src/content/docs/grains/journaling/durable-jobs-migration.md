---
title: Migrate Durable Jobs storage
description: Move new Durable Jobs to a named journal provider while draining existing storage and verifying retirement.
ms.date: 09/16/2026
ms.topic: how-to
---

# Migrate Durable Jobs storage

Durable Jobs uses journal storage for shard metadata and job records. Select a
write provider for new shards and keep previous providers selected for draining
their existing jobs. Most deployments use a single provider; additional
providers are needed only while work remains in other storage namespaces.

This procedure directs **new work** to B while existing journals drain in place.
Keep one authoritative location for each shard throughout its lifetime.

## Prepare the bindings

1. Register A's existing namespace and B's new namespace using the named
   [Azure Blob/Table](azure-storage.md#named-storage-namespaces),
   [Redis](redis-journal-storage.md#configure-redis-journal-storage), or S3
   journal registration. Use separate accounts, containers, tables, buckets,
   or key prefixes as appropriate. Each selected namespace has one binding.
1. Retain A's identity, namespace mapping, journal format readers, and payload
   codecs. Its credentials must permit reads, listing, conditional metadata
   updates, journal writes, compaction, and deletion throughout the drain.
1. Register <xref:Orleans.Hosting.DurableJobsExtensions.UseJournaledDurableJobs*>
   and configure <xref:Orleans.Hosting.DurableJobsOptions.WriteProviderName>
   and <xref:Orleans.Hosting.DurableJobsOptions.DrainingProviderNames>.
   The default write name is `Default`; the draining list is initially empty.
1. Validate both namespaces and permissions in an isolated environment. Startup
   validates selected storage, catalog, and factory bindings. Physical
   availability and access still need operational checks.

Named journal providers resolve storage, catalog, and state-manager factory
from the same binding. The default provider used by
<xref:Orleans.Journaling.DurableGrain> remains independent of explicitly named
Durable Jobs providers. The in-memory and Azure Durable Jobs convenience
methods compose the same common registration path for the default binding.

For executable configuration and verification, use the
[Durable Jobs migration sample](https://github.com/dotnet/orleans/tree/main/samples/DurableJobsMigration).
It runs separate prepare/drain console processes against disk-backed Azurite,
persists a future job handle, recovers and executes A work after restart, creates
B work, and reports complete inventories locally. Named-provider APIs require
packages built from sources containing this feature; the repository's sample
build supplies those packages.

## Stage the deployment

Provider selection is fixed at process startup. Restart silos when changing
bindings or selection.

| Stage | Write provider | Draining providers | Required condition |
| --- | --- | --- | --- |
| Steady state | A | Empty | All work resides in A |
| Prepare | A | B | Every scheduling silo can discover and locate both A and B before B receives new work |
| Cut over | B | A | Restart all scheduling silos with this selection and finish prior scheduling calls |
| Retire | B | Empty | Verify the complete drain and stop every remaining A writer first |

During an ordinary rolling cutover, older A-configured silos can still create
A shards. Treat deployment completion and the end of admitted scheduling calls
as part of the boundary. If the application requires a strict cutover time,
pause application scheduling, finish in-flight scheduling, update all scheduling
silos, verify their configuration, and then resume.

Retain A's credentials and namespace through its final cleanup. Existing A work
continues mutating A while B receives new jobs.

## Understand routing during the drain

New schedules use newly created shards in B. Discovery sweeps A and B with one
oldest-first ordering and aggregate claim budget, then opens each shard with
the binding of the provider which contained it. Execution, ownership changes,
retries, rescheduling, cancellation, snapshots, and deletion retain that binding.
Draining shards stay closed to new schedules.

Shard IDs retain their timestamp/GUID representation, and journal IDs remain
`jobs/shards/<id>`. Serialized <xref:Orleans.DurableJobs.DurableJob> handles keep
their existing provider-independent shape.
Application code can keep using a previously saved handle after cutover.

For an uncached known shard:

- With one selected provider, the runtime reads that provider's metadata
  directly. This path performs one metadata read regardless of other registered
  providers.
- With multiple selected providers, it reads the exact journal ID in the write
  provider first, then draining providers as needed. This lookup also finds
  future shards outside normal discovery lookahead.
- Once the shard is located, operations use its original binding. A tracked
  shard already has this binding.

Successful absence checks across all selected providers establish absence.
Provider failures that leave a lookup unresolved surface as an
<xref:System.AggregateException>. If another provider supplies the authoritative
shard, that binding can be used while the earlier failure remains observable.
Per-provider discovery failures are reported independently so healthy providers can keep progressing;
a later fresh sweep retries failed providers.

## Verify retirement

Resolve <xref:Orleans.DurableJobs.IDurableJobsStorageInspector> from the host's
services and call its <xref:Orleans.DurableJobs.IDurableJobsStorageInspector.InspectAsync*>
method with A's configured name and a cancellation token.
The returned <xref:Orleans.DurableJobs.DurableJobsStorageStatus> describes a full
`jobs/shards/` catalog snapshot through the caller using read-only catalog and
metadata operations. It includes future shards outside lookahead and poisoned
shards requiring operator recovery.

| Result | Operational meaning |
| --- | --- |
| `ProviderName`, `IsWriteProvider` | The caller's configured binding and write selection |
| `ShardCount` | Existing journals in the shard namespace, including unrecognized entries; each recognized shard can contain many jobs |
| `OwnedShardCount` | Shards with an owner recorded in metadata; evaluate owner liveness separately through cluster membership |
| `PoisonedShardCount` | Shards requiring investigation before normal processing can continue |
| `UnrecognizedShardCount` | Entries with uninterpretable shard metadata; resolve them before retirement |
| `OldestShardStartTime`, `NewestShardStartTime` | Nullable bounds from recognized shard-window metadata; retries and reschedules can move attempts beyond these windows |

Counts are 64-bit values. Owned and poisoned counts can overlap. Inspection
failure or cancellation faults the operation. A successful result covers the
complete enumeration. Display **unknown** and preserve the error on failure.

Require all of the following before removing A:

- Every scheduling silo and other A writer has cut over, and prior scheduling
  requests have finished.
- A successful complete inventory reports a total shard count of zero,
  including unrecognized entries.
- Future-dated, owned, poisoned, and repeatedly rescheduled work has been
  resolved through the application's recovery/cancellation procedures.
- Completion and empty-shard deletion have succeeded; storage authorization and
  cleanup errors have been investigated.
- Repeated inventories, as required by the backend's live-listing semantics,
  agree with the deployment evidence.
- Other consumers of the same binding have finished using it.

Retirement requires both a successful full-zero inventory and completed
cluster-wide writer cutover. An inventory is a live observation, and
`IsWriteProvider` reflects the inspecting process's configuration. Keep
independent deployment evidence for every writer's cutover and the time of
each successful inventory.

Remove A from the draining list and restart with B alone after these conditions
hold. Retire A's storage binding and credentials only when all other consumers
are also finished. The runtime returns to the single-provider direct-lookup
path. Handles for already completed jobs retain the existing cancellation
outcome when their shards are absent.

## Roll back safely

Keep both providers selected and retain provider-aware binaries. Switch writes
back to A while B drains, using the same staged-deployment discipline. Existing
B shards keep executing and mutating B. Keep both namespaces, permissions,
format readers, and payload codecs available through the rollback window.

## Monitor and diagnose

Follow [provider-scoped drain monitoring](operations.md#monitor-durable-jobs-provider-draining).
Record inventory timestamps and failures alongside deployment versions and
configured provider names. Successful inspection emits a structured
Information-level inventory log; provider failures emit structured Error-level
logs with the configured name. Use these logs and inspector snapshots for
provider-scoped progress, alongside existing Durable Jobs and journal metrics.

| Symptom | Check and response |
| --- | --- |
| A's count grows after B is enabled | Find silos or other writers still configured to create A shards; complete cutover before evaluating retirement |
| A remains populated while due-job execution is idle | Inspect the full namespace for future, poisoned, owned, or repeatedly rescheduled work |
| A jobs execute but its count remains above zero | Investigate remaining writable shards, retry/reschedule activity, and failed empty-shard deletion |
| Cancellation after cutover reports a storage error | Check reachability and mutation permissions of every selected provider; preserve the storage error and restore access before retrying the request |
| One provider's inventory is stale | Mark its status unknown; inspect listing, metadata reads, throttling, and authorization |

Inventory can be expensive on large catalogs. Run it at a controlled cadence
with a bounded operator timeout, restrict access to operational results, and
keep credentials out of logs. The sample reports inventory through local
console output. Apply authentication and authorization to any operational
interfaces added by a deployment.
