---
title: Journaling runtime behavior and consistency
description: Understand Orleans Journaling activation, write, recovery, compaction, concurrency, and failure semantics.
ms.date: 08/23/2026
ms.topic: conceptual
---

# Journaling runtime behavior and consistency

Orleans Journaling assigns one <xref:Orleans.Journaling.JournalId> to each grain identity and one journal stream to each named durable state. The state manager serializes recovery and storage work for that journal.

## Activation and recovery

The <xref:Orleans.Journaling.DurableGrain> constructor attaches its state manager to the grain lifecycle. During the setup-state stage, the manager:

1. Reads the journal as an ordered byte stream.
1. Selects the reader named by the stored format metadata.
1. Rebuilds the state-name directory.
1. Resets and replays each registered durable state.
1. Completes activation setup after replay finishes.

Requests reach the grain after setup succeeds. A storage read, format, codec, or malformed-data failure fails activation and preserves the stored journal for diagnosis and recovery.

Storage providers can split reads at arbitrary byte boundaries. The journal format buffers incomplete entries and only applies complete ordered records.

## Mutation and write acknowledgement

Durable collections encode their operation before applying it to the in-memory collection. A codec failure therefore leaves both the journal buffer and collection unchanged.

<xref:Orleans.Journaling.DurableGrain.WriteStateAsync*> gathers pending entries from all named states and queues one storage operation:

- **Append** adds the encoded operation batch atomically.
- **Snapshot replacement** writes the current state of every registered stream and atomically publishes it as the new journal generation.

Concurrent calls made while the same kind of write is queued can share that queued operation. Each caller observes its completion or failure. Calls made after a storage operation starts are processed by a later operation.

> [!IMPORTANT]
> In-memory mutation is visible before storage acknowledgement. Return success to a caller only after the required <xref:Orleans.Journaling.DurableGrain.WriteStateAsync*> completes. Recovery can rewind changes that were never acknowledged by storage.

## Consistency and competing writers

Orleans grain placement normally supplies a single active writer for a grain identity. Journal storage providers also use optimistic concurrency to protect the journal when a stale or competing writer reaches storage.

An append, replacement, or delete with stale storage metadata throws <xref:Orleans.Storage.InconsistentStateException>. The state manager marks the journal for recovery, resets its in-memory durable states, and replays the current stored journal before processing later work. The failed write task remains faulted so the grain call reports an uncertain outcome.

Design commands to tolerate retries at the application boundary. Use operation identifiers when a caller can repeat a command after an uncertain network outcome.

## Storage failures

A normal append failure leaves encoded pending entries available for a later write attempt.

A snapshot failure leaves the previously published journal unchanged and keeps the captured in-memory changes available for a later write attempt. Commands added while the replacement is awaiting storage remain outside the captured snapshot and are persisted by a later operation.

An optimistic-concurrency conflict identifies a competing journal generation. The manager recovers that winning generation and discards the losing activation's uncommitted in-memory changes before reporting the conflict.

Storage acknowledgement can still have an uncertain network outcome. Treat a failed write as an uncertain application outcome and retry commands using an operation identifier or another idempotency mechanism.

Recovery exceptions fault activation or queued work rather than replacing or truncating stored data. Restore the required format/codec registration or repair the backing data before retrying activation.

## Compaction

Each provider reports when its journal crosses a configured storage threshold. The next <xref:Orleans.Journaling.DurableGrain.WriteStateAsync*>:

1. Captures the pending journal prefix and builds a snapshot containing the state directory and every active durable state.
1. Atomically replaces the published journal with the complete snapshot.
1. Consumes the captured pending prefix after storage acknowledges the replacement.
1. Leaves commands added during the replacement pending for the next write.
1. Clears the compaction request after storage acknowledges the replacement.

Compaction bounds replay work and storage growth according to provider thresholds. Snapshot size still scales with the complete durable state owned by the grain, so capacity tests must include hot and large grain identities.

## Retire a named state

Recovery preserves streams whose names are no longer registered by the current grain type. The manager starts a retirement grace period for each such state. The default minimum is seven days, configured with <xref:Orleans.Journaling.JournaledStateManagerOptions.RetirementGracePeriod>.

Reintroducing the same state name during the grace period replays its preserved entries into the new state instance. After the grace period has elapsed, a later compaction removes the retired stream. Permanent removal can therefore occur later than the configured period.

This behavior supports staged deployments and rollback. Keep the previous format codecs available while retired streams remain. A format migration pauses when an unregistered stream can't be decoded into a snapshot.

## Deactivation and shutdown

Grain deactivation stops the state manager's work loop. Completed writes are the durability barrier. Await every required write during the grain call which made the mutation, and size host shutdown grace periods for writes already in progress.
