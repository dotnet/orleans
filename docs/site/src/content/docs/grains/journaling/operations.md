---
title: Operate and troubleshoot Orleans Journaling
description: Monitor capacity, compaction, recovery, upgrades, and failures for experimental Orleans Journaling.
ms.date: 08/21/2026
ms.topic: how-to
---

# Operate and troubleshoot Orleans Journaling

Orleans Journaling is an experimental alpha feature. A production evaluation should define owners and tested procedures for package upgrades, storage-format migration, rollback, backup, restore, compaction, and provider outages.

## Monitor writes and recovery

Journaling emits metrics through the `Microsoft.Orleans` meter. Use the [metrics catalog](../../host/monitoring/metrics-catalog.md#journaling) for the full instrument list.

At minimum, dashboard and alert on:

- Storage operation errors and latency by `append`, `snapshot`, `replace`, `read`, and `delete`.
- Recovery failures and recovery duration.
- Storage-operation queue duration, which shows time waiting behind earlier work for the same journal.
- Compaction triggers by `storage_requested`, `migration`, and `user_snapshot`.
- Write coalescing, gathered state count, and operation byte distributions.
- Catalog page and candidate counts, grouped by `provider`.
- Native listing item counts compared with delivered catalog entries, and explicit provider retry rates.

Correlate these signals with grain identity, provider dependency health, deployment version, and storage throttling.

### Monitor Durable Jobs provider draining

Track the configured provider name separately from the backend category in
journaling catalog metrics. Two named Blob providers use the same backend but
can have different availability and remaining work.

Use <xref:Orleans.DurableJobs.IDurableJobsStorageInspector.InspectAsync*> to
collect a complete shard-namespace snapshot per selected provider. Record the
inventory's start/completion timestamps, caller, configured write provider,
success/failure, counts, and oldest/newest shard start time. Keep the last
successful result with its timestamp for history; mark current status unknown
on failure.

Successful inspection also emits a structured Information-level inventory log.
Provider errors emit structured Error-level logs with the configured provider
name. Use inspector snapshots and these logs for provider-scoped drain progress;
existing execution and catalog metrics provide complementary workload signals.

During cutover, alert on old-provider write-permission failures, failed
discovery or known-ID lookups, inventory failures, poisoned or unrecognized
entries, and an unexpectedly flat drain curve. Each shard may contain many jobs,
and retries or rescheduling can extend its
lifetime beyond its original time window. Oldest/newest shard start times
describe recognized shard windows; retries and reschedules can move attempts
beyond those windows.

Normal discovery uses lookahead bounds and can skip future or poisoned work.
Use the complete inspector results and deployment evidence in the
[retirement checklist](durable-jobs-migration.md#verify-retirement) to decide
whether old storage can be removed. Execution throughput and eligible-work
sweeps provide complementary workload signals. Run inspection as a protected
operator command or background diagnostic task, and apply authentication and
authorization to operational interfaces.

### Monitor catalog traversal and retries

Use `orleans-journaling-provider-catalog-pages` to track pages received during S3 and Azure catalog
traversal, including empty pages. Compare `orleans-journaling-provider-catalog-items` with
`orleans-journaling-provider-catalog-entries` to see how many returned candidates are filtered before
reaching the consumer. Redis candidate counts include consumed scan keys before filtering and
duplicate suppression. Volatile storage records delivered entries.

`orleans-journaling-provider-retries` records explicit retries in provider storage recovery loops.
Correlate retry rates with the existing journaling and provider-specific storage latency and outcome
metrics. Catalog pages, candidates, and entries describe traversal work; use service-side transaction
metrics when reconciling costs.

### Use host-provided dependency telemetry

The application supplies client SDK telemetry through its hosting integrations, instrumentation
libraries, and diagnostic configuration. For example, Aspire integrations can enable dependency
telemetry for their registered clients. Reuse that existing instrumentation when investigating
SDK request latency, failures, and transport retries. Additional diagnostic collection belongs
in the application's client configuration:

| Provider | Opt-in diagnostics |
| --- | --- |
| Azure Blob and Table | Follow [Azure SDK logging](https://learn.microsoft.com/dotnet/azure/sdk/logging) to attach an event listener or forward SDK events to application logging. Request/response events include HTTP status and I/O duration. |
| S3 | Configure [AWS SDK logging](https://docs.aws.amazon.com/sdkfornet/v4/apidocs/items/Util/TLoggingConfig.html) at application startup, before constructing the S3 client. Select a logging destination and enable SDK metric logging for request diagnostics. |
| Redis | Register a profiler on the provider's connection multiplexer using [StackExchange.Redis profiling](https://seredis.dev/Profiling_v2). Scope sessions to the investigated work and finish each session to collect command queue, send, and response timings. |

The application controls diagnostic collection, exporters, and retention. Limit detailed collection
to the required interval and protect resource identities and response contents using the SDK's
logging controls and the application's data-handling policy. Shared clients retain their existing
diagnostic configuration and caller-owned lifetime.

## Plan capacity

Journal replay cost grows with bytes and operations since the latest snapshot. Snapshot cost grows with the complete durable state of the grain.

Capacity tests should include:

- The largest expected durable collection and persistent-state payload.
- Hot grain identities with frequent writes.
- Activation after a long append sequence.
- Provider-requested compaction during peak traffic.
- Storage throttling, transient failures, and optimistic-concurrency recovery.
- Backup and restore followed by activation and another write.

Tune provider thresholds before replay time, storage growth, or backend transaction limits become operational risks. The [Azure Table provider](azure-storage.md#azure-table-storage) limits one append batch to 2 MiB. Redis requests compaction at 128 MiB by default. Azure Blob compacts before exhausting its append-block budget.

## Upgrade and rollback

Treat the package version, public API, journal format, application payload schema, and durable-state names as one compatibility contract.

1. Back up journal bytes and metadata.
1. Validate old data against the candidate binaries in an isolated environment.
1. Keep all silos which can activate a journaling grain on a tested compatible version set.
1. Deploy readers for old and new payload schemas before writers emit only the new schema.
1. Observe recovery and migration-compaction metrics.
1. Retain old readers, codecs, and state definitions through the rollback window.

A provider key prefix, blob-name mapping, table partition mapping, journal format key, or durable-state name defines how the runtime finds and interprets existing data. Perform and verify the corresponding data migration before a new mapping serves traffic.

## Backup and disaster recovery

Provider metadata selects the published generation and journal format. Back up metadata and journal data consistently. Restore them as one unit and keep the same application `ServiceId`, grain identities, state names, format readers, and payload codecs.

Exercise restore procedures regularly:

1. Restore to an isolated provider namespace.
1. Start a compatible silo.
1. Activate representative grains and verify recovered state.
1. Execute and acknowledge a new write.
1. Trigger or await compaction and verify a second activation.

## Diagnose common failures

| Symptom | Runtime outcome | Operator response |
| --- | --- | --- |
| Activation fails with a journal format key | Recovery leaves storage unchanged | Deploy the required format and command codecs, or restore metadata which names the actual stored format |
| Activation reports malformed or truncated journal data | Recovery stops before activation completes | Quarantine writes, restore a consistent backup, and preserve the failed data for diagnosis |
| <xref:Orleans.Storage.InconsistentStateException> during a write | The manager fences operations and requests deactivation; a fresh activation recovers durable state | Find stale or competing writers, confirm grain identity and cluster configuration, then retry the application command according to its idempotency contract |
| Storage commits a write but its acknowledgement is lost | The manager fences operations and requests deactivation; the new activation observes the committed mutation | Reconcile using the operation identifier and retry the command only through its idempotency protocol |
| Write latency rises with storage queue duration | Writes for the journal are waiting behind earlier operations | Inspect provider latency and throttling, hot-grain traffic, write frequency, and snapshot size |
| Recovery time and journal bytes grow | The current append history is large | Confirm compaction thresholds are enabled and that snapshot replacements succeed |
| Format migration reports an unregistered state | A retired stream still requires its previous codec | Re-register the state for migration or retain the previous write format until retirement and compaction remove it |
| Redis state is lost after a server failure | Recovery reflects the Redis durability configuration | Configure and test Redis persistence and replication for the required recovery point |

Storage and codec exceptions are surfaced to the grain call. Preserve them in logs and traces rather than converting them into successful application responses.
