---
title: Replay retained persistent-stream history
description: Request, size, and operate retained-history replay for Orleans persistent streams.
ms.date: 09/07/2026
ms.topic: concept-article
---

# Replay retained persistent-stream history

Retained-history replay lets one subscription read a provider partition from an older sequence token while the shared live receiver continues independently. Amazon Kinesis and the ADO.NET partitioned stream provider create historical readers when the requested token has left the live Orleans cache. Azure Event Hubs and Redis Streams support subscription rewind within that live cache.

Use retained replay when application recovery requires a provider-native position older than the in-memory cache window. Persist explicit subscription metadata and application checkpoints, and make event handling idempotent so recovery can safely redeliver records.

## Request replay from a token

Pass a non-null <xref:Orleans.Streams.StreamSequenceToken> to a token-taking <xref:Orleans.Streams.AsyncObservableExtensions.SubscribeAsync*> overload when creating an explicit subscription. Pass a token to <xref:Orleans.Streams.StreamSubscriptionHandleExtensions.ResumeAsync*> to replace an existing explicit handle's position.

A caller-supplied token is an **inclusive start position**: Orleans offers the matching record when it is still available. In contrast, Orleans records a delivery acknowledgment only after the consumer call completes successfully. Internal recovery from that acknowledged delivery token is **strictly after** the matching record. Therefore:

- Persist an application checkpoint only after its effects are durable.
- Expect the checkpoint boundary to be delivered again when the application explicitly resumes from the persisted token.
- Resume a current explicit handle without a replacement token to preserve its existing handshake progress.

Tokens are provider and partition scoped. Kinesis tokens include shard identity and Kinesis sequence position. Native ADO.NET tokens include the Orleans service, provider, queue partition, message position, and event index. ADO.NET can normalize legacy position-only tokens to the current partition for compatibility, but applications should persist the native token received by the observer so identity remains explicit. A native token from another provider or partition is rejected instead of being interpreted as a local position.

For a cache-relative start, use [`EarliestAvailable`](subscription-start-positions.md). That position searches the pulling agent's live cache, including for Kinesis and ADO.NET.

## Provider capabilities

| Provider | Position after the live cache evicts the token |
|---|---|
| Amazon Kinesis | Opens an independent shard iterator while Kinesis still retains the record. |
| ADO.NET partitioned streams | Acquires an epoch-fenced database replay lease and reads retained partition rows. |
| Azure Event Hubs | Subscription rewind uses the live Orleans cache. A replacement receiver uses Event Hubs retention and its durable partition checkpoint for transport recovery. |
| Redis Streams | Subscription rewind uses the live Orleans cache. A replacement receiver resumes the retained Redis transport from its durable checkpoint. |

See the [provider matrix](stream-providers.md#provider-matrix) and [stream delivery semantics](delivery-semantics.md) for the capabilities of other providers.

## Configure replay capacity

Kinesis and ADO.NET register <xref:Orleans.Configuration.RecoverableStreamReplayOptions> for each named provider. Configure the silo-side provider with <xref:Orleans.Hosting.SiloKinesisStreamConfigurator.ConfigureReplay*> or <xref:Orleans.Hosting.SiloAdoNetStreamConfigurator.ConfigureReplay*>. Each provider activates replay with the following defaults.

| Option | Default | Constraint and runtime effect |
|---|---:|---|
| <xref:Orleans.Configuration.RecoverableStreamReplayOptions.MaxConcurrentReaders> | `4` | Must be greater than zero. Bounds independent historical readers per queue partition. Compatible cursors can share one reader fragment. |
| <xref:Orleans.Configuration.RecoverableStreamReplayOptions.MaxPendingReaders> | `32` | Must be zero or greater. Sets the normal bound for cursors waiting for reader admission per queue. When the queue is full, acquisition fails with <xref:System.InvalidOperationException>. During asynchronous reader disposal, the manager can temporarily admit one replacement waiter per disposing reader so capacity turnover doesn't deadlock. |
| <xref:Orleans.Configuration.RecoverableStreamReplayOptions.CacheSize> | `4096` records | Must be greater than zero. Bounds raw partition records held by each replay fragment. Records for other logical streams count because replay scans the physical partition in order. Memory use depends on encoded record sizes and pooled-buffer overhead. |
| <xref:Orleans.Configuration.RecoverableStreamReplayOptions.ReadBatchSize> | `256` records | Must be greater than zero. Bounds one historical provider read and is further limited by available fragment-cache capacity. |
| <xref:Orleans.Configuration.RecoverableStreamReplayOptions.TemporaryTailRetryDelay> | `200 ms` | Must be zero or greater. Delays another read when a historical source hasn't reached its final tail or the fragment cache is temporarily full. |

Size `MaxConcurrentReaders` against provider read limits and database or broker cost. Size `MaxPendingReaders` against the acceptable number of waiting subscription operations. Each active fragment owns its cache capacity, so its configured upper item-count envelope per queue is `MaxConcurrentReaders × CacheSize`, plus the separately configured live cache.

A slow cursor can hold the safe boundary of a shared fragment. When the fragment reaches `CacheSize`, historical reads pause until delivery or filtering advances safe progress. This is replay backpressure: increasing capacity can reduce pauses but also raises memory use and the amount of history pinned by ADO.NET leases.

## Understand the replay-to-live handoff

The receiver-owned replay manager starts a historical cursor at the requested provider token and records an immutable live-cache boundary. Replay scans the partition in order, including unrelated stream records, while the boundary remains pinned against live-cache purge. The manager then creates the live cursor before releasing historical state. This ordering preserves a contiguous replay-to-live transition.

Delivery remains at least once through handoff. A crash, lost acknowledgment, checkpoint interval, or ownership change can redeliver records around the recovery boundary. Provider retention can also expire before replay reaches the pinned live boundary, which ends the replay with <xref:Orleans.Streams.DataNotAvailableException>.

## Failure and lifecycle behavior

| Condition | Runtime outcome | Operator response |
|---|---|---|
| Token identity is invalid, or retained history no longer contains the position | <xref:Orleans.Streams.DataNotAvailableException> is reported to the subscription and the requested replay remains unavailable. | Choose a valid checkpoint still covered by retention, or explicitly start at a newer position. |
| Historical provider read, iterator creation, or ADO.NET lease renewal fails transiently | <xref:Orleans.Streams.TransientStreamReplayException> is reported through stream failure handling. The pulling agent recreates the replay cursor from its last safe partition token and applies its delivery retry backoff. | Alert on recurrence and inspect provider throttling, connectivity, credentials, and database health. |
| Reader admission is full | Cursor acquisition fails with <xref:System.InvalidOperationException> containing the configured pending-reader limit. | Reduce simultaneous replay demand or increase bounded reader and pending capacity after checking provider limits. |
| The source reaches a temporary tail or replay cache is full | The cursor remains active and retries after `TemporaryTailRetryDelay`. | Investigate sustained cache pressure or a provider reader which never reaches its tail. |
| The subscription or its wait is canceled | The current wait is canceled. Disposing the cursor releases its admission, fragment reference, and provider reader when no cursor still uses it. | Treat cancellation separately from retention loss and retry only while the subscription remains desired. |
| The receiver shuts down | Pending admissions and reads are canceled, replay readers are drained, the live checkpoint is flushed, and cleanup failures remain visible from shutdown. | Preserve shutdown errors and allow enough queue shutdown time for provider cleanup. |

Normal ADO.NET cursor disposal releases its replay lease. Receiver shutdown deliberately leaves the database lease until its TTL expires so a replacement queue owner can reconstruct replay protection without opening a cleanup gap. See [Operate ADO.NET partitioned streams](adonet-streaming.md).

## Separate subscription position from queue recovery

Retained replay maintains three related but distinct positions:

- The **subscription cursor** selects what one observer receives.
- The **safe partition watermark** is the earliest contiguous scan-and-delivery progress across active subscriptions. It can advance across records belonging to other streams after they are safely scanned.
- The **queue checkpoint** is persisted from that safe watermark and tells a replacement live receiver where to resume the physical partition.

The Kinesis or ADO.NET queue checkpoint persists shared partition recovery progress. `PubSubStore` persists explicit subscription identity; application state stores persist consumer effects and application checkpoints. After failure, the receiver resumes strictly after its durable queue checkpoint, while uncheckpointed records and an explicitly supplied application checkpoint can be delivered again.

For operating symptoms and signals, see [Diagnose retained-history replay](streaming-operations.md#diagnose-retained-history-replay). For implementation details, see [Persistent stream pulling architecture](../implementation/streams-implementation/index.md#retained-history-replay-pipeline).
