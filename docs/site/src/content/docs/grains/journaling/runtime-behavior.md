---
title: Journaling runtime behavior and consistency
description: Understand Orleans Journaling activation, write, recovery, compaction, concurrency, and failure semantics.
ms.date: 08/21/2026
ms.topic: conceptual
---

# Journaling runtime behavior and consistency

Orleans Journaling assigns one <xref:Orleans.Journaling.JournalId> to each grain identity and one journal stream to each named durable state. The state manager serializes recovery and storage work for that journal.

## Activation and recovery

The standard grain-scoped <xref:Orleans.Journaling.IJournaledStateManager> factory registered by <xref:Orleans.Journaling.HostingExtensions.AddJournalStorage*> creates the concrete manager and enrolls it in the grain lifecycle before returning it. Resolving a keyed durable state also resolves this manager. Each activation shares one manager across its injected states and features.

Constructor injection registers durable states before lifecycle startup. Orleans completes the grain constructor and assigns <xref:Orleans.Runtime.IGrainContext.GrainInstance>, then runs any shared activation setup actions before calling the grain object's `Participate` method and starting the lifecycle. A feature can resolve additional activation-scoped states and enroll its own participant in those actions. All subscriptions are established before lifecycle startup.

During <xref:Orleans.Runtime.GrainLifecycleStage.SetupState>, the manager:

1. Reads the journal as an ordered byte stream.
1. Selects the reader named by the stored format metadata.
1. Rebuilds the state-name directory.
1. Resets and replays each registered durable state.
1. Completes activation setup after replay finishes.

<xref:Orleans.Grain.OnActivateAsync*> and requests observe recovered durable state after setup succeeds, whether the grain derives directly from <xref:Orleans.Grain>, from an application-owned base, or from <xref:Orleans.Journaling.DurableGrain>. A storage read, format, codec, or malformed-data failure fails activation and preserves the stored journal for diagnosis and recovery.

Provider registration makes Journaling services available. Per-grain journal I/O begins only for activations which resolve the manager, directly or through durable-state dependencies. Grains which use other persistence models keep their existing activation behavior.

Lifecycle stages execute in order, with callbacks at the same stage eligible to run concurrently. A feature which reads recovered state subscribes after `SetupState`; a feature which must finish before `OnActivateAsync` subscribes before <xref:Orleans.Runtime.GrainLifecycleStage.Activate>. Shared setup actions execute after construction and are reused concurrently across activations, so they resolve scoped state through the supplied context. See [Shared activation setup](../grain-lifecycle.md#shared-activation-setup).

Storage providers can split reads at arbitrary byte boundaries. The journal format buffers incomplete entries and only applies complete ordered records.

### Custom and caller-owned managers

Assign lifecycle enrollment to the owner which creates or selects a manager. `DurableGrain` enrolls explicitly supplied managers implementing <xref:Orleans.ILifecycleParticipant`1> for <xref:Orleans.Runtime.IGrainLifecycle>, including a standard manager returned by a custom registration. It preserves the standard hosting factory's completed enrollment so that manager participates once. A custom manager injected into a plain grain uses its service factory or an explicit shared activation setup action to enroll. Ordinary DI registration makes a service resolvable; its factory or setup action establishes lifecycle subscriptions.

Managers created through <xref:Orleans.Journaling.IJournaledStateManagerFactory> with an explicit <xref:Orleans.Journaling.JournalId>, and manually constructed managers, retain caller-owned initialization and disposal. This applies inside grain calls as well as outside the runtime. Register their states, await <xref:Orleans.Journaling.IJournaledStateManager.InitializeAsync*> with a cancellation token, and dispose the manager when processing ends. A caller can deliberately assign lifecycle ownership by supplying a manager through its scoped registration to `DurableGrain` or explicitly enrolling it. Creation through the explicit-journal factory keeps failure handling independent of the ambient activation, including after the caller enrolls that manager in a lifecycle.

## Mutation and write acknowledgement

Durable collections encode their operation before applying it to the in-memory collection. A codec failure therefore leaves both the journal buffer and collection unchanged.

<xref:Orleans.Journaling.IJournaledStateManager.WriteStateAsync*> gathers pending entries from all named states and queues one storage operation:

- **Append** adds the encoded operation batch atomically.
- **Snapshot replacement** writes the current state of every registered stream and atomically publishes it as the new journal generation.

Concurrent calls made while the same kind of write is queued can share that queued operation. Each caller observes its completion or failure. Calls made after a storage operation starts are processed by a later operation.

> [!IMPORTANT]
> In-memory mutation is visible before storage acknowledgement. Return success to a caller only after the required <xref:Orleans.Journaling.IJournaledStateManager.WriteStateAsync*> completes. Recovery reconstructs durable state in a new activation.

## Safe-to-commit staging

All interleaved callers share the manager's pending journal. Prepare fallible work, external acknowledgements,
and proposed output in operation-local data. After establishing that an outcome is safe to commit, apply its
mutations to durable state and initiate a write. Coordinate that transition with other interleaved operations
which can affect the same decision. Any caller's write can include staged mutations from other calls.

If an application error occurs after staging and makes those mutations unsafe to commit, end the activation's
use of the manager and request deactivation. In-flight methods can retain local decisions and references
across awaits; a fresh activation reconstructs both application and durable state together.

## Consistency and competing writers

Orleans grain placement normally supplies a single active writer for a grain identity. Journal storage providers also use optimistic concurrency to protect the journal when a stale or competing writer reaches storage.

An append, replacement, or delete with stale storage metadata throws <xref:Orleans.Storage.InconsistentStateException>. The state manager permanently fences further operations, faults queued work, and requests grain deactivation. The failed operation reports its original exception. A new activation replays the stored journal to determine the durable outcome.

Design commands to tolerate retries at the application boundary. Use operation identifiers when a caller can repeat a command after an uncertain network outcome.

## Storage failures

A failed append, snapshot replacement, delete, or initialization permanently fences that manager instance.
Queued operations fault, and later write, delete, registration, and initialization requests fail explicitly.
Existing in-memory state remains available to in-flight calls until deactivation completes. The grain runtime
starts deactivation as part of handling the failure.

A storage operation can commit before its acknowledgement is lost. Treat a failed write as an uncertain
application outcome and reconcile in a new activation using an operation identifier or another idempotency
mechanism.

Owners of standalone managers created through <xref:Orleans.Journaling.IJournaledStateManagerFactory>
dispose the failed manager and create a new one for the same <xref:Orleans.Journaling.JournalId>. Register
new durable state instances and initialize them before resuming processing.

Cancelling a write's cancellation token stops the caller's wait. An already queued write continues to its
storage outcome, so the caller reconciles that outcome before retrying the command.

An initialization failure preserves stored data for diagnosis. Restore the required format/codec registration
or repair the backing data before creating a fresh manager or retrying activation.

## Compaction

Each provider reports when its journal crosses a configured storage threshold. The next <xref:Orleans.Journaling.IJournaledStateManager.WriteStateAsync*>:

1. Builds a snapshot containing the state directory and every active durable state.
1. Atomically replaces the published journal with the snapshot.
1. Clears the compaction request after storage acknowledges the replacement.

Compaction bounds replay work and storage growth according to provider thresholds. Snapshot size still scales with the complete durable state owned by the grain, so capacity tests must include hot and large grain identities.

## Retire a named state

Recovery preserves streams whose names are no longer registered by the current grain type. The manager starts a retirement grace period for each such state. The default minimum is seven days, configured with <xref:Orleans.Journaling.JournaledStateManagerOptions.RetirementGracePeriod>.

Reintroducing the same state name during the grace period replays its preserved entries into the new state instance. After the grace period has elapsed, a later compaction removes the retired stream. Permanent removal can therefore occur later than the configured period.

This behavior supports staged deployments and rollback. Keep the previous format codecs available while retired streams remain. A format migration pauses when an unregistered stream can't be decoded into a snapshot.

## Deactivation and shutdown

Grain deactivation stops the lifecycle-bound state manager's work loop, and activation-scope disposal releases its resources. Completed writes are the durability barrier. Await every required write during the grain call which made the mutation, and size host shutdown grace periods for writes already in progress. Owners of explicit-journal or manually constructed managers arrange their own shutdown and disposal.
