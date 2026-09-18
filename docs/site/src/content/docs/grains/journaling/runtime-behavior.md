---
title: Journaling runtime behavior and consistency
description: Understand Orleans Journaling activation, write, recovery, compaction, concurrency, and failure semantics.
ms.date: 08/21/2026
ms.topic: conceptual
---

# Journaling runtime behavior and consistency

Orleans Journaling assigns one <xref:Orleans.Journaling.JournalId> to each grain identity and one journal stream to each named durable state. The state manager serializes recovery and storage work for that journal.

## Activation and recovery

The standard grain-scoped <xref:Orleans.Journaling.IDurableStateManager> registered by <xref:Orleans.Journaling.JournalingHostingExtensions.AddJournaling*> enrolls itself in the grain lifecycle when constructed with the activation's <xref:Orleans.Runtime.IGrainContext>, before resolution returns. Resolving a keyed durable state also resolves this manager. Each activation shares one manager across its injected states and features. Its registry owns state creation and binding, so compatible requests through <xref:Orleans.Journaling.IDurableStateManager.GetOrAddState*>, its typed extension helpers, or keyed injection return the same instance.

Constructor injection registers durable states before lifecycle startup. Orleans completes the grain constructor and assigns <xref:Orleans.Runtime.IGrainContext.GrainInstance>, then runs any shared activation setup actions before calling the grain object's `Participate` method and starting the lifecycle. A feature can first resolve the manager, declare additional activation-scoped states, and enroll its own participant in those synchronous actions. All subscriptions are established before lifecycle startup.

The standard manager participates once, supporting ordinary <xref:Orleans.Grain> subclasses, application-owned bases, <xref:Orleans.IGrainBase> implementations, and the <xref:Orleans.Journaling.DurableGrain> convenience base. Its grain-bound constructor subscribes through the existing lifecycle participation mechanism. Resolve grain-owned managers and declare their states during construction or synchronous activation setup so subscriptions are established before lifecycle startup. If lifecycle subscription fails, the constructor disposes the failed manager and its writer, then propagates the failure.

During <xref:Orleans.Runtime.GrainLifecycleStage.SetupState>, the manager:

1. Reads the journal as an ordered byte stream.
1. Selects the reader named by the stored format metadata.
1. Rebuilds the state-name directory.
1. Resets and replays each registered durable state.
1. Completes activation setup after replay finishes.

<xref:Orleans.Grain.OnActivateAsync*> and requests observe recovered durable state after setup succeeds, whether the grain derives directly from <xref:Orleans.Grain>, from an application-owned base, or from <xref:Orleans.Journaling.DurableGrain>. A storage read, format, codec, or malformed-data failure fails activation and preserves the stored journal for diagnosis and recovery.

Provider registration makes Journaling services available. Per-grain journal I/O begins only for activations which resolve the manager, directly or through durable-state dependencies. Grains which use other persistence models keep their existing activation behavior.

Lifecycle stages execute in order, with callbacks at the same stage eligible to run concurrently. A feature which reads recovered state subscribes after `SetupState`; a feature which must finish before `OnActivateAsync` subscribes before <xref:Orleans.Runtime.GrainLifecycleStage.Activate>. Shared setup actions execute after construction and are reused concurrently across activations, so they resolve scoped state through the supplied context. See [Shared activation setup](../grain-lifecycle.md#shared-activation-setup).

State creation belongs to setup before initialization. After initialization begins, `GetOrAddState` can resolve existing registrations; adding a missing name fails immediately before changing the registry. <xref:Orleans.Journaling.IDurableStateManager.TryGetState*> provides lookup without creation. Application reads and mutations use the reconstructed state after initialization succeeds.

Storage providers can split reads at arbitrary byte boundaries. The journal format buffers incomplete entries and only applies complete ordered records.

### Custom and caller-owned managers

Grain-scoped managers are enrolled before resolution returns. The standard manager establishes this in its grain-bound constructor. Custom managers establish it in their constructor or registration factory by enrolling their <xref:Orleans.ILifecycleParticipant`1> for <xref:Orleans.Runtime.IGrainLifecycle>, or subscribing initialization and shutdown callbacks directly. This gives ordinary grains, application-owned bases, and `DurableGrain` the same recovery and shutdown lifecycle for their injected managers.

Managers created through <xref:Orleans.Journaling.IJournaledStateManagerFactory.CreateStandalone*> with an explicit <xref:Orleans.Journaling.JournalId>, and managers constructed directly from storage without a grain context, retain caller-owned initialization and disposal. This applies inside grain calls as well as outside the runtime. Register their states, await <xref:Orleans.Journaling.IJournaledStateManager.InitializeAsync*> with the operation's cancellation token, and dispose the manager when processing ends. A caller can deliberately assign lifecycle ownership by enrolling the manager in a grain-scoped registration factory. Standalone creation keeps failure handling independent of the ambient activation, including after the caller enrolls that manager in a lifecycle.

Grain-owned managers use the existing activation scope. A standalone manager creates its own scope lazily, on the first actual state-service resolution: requesting a missing DI-created state through `GetOrAddState`, or accessing <xref:Orleans.Journaling.JournalReplayContext.ServiceProvider> during replay. That one scope is bound to the manager and reused for isolation, scoped-service caching, and dependency disposal.

`CreateStandalone`, manual <xref:Orleans.Journaling.IJournaledStateManager.RegisterStateMachine*> calls, and lookups of existing states leave the scope unallocated. Recovery and writes which use already-supplied same-format codecs also remain scope-free until service resolution is needed. This avoids creating a scope for standalone integrations such as Durable Jobs shards which provide their own state and codecs. The manager disposes only the scope it owns; the grain runtime owns disposal of the activation scope.

## Mutation and write acknowledgement

Durable collections encode their operation before applying it to the in-memory collection. A codec failure therefore leaves both the journal buffer and collection unchanged.

<xref:Orleans.Journaling.IDurableStateManager.WriteStateAsync*> gathers pending entries from all named states and queues one storage operation. The protected `DurableGrain.WriteStateAsync` helper forwards to that same manager:

- **Append** adds the encoded operation batch atomically.
- **Snapshot replacement** writes the current state of every registered stream and atomically publishes it as the new journal generation.

Concurrent calls made while the same kind of write is queued can share that queued operation. Each caller observes its completion or failure. Calls made after a storage operation starts are processed by a later operation.

> [!IMPORTANT]
> In-memory mutation is visible before storage acknowledgement. Return success to a caller only after the required `WriteStateAsync` completes. Recovery reconstructs durable state in a new activation.

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
dispose the failed manager and create a new one for the same <xref:Orleans.Journaling.JournalId>. Declare
new durable state instances with the manager's `GetOrAdd` helpers, or use
<xref:Orleans.Journaling.IJournaledStateManager.RegisterStateMachine*> for an explicitly owned
<xref:Orleans.Journaling.IStateMachine>, then initialize before resuming processing.

Cancelling a write's cancellation token stops the caller's wait. An already queued write continues to its
storage outcome, so the caller reconciles that outcome before retrying the command.

An initialization failure preserves stored data for diagnosis. Restore the required format/codec registration
or repair the backing data before creating a fresh manager or retrying activation.

## Compaction

Each provider reports when its journal crosses a configured storage threshold. The next `WriteStateAsync`:

1. Builds a snapshot containing the state directory and every active durable state.
1. Atomically replaces the published journal with the snapshot.
1. Clears the compaction request after storage acknowledges the replacement.

Compaction bounds replay work and storage growth according to provider thresholds. Snapshot size still scales with the complete durable state owned by the grain, so capacity tests must include hot and large grain identities.

## Retire a named state

Recovery preserves streams whose names are no longer registered by the current grain type. The manager starts a retirement grace period for each such state. The default minimum is seven days, configured with <xref:Orleans.Journaling.JournaledStateManagerOptions.RetirementGracePeriod>.

Reintroducing the same state name during the grace period replays its preserved entries into the new state instance. After the grace period has elapsed, a later compaction removes the retired stream. Permanent removal can therefore occur later than the configured period.

This behavior supports staged deployments and rollback. Keep the previous format codecs available while retired streams remain. A format migration pauses when an unregistered stream can't be decoded into a snapshot.

Declare every state to retain on each activation, even when that activation never accesses its contents. Setup declarations determine which streams remain active. Omitting a declaration starts retirement; register that same name during setup on a later activation to recover it within the grace period.

## Deactivation and shutdown

Grain deactivation stops the lifecycle-bound state manager's work loop, and activation-scope disposal releases its resources. Completed writes are the durability barrier. Await every required write during the grain call which made the mutation, and size host shutdown grace periods for writes already in progress. Owners of explicit-journal or standalone managers arrange their own shutdown and disposal.
