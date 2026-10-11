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

Grain-scoped managers are enrolled before resolution returns. The standard manager establishes this in its grain-bound constructor and implements both the grain-facing `IDurableStateManager` and the independent journal-owner <xref:Orleans.Journaling.IJournaledStateManager>. Custom grain manager replacements provide both contracts and their registrations as appropriate. They establish enrollment in their constructor or registration factory by enrolling their <xref:Orleans.ILifecycleParticipant`1> for <xref:Orleans.Runtime.IGrainLifecycle>, or subscribing initialization and shutdown callbacks directly. This gives ordinary grains, application-owned bases, and `DurableGrain` the same recovery and shutdown lifecycle for their injected managers.

Managers created through <xref:Orleans.Journaling.IJournaledStateManagerFactory.CreateStandalone*> with an explicit <xref:Orleans.Journaling.JournalId>, and managers constructed directly from storage without a grain context, retain caller-owned initialization and disposal. This applies inside grain calls as well as outside the runtime. Construct state machine components with their dependencies, register them with <xref:Orleans.Journaling.IJournaledStateManager.RegisterStateMachine*>, and await <xref:Orleans.Journaling.IJournaledStateManager.InitializeAsync*> with the operation's cancellation token before using recovered state. Look up registered components with <xref:Orleans.Journaling.IJournaledStateManager.TryGetStateMachine*>. A caller can deliberately assign lifecycle ownership by enrolling the owner in a grain-scoped registration factory. Standalone creation keeps failure handling independent of the ambient activation, including after such enrollment.

Grain-owned managers use the existing activation scope for `GetOrAddState` and keyed component resolution. The runtime-owned scope controls disposal of the DI-created state components, their dependencies, and the manager. <xref:Orleans.Journaling.JournalingHostingExtensions.AddStateMachine*> registers application state components for this grain-facing path.

Standalone journal owners orchestrate manually registered components and never create or dispose DI scopes. Their callers supply component dependencies and manage their lifetimes, including any caller-owned scopes. Replay uses the configured shared journal services when it needs a codec for a stored format. Disposing the owner stops journal processing and releases journal resources; component and dependency disposal remains the caller's responsibility. Durable Jobs shards use this ownership model for their explicitly supplied state.

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
and proposed output in operation-local data. After the final preparation await, check the relevant
preconditions and apply the complete safe-to-commit update synchronously, then request an ordinary write.
Orleans executes that synchronous block on a single activation thread. Another grain turn can run when
the operation awaits, so keep shared state safe to commit at each await. Any caller's write can include
staged mutations from other calls.

## Journal operation hooks

Activation-scoped features coordinate prerequisites and completion through
<xref:Orleans.Journaling.IJournaledStateManager.Hooks>. The owner exposes a stable, lazily
allocated list of <xref:Orleans.Journaling.IJournaledStateHook> registrations. Inspect and
deduplicate feature registrations on the owner's logical execution context while persistence
is quiescent. Registration survives recovery and deletion; the standard manager rejects hook-list
mutation while persistence is queued or running.
Every journal owner implementation provides this list and runs its registered callbacks at the
operation boundaries below. Delegating owners forward the list to the inner owner.

Each actual append, snapshot, or deletion runs ordinary before callbacks in list order, outside
the manager lock. At most one <xref:Orleans.Journaling.IJournaledStateCaptureHook> supplies the
final prerequisite. Its before callback runs last, and the work loop awaits it directly before
synchronous capture or storage deletion. Prerequisites cover changes staged during asynchronous
preparation, including the final hook's own I/O wait. Preserve operation-local bookkeeping for
the captured batch separately from changes staged later.

Storage acknowledgement and registered-state acknowledgement or reset precede after callbacks.
All after callbacks run in list order, including for successful zero-byte writes. Coalesced callers
share callbacks for the actual operation. Features implement the before and after callbacks on
their own hook, retaining feature identity and operation-local bookkeeping there.

A failed prerequisite reports <xref:Orleans.Journaling.JournaledStatePreCommitException> with pending
state retained for an explicit persistence retry after the prerequisite is restored. A failed after
callback reports <xref:Orleans.Journaling.JournaledStatePostCommitException> with persistence completed.
Remaining after callbacks run, multiple failures are aggregated, and the manager stays usable.
The feature's durable recovery protocol resumes interrupted post-persistence work. Storage and
state-processing failures retain the manager's fencing and fresh-recovery behavior.

Hook callbacks receive the owner's shutdown token. Cancelling a caller's wait leaves the owned
operation running through its actual outcome. Disposal drains owned hooks and storage before releasing
journal resources, including when cancellation callbacks or cleanup fail. Concurrent disposal callers
share this completion. Hook implementations complete without calling initialization, persistence,
or disposal on their own owner. Awaiting an operation serialized behind the current callback creates
a circular dependency. Shutdown closes work admission and cancels queued operations while the current
operation drains to its actual storage and hook outcome. For deletion, the feature owner stops
admission and drains feature operations before
queuing the whole-journal reset.

## Consistency and competing writers

Orleans grain placement normally supplies a single active writer for a grain identity. Journal storage providers also use optimistic concurrency to protect the journal when a stale or competing writer reaches storage.

An append, replacement, or delete with stale storage metadata throws <xref:Orleans.Storage.InconsistentStateException>. The state manager permanently fences further operations, faults queued work, and requests grain deactivation. The failed operation reports its original exception. A new activation replays the stored journal to determine the durable outcome.

Design commands to tolerate retries at the application boundary. Use operation identifiers when a caller can repeat a command after an uncertain network outcome.

## Storage failures

A failed append, snapshot replacement, or delete permanently fences that manager instance.
Queued operations fault, and later write, delete, registration, and initialization requests fail explicitly.
Existing in-memory state remains available to in-flight calls until deactivation completes. The grain runtime
starts deactivation as part of handling the failure.

A storage operation can commit before its acknowledgement is lost. Treat a failed write as an uncertain
application outcome and reconcile in a new activation using an operation identifier or another idempotency
mechanism.

Owners of standalone managers created through <xref:Orleans.Journaling.IJournaledStateManagerFactory>
dispose the failed manager and create a new one for the same <xref:Orleans.Journaling.JournalId>.
Construct fresh state machine components with caller-supplied dependencies and register them using
<xref:Orleans.Journaling.IJournaledStateManager.RegisterStateMachine*>, then initialize before resuming
processing. The caller also retires the failed components and their dependencies according to their
assigned lifetimes.

Cancelling a write's cancellation token stops the caller's wait. An already queued write continues to its
storage outcome, so the caller reconciles that outcome before retrying the command.

An initialization failure reports its error to that attempt's callers and leaves the manager uninitialized.
The caller can retry <xref:Orleans.Journaling.IJournaledStateManager.InitializeAsync*> after a transient
failure or after restoring the required format, codec, or backing data. Each attempt resets recovery
bookkeeping and replays the journal from the beginning using the same registered state machines.
Concurrent callers share the active attempt; writes and deletion become available after initialization
succeeds. State registration stays closed once initialization has begun.
One work-loop task owns recovery attempts and subsequent journal work for the manager's lifetime.
After a failed attempt it waits for an explicit initialization request before retrying.

Owner shutdown which cancels initial recovery cancels all initialization waiters and leaves the manager
stopped. Disposal waits for the owned read to finish before releasing journal resources. Cancelling an
individual initialization caller's token ends only its wait; owned recovery continues for other callers.

## Custom state lifecycle

Custom <xref:Orleans.Journaling.IStateMachine> implementations share the manager's single logical execution thread.
Supply command codecs as constructor dependencies. The registration factory selects codecs keyed by the
same write-format key used to configure the journal owner. Activation-owned state factories resolve those
dependencies from the activation's services; standalone callers supply codecs with the appropriate lifetime.
The codec is available when the state is constructed, including for an empty journal.
During replay, <xref:Orleans.Journaling.JournalReplayContext.GetRequiredCommandCodec*> selects the codec
for each entry's stored format.

States synchronously encode their pending changes through <xref:Orleans.Journaling.IStateMachine.WritePendingEntries*>
or their current contents through <xref:Orleans.Journaling.IStateMachine.WriteSnapshot*>.
After storage acknowledges captured bytes, <xref:Orleans.Journaling.IStateMachine.OnWriteCompleted*>
performs durable-completion bookkeeping. A zero-byte write completes without this callback.

The journal owner keeps feature operations quiescent through deletion's storage and reset outcome,
including when a caller cancels its wait. Successful deletion calls <xref:Orleans.Journaling.IStateMachine.Reset*>
before completing deletion waiters.

The manager records the first write or delete failure, fences further persistence, faults current
and queued manager waiters, and requests grain deactivation. Features observe their write failures and
complete their own operation waiters and resource cleanup through their operation and lifecycle ownership.
Standalone callers own that cleanup explicitly. A previously captured write retains its actual storage
outcome and acknowledgement bookkeeping. Owner-canceled initial recovery and idle shutdown complete
through normal shutdown; admitted write/delete storage cancellation remains terminal.

### Durable Jobs shard retirement

When a shard runner terminates, including after an unexpected executor failure, the local Durable Jobs
manager withdraws its writable and cached references and retains responsibility for cleanup until
disposal finishes. Journaled shard retirement closes mutation admission and waits for previously
accepted operations at a queued completion barrier. Ownership cleanup then samples the drained job
count, releases populated shards for discovery, and deletes empty shard journals.

Scheduling calls which retained a retiring instance retry against a writable replacement. Queued jobs
acknowledged before retirement remain in the journal and recover through a fresh canonical
instance on discovery. Discovery retains distinct replacement instances while an old runner finishes
cleanup. Canonical lookups skip retiring instances, and local publication and activation check the
instance lifetime again when delayed creation or discovery results arrive. A later sweep opens a fresh
instance after eviction. Cleanup removes tracking entries by instance identity.

Cleanup failures are logged, and an unexpected executor failure remains the runner's failure. A prior
journal I/O failure fences deletion, preserving the persisted journal for recovery in a fresh instance.
Failed closure or ownership cleanup also evicts and disposes the retired local instance. Investigate
the logged storage or metadata failure and allow a fresh discovery sweep to recover persisted work.

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
