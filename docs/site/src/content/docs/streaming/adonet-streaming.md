---
title: Operate ADO.NET partitioned streams
description: Configure, retain, upgrade, and diagnose the Orleans ADO.NET partitioned stream provider.
ms.date: 09/07/2026
ms.topic: how-to
---

# Operate ADO.NET partitioned streams

The ADO.NET partitioned stream provider stores immutable ordered records, partition ownership, live checkpoints, and replay leases in SQL Server, PostgreSQL, or MySQL. It is an alpha provider: deploy the matching streaming schema and coordinate schema replacement across every producer and consumer which uses it.

Register the provider with <xref:Orleans.Hosting.SiloBuilderAdoNetStreamExtensions.AddAdoNetStreams*> and configure it through <xref:Orleans.Hosting.SiloAdoNetStreamConfigurator>. Install the matching ADO.NET driver and configure exactly one of <xref:Orleans.Configuration.AdoNetStreamOptions.ConnectionString> or <xref:Orleans.Configuration.AdoNetStreamOptions.DataSource>. The caller owns the lifetime and disposal of a supplied <xref:System.Data.Common.DbDataSource>.

## Configuration reference

The provider's default configuration creates one Orleans queue partition. Calling `ConfigurePartitioning()` without an argument uses that method's public default of eight partitions. More partitions increase independent database readers, checkpoints, cleanup operations, and replay admission pools. Configure partition count consistently on silos and clients.

| Provider option | Default | Operational effect |
|---|---:|---|
| <xref:Orleans.Configuration.AdoNetStreamOptions.StartFromNow> | `false` | A new partition checkpoint begins before its earliest retained record. `true` initializes a previously unseen partition at the current history tail. Existing checkpoints take precedence. |
| <xref:Orleans.Configuration.AdoNetStreamOptions.FaultOnDeliveryFailure> | `false` | When enabled, delivery failure handling can fault the failing subscription. The immutable partition record remains available to other subscriptions. |
| <xref:Orleans.Configuration.AdoNetStreamOptions.MaxMessagesPerRead> | `1000` | Bounds one ordered live storage read. Must be greater than zero. |
| <xref:Orleans.Configuration.AdoNetStreamOptions.CheckpointPersistInterval> | `5 s` | Throttles durable checkpoint writes. Must be greater than zero; a longer interval increases possible redelivery after failure. |
| <xref:Orleans.Configuration.AdoNetStreamOptions.RetentionPeriod> | `1 day` | Minimum age after checkpoint before ordinary cleanup can delete a record. Must fit in one to `2147483647` whole SQL seconds. Fractional seconds round upward. |
| <xref:Orleans.Configuration.AdoNetStreamOptions.MaximumRetentionPeriod> | `null` | Optional hard age ceiling. When set, it must be at least `RetentionPeriod` and can delete unread or replay-protected records. |
| <xref:Orleans.Configuration.AdoNetStreamOptions.CleanupInterval> | `1 min` | Minimum interval between partition cleanup attempts. Fractional seconds round upward. |
| <xref:Orleans.Configuration.AdoNetStreamOptions.CleanupBatchSize> | `1000` | Bounds the contiguous eligible prefix deleted by one cleanup operation. Must be greater than zero. |
| <xref:Orleans.Configuration.AdoNetStreamOptions.ReplayLeaseDuration> | `1 min` | Database TTL protecting an active replay's safe retained watermark. |
| <xref:Orleans.Configuration.AdoNetStreamOptions.ReplayLeaseRenewalInterval> | `20 s` | Must be positive and shorter than `ReplayLeaseDuration`. |
| <xref:Orleans.Configuration.AdoNetStreamOptions.InitializationTimeout> | `30 s` | Bounds query-catalog creation and waiting for the provider's shared initialization lock. |

<xref:Orleans.Hosting.SiloAdoNetStreamConfigurator.ConfigureCache*> controls the live receiver cache and defaults to 4,096 records. <xref:Orleans.Hosting.SiloAdoNetStreamConfigurator.ConfigureReplay*> controls independent historical readers; see [Replay retained persistent-stream history](retained-history-replay.md#configure-replay-capacity) for all defaults and admission behavior.

## Apply streaming schema version 3

Schema version 3 requires coordinated schema replacement. Provider initialization rejects an old or mixed streaming query catalog, and the installation scripts reject existing current or former streaming objects.

1. Stop all ADO.NET stream producers and consumers using the target database. Stop every silo and client which can initialize this named provider.
1. Back up or export any payloads which must survive the alpha-schema replacement. Existing queue rows aren't migrated.
1. Drop `OrleansStreamMessage`, `OrleansStreamReplayLease`, and `OrleansStreamPartition`.
1. Drop former `OrleansStreamDeadLetter`, `OrleansStreamControl`, and stream sequence objects if present.
1. Drop the provider's former streaming routines and remove their streaming `OrleansQuery` rows, including the old schema marker.
1. Apply exactly one current `SQLServer-Streaming.sql`, `PostgreSQL-Streaming.sql`, or `MySQL-Streaming.sql` script.
1. Verify that `StreamSchemaVersionKey` is `3` and that all replay query keys were installed before restarting the cluster.

Deploy matching script and provider versions together. The query catalog and tables form one storage contract, so every process must observe the same schema.

## Retention, cleanup, and replay leases

Ordinary cleanup deletes only a bounded, checkpointed prefix older than `RetentionPeriod`. Replay admission locks the partition, validates the requested lower bound, and creates an epoch-fenced lease in the same database transaction. Cleanup then respects the minimum safe watermark of active, unexpired leases across silos.

Consumer-safe progress advances a lease watermark. Fetched records remain protected while delivery or intentional filtering is pending.

Normal cursor disposal stops the heartbeat and releases the lease. Receiver shutdown stops renewal without releasing it, so the lease remains until `ReplayLeaseDuration` expires. This deliberate TTL handoff lets a replacement owner reconstruct replay without a cleanup interval in which history is unprotected.

`MaximumRetentionPeriod` takes precedence over checkpoints and replay leases. When hard retention crosses either boundary, the provider logs a warning containing the service, provider, queue, deleted range, checkpoint, and replay watermark. Outcomes are explicit:

- A live receiver which loses unread rows enters a retained <xref:Orleans.Streams.DataNotAvailableException> failure and continues reporting that gap on later reads.
- A replay admission below the retained lower bound reports `DataNotAvailableException`.
- An active replay whose lease expires or whose required rows are hard-deleted reports `DataNotAvailableException`.
- A database error during replay admission, reading, or lease renewal reports <xref:Orleans.Streams.TransientStreamReplayException>; the pulling agent retries from its last safe token.
- Loss of the partition ownership epoch reports <xref:System.InvalidOperationException> and prevents the stale receiver or lease from advancing state.

Choose hard retention from a storage-capacity requirement, not as the normal replay window. Alert before oldest required consumer lag approaches that ceiling.

## Recovery and diagnosis

The durable partition checkpoint advances from the earliest safe partition progress across registered subscriptions. A replacement receiver resumes strictly after that checkpoint. Records read after the last persisted checkpoint can be delivered again, and one slow or unregistered subscription can prevent checkpoint advancement.

Monitor database storage growth, oldest row age, cleanup throughput, query latency, connection-pool pressure, delivery failures, and the Orleans queue-cache and oldest-message metrics. Investigate these common symptoms:

| Symptom | Meaning and response |
|---|---|
| `The ADO.NET streaming schema is incompatible` | The query catalog or objects aren't schema version 3. Keep the provider stopped and perform the coordinated replacement. |
| `Timed out waiting to initialize ADO.NET stream provider` | Query-catalog initialization or the local shared initialization lock exceeded `InitializationTimeout`. Check database reachability, credentials, pool exhaustion, locks, and slow DDL/query-catalog access. |
| `Hard stream retention deleted ... crossing checkpoint or replay watermark` | `MaximumRetentionPeriod` overrode delivery or lease protection. Increase the ceiling, reduce lag, or add processing capacity; affected readers can fail with `DataNotAvailableException`. |
| `replay lease ... renewal temporarily failed` | The lease heartbeat encountered a database error. Restore database health before the lease TTL expires; repeated failures can turn into retention loss. |
| `stream partition ownership was lost` | A newer receiver epoch owns the partition. Stop the stale receiver and investigate queue reassignment or overlapping provider instances. |
| Replay admission limit exception | All reusable reader fragments and bounded active/pending capacity are occupied. Reduce concurrent replay or tune `ConfigureReplay` after measuring database load. |

See [Operate and tune Orleans streams](streaming-operations.md) for shared metrics and [Stream delivery, ordering, replay, and recovery](delivery-semantics.md) for application guarantees.
