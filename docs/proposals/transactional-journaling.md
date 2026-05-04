# Transactional journaling

## Summary

Add an Orleans Transactions integration for Orleans Journaling so that every
`IDurableStateMachine` attached to a grain log can participate in distributed
transactions as one ACID resource. A transactional method should be able to
remove an item from an `IDurableDictionary<K, V>` on one grain and add it to an
`IDurableDictionary<K, V>` on another grain with the same commit, abort,
isolation, and recovery semantics as existing `ITransactionalState<TState>`.

The proposed design treats a grain's journal as the transaction participant.
All durable state machines in that journal share one transactional lock,
one local sequence, and one transaction recovery log. Transaction metadata is
persisted using a reserved runtime state machine id, while committed operations
remain ordinary journal entries bracketed by Control Log Entries. This keeps the
commit path close to existing journaling and relies on abort-by-recovery to reset
and replay state while omitting aborted transaction regions. The transaction
protocol, lock behavior, wire contracts, and recovery decisions should stay as
close as possible to `Orleans.Transactions`; journaling supplies a different
participant storage adapter, not a different transaction algorithm.

## Background

### Orleans Journaling today

Journaling stores ordered log entries for durable state machines owned by a
grain log:

- `DurableGrain` resolves `IStateMachineManager` and registers state machines
  with stable names.
- `LogStateMachineManager` maps stable names to numeric state machine ids. Id
  `0` is the durable state machine directory, id `1` is the retired state
  machine tracker, and application ids start at `8`.
- `IDurableStateMachine` exposes `Apply`, `AppendEntries`, `AppendSnapshot`,
  `OnRecoveryCompleted`, `OnWriteCompleted`, and `DeepCopy`.
- `ILogStorage` persists opaque encoded log bytes using atomic `AppendAsync`,
  `ReplaceAsync`, and `DeleteAsync`. Storage is intentionally format-agnostic.
- Durable collections such as `DurableDictionary<K, V>` synchronously append a
  durable operation to their `LogStreamWriter` and then apply it to their
  in-memory data structure.
- Recovery resets volatile state, reads ordered log bytes, and uses the active
  `ILogFormat` to dispatch each log entry by state machine id.

This model is already atomic at the grain-log write boundary: one append or
snapshot can contain updates from multiple state machines in the same grain.
It is not currently transactional across grains, and mutations are applied to
the live state machine before storage is flushed.

### Orleans Transactions today

Transactions currently center on `ITransactionalState<TState>`:

- `TransactionContext` propagates the ambient `TransactionInfo`.
- `TransactionalState<TState>.PerformRead` and `PerformUpdate` record a read or
  write participant in `TransactionInfo`.
- `ReadWriteLock<TState>` provides isolation and conflict handling.
- `TransactionQueue<TState>` implements the participant-side and
  transaction-manager-side protocol.
- `ITransactionalStateStorage<TState>` persists pending prepare records,
  committed sequence ids, commit records, and metadata using optimistic ETags.
- Recovery reloads committed state and pending prepare records. Remote pending
  prepares are resumed by contacting the transaction manager; local unfinished
  transactions are presumed aborted.

The important reusable idea is not the serialized `TState` shape. It is the
protocol: a participant durably prepares its post-transaction state before
replying prepared, commits only after the transaction manager confirms, and
keeps enough metadata to resume confirmations or discover aborts after failure.

## Goals

- Allow all journaled state machines in one grain log to join an ambient
  Orleans transaction.
- Preserve the existing non-transactional journaling behavior when no
  transaction is active.
- Preserve journaling's storage/format separation: storage stores opaque log
  bytes and must not decode durable operations.
- Reuse the Orleans Transactions protocol and wire protocol so journaled grains
  can transact with existing `ITransactionalState<TState>` grains.
- Minimize implementation divergence from `Orleans.Transactions`: reuse or
  factor the current transaction agent, resource extensions, lock semantics,
  access counters, confirmation worker behavior, and recovery rules wherever
  possible instead of creating a second transaction algorithm.
- Support method-bracketed transactions for transactional journaled grains so
  user code can mutate durable collections directly inside grain methods without
  explicit `PerformRead` or `PerformUpdate` calls.
- Match the current read-only transaction behavior: read-only transactions take
  shared read locks, readers can share with readers, readers conflict with
  writers, and writers conflict with readers.
- Make request scheduling transaction-aware so in-place speculative execution
  cannot be observed by unrelated requests.
- Provide crash recovery for prepared, committed-but-unconfirmed, aborted, and
  compacted transactions.
- Avoid requiring every durable collection type to become an independent
  transaction participant.

## Non-goals

- Per-state-machine transaction participants inside one grain log. The first
  design uses the grain log as the participant and therefore locks the whole
  journal for transactional writes.
- Store-level distributed transactions across multiple `ILogStorage`
  instances.
- Changing `ILogStorage` into a log-format-aware API.
- Making undetected in-place mutations of mutable values transactional. The
  transaction covers journaled operations; values returned from collections
  must still be treated according to the existing durable collection semantics.
- Changing the persisted format of existing non-transactional journal entries.
- Making `OneWay` calls transactional. Transactional method-bracketing requires
  a response path to report commit or abort.

## Proposed design

### Reuse of Orleans Transactions

Transactional journaling should be a new storage/state representation for the
existing Orleans Transactions protocol, not a separate protocol. The integration
should continue to use the current transaction agent and wire-visible contracts:

- `TransactionContext` and `TransactionInfo` for ambient propagation and
  participant reconciliation;
- `ParticipantId`, `AccessCounter`, `TransactionalStatus`, and the existing
  resource/manager extension interfaces;
- `ITransactionalResource` and `ITransactionManager` as the participant surface
  exposed by a journaled grain;
- the current two-phase commit roles: read-only participant, remote commit
  participant, and local transaction manager participant;
- the current two-phase locking behavior, including lock validation by access
  counters, lock upgrade failure behavior, `UseExclusiveLock`, lock timeouts,
  and Early Lock Release;
- the current confirmation, ping, cancel, collect, and presumed-abort recovery
  rules.

The implementation should therefore factor reusable pieces from
`TransactionQueue<TState>`, `ReadWriteLock<TState>`, `TransactionManager<TState>`,
`TransactionalResource<TState>`, and the confirmation worker before introducing
journaling-specific equivalents. Some code will need a journal-specific storage
adapter because the durable unit is a bracketed sequence of log entries rather
than a serialized `TState` snapshot. That adapter should plug into the same
participant engine where practical. If a temporary fork is needed to land the
feature, the fork should preserve method names, state transitions, log messages,
and tests closely enough that it can be collapsed back into shared transaction
infrastructure.

The target is compatibility with existing transactional grains: a transaction
may touch both `ITransactionalState<TState>` and a transactional journaled grain,
and the transaction agent should not need to know which storage model backs each
participant.

### Transaction participant granularity

Introduce a transaction-enabled state machine manager for journaling. The
manager registers one Orleans Transactions resource for the grain log, for
example with participant name `"$journal"` and the grain reference as the
participant reference.

When any durable state machine in the log is read or written under an ambient
`TransactionContext`, the manager records the grain-log participant in the
ambient `TransactionInfo`.

The grain log, not each durable collection, is the participant:

- A transaction touching two dictionaries on the same grain records one
  participant.
- A transaction touching dictionaries on two grains records two participants.
- All journaled state machines in one grain share one local transaction
  sequence and one isolation lock.

This matches the current persistence boundary. `LogStateMachineManager` already
flushes the directory and all registered state machines through one grain-local
log append or snapshot.

### Primary execution model

The first implementation should use in-place speculative execution with
abort-by-recovery. Under this model, transactional writes append ordinary
application log entries and mutate live durable state machines immediately, just
as non-transactional journaling does today. Transaction control entries emitted
under runtime state machine id `2` bracket those ordinary entries and persist the
prepare, commit, confirm, cancel, and recovery metadata needed by Orleans
Transactions.

This choice assumes aborts are rare. The fast path avoids copy-on-write replicas,
duplicate nested-log encoding, and reapplying the same durable operations after
commit. The cost moves to the uncommon abort and recovery paths: the manager must
reset state machines and replay the journal through a transaction-aware filter
which omits canceled transaction regions and resolves pending prepares.

The conservative nested-log/copy-on-write design remains a useful fallback and
reference point, but it should not be the primary implementation target unless
the scheduler/replay-filter requirements prove infeasible.

No per-operation undo stack is required in the primary model. If a transaction
which mutated live state aborts before it is durably prepared, the manager can
poison the activation for user work, abort or cascade-abort affected in-memory
transactions, reset all durable state machines in the grain log, and recover
from durable storage. Deactivating the grain and letting the next activation
recover is the simplest correct implementation of the same rule. The key
invariant is that no user request may continue observing an activation after its
live journal state is known to include aborted speculative effects.

### Method-bracketed transactional grains

The best developer experience is a transactional grain mode where every normal
request is implicitly bracketed by a transaction. User code should look like
ordinary journaling code:

```csharp
[TransactionalJournaled]
public sealed class InventoryGrain : DurableGrain, IInventoryGrain
{
    private readonly IDurableDictionary<string, InventoryItem> _items;

    public async Task MoveItem(IInventoryGrain target, string itemId)
    {
        var item = _items[itemId];
        _items.Remove(itemId);
        await target.AddItem(itemId, item);
    }

    [ReadOnly]
    public Task<int> CountItems() => Task.FromResult(_items.Count);
}
```

The grain class or grain interface would opt in using an attribute such as
`[TransactionalJournaled]`. The attribute should implement
`IGrainPropertiesProviderAttribute` and stamp a grain-type property, following
the same pattern as `[Reentrant]`, `[MayInterleave]`, and
`[StatelessWorker]`. A grain-type configurator would then install:

- the transaction-enabled journaling manager;
- a journal transaction participant and transaction manager resource registered
  with the grain context;
- interleaving validation for transactional journaled grains;
- an incoming method-bracketing component which enters call-chain reentrancy for
  the transaction scope.

The default method policy for a transactional journaled grain should be
`CreateOrJoin`:

- if an incoming request already carries a transaction, fork and join it;
- if the incoming request has no transaction, start a root transaction before
  invoking the method;
- if the method has `[ReadOnly]`, start a read-only root transaction, record
  reads, and fail the transaction if any durable write is attempted;
- allow explicit method-level overrides for `Suppress`, `NotAllowed`, `Join`,
  `Create`, and `UseExclusiveLock`.

This is larger than the existing `[Transaction]` implementation. Today,
`[Transaction]` changes the generated invokable base type to
`TransactionRequestBase`, which starts or joins a transaction and wraps the
response in `TransactionResponse`. That only applies to annotated methods, and
state participation is still explicit through `PerformRead` and
`PerformUpdate`. Method-bracketed transactional journaling needs a target-grain
mode which applies even when callers use ordinary generated request types.

The method-bracketing component should enter a
`RequestContext.AllowCallChainReentrancy()` scope for the duration of the
transactional method body and root transaction resolution. Outgoing calls made
inside that scope carry the Orleans call-chain reentrancy id, so callbacks in the
same transaction can re-enter the activation using the existing scheduler rule
for matching reentrancy ids.

### Transaction propagation envelope

Implicit transactional grains should not rely on every interface method being
generated as a `TransactionRequestBase`. Instead, transaction context should be
propagated as runtime message metadata:

1. A root request to a transactional journaled grain starts a `TransactionInfo`
   on the target activation before invoking user code.
2. A global or transaction-aware outgoing call filter sees ambient
   `TransactionContext`, forks it, and attaches the fork to the outgoing request
   metadata unless the callee method suppresses transactions.
3. The callee activation imports the metadata before invoking user code and
   sets `TransactionContext`.
4. The callee returns the reconciled transaction info in response metadata.
5. The caller merges the returned info into its ambient transaction.
6. The root activation resolves or aborts the transaction before sending the
   user response.

The existing `TransactionRequestBase` and `TransactionResponse` are useful
models, but this design probably needs a runtime-level request/response
transaction envelope so transaction propagation is independent of generated
method base types. `RequestContext` already flows with messages, but the
response path also needs to return participant information for reconciliation.

### Call-chain reentrancy and interleaving restrictions

The primary design should not require a new activation request scheduler, inbox
abstraction, or `ActivationData.MayInvokeRequest` hook. Existing Orleans
scheduling already blocks unrelated non-interleavable requests while a normal
request is executing. It also supports call-chain reentrancy: a request carrying
the active `RequestContext.ReentrancyId` can be admitted while the activation is
inside the matching reentrant section.

Transactional journaling should use that existing mechanism for
same-transaction reentrancy. The method-bracketing component opens the
call-chain reentrancy section before invoking user code and keeps it open until
root transaction resolution, abort handling, and abort-by-recovery have
completed. This prevents call cycles from deadlocking when grain A calls grain B
and B calls back into A inside the same transaction.

In-place speculative execution is not safe with arbitrary application
interleaving because the live durable state machines can contain uncommitted
data. Therefore `[TransactionalJournaled]` should reject application-level
interleaving by default:

- grain-level `[Reentrant]`;
- grain or interface `[MayInterleave]` predicates;
- user methods marked `[AlwaysInterleave]`.

This should be enforced by grain-type validation and analyzers where possible.
The only interleaving allowed by default is same-call-chain reentrancy and
Orleans system/protocol calls needed by the transaction implementation.
Transaction protocol extension calls such as `Prepare`, `PrepareAndCommit`,
`Prepared`, `Ping`, `Abort`, `Cancel`, and `Confirm` remain interleavable because
they are system calls marked `[AlwaysInterleave]` and
`[Transaction(TransactionOption.Suppress)]`.

Read-only interleaving remains safe only because read-only transactional methods
are not allowed to write durable state. If a `[ReadOnly]` transactional
journaled method attempts to append a durable operation locally, or calls another
transactional journaled grain which attempts to append a durable operation in the
same read-only transaction, the manager must fail the transaction before
appending or mutating state. It must not upgrade a read-only transaction to a
write transaction after the scheduler has admitted other read-only calls.

### Automatic journal participation

Method bracketing gives every transactional journaled grain method an ambient
`TransactionContext`, but the journal still needs to record participant access
without explicit `PerformRead` or `PerformUpdate` calls.

The transaction-enabled state machine manager should automatically record:

- a read when a durable state machine read API is used under an ambient
  transaction;
- a write when a durable operation is appended under an ambient transaction;
- an exclusive lock request when the method or grain policy requires it.

If the ambient transaction is read-only, the manager must reject durable writes
before any `Begin` control entry, application log entry, or live state mutation
occurs. This includes writes attempted by a callee which joined a read-only
transaction started by its caller.

For conservative correctness, a non-`[ReadOnly]` method can also be treated as a
potential write at scheduling time even if no durable operation is eventually
emitted. This preserves isolation for in-place speculative execution and keeps
the first implementation simple. The transaction agent already commits an empty
transaction cheaply when no participants are recorded.

### Read-only lock behavior

Transactional journaling should preserve the current Orleans Transactions
read-only semantics instead of introducing snapshot or MVCC reads in the first
design.

Current Orleans transactions use two-phase locking with shared read groups:

- read-only transactions acquire read locks on each participant they touch;
- multiple non-exclusive readers can share the same lock group;
- readers conflict with active writers and wait behind them;
- writers conflict with active readers and wait behind them;
- `UseExclusiveLock` makes even read access conflict as an exclusive lock;
- a write attempted under a read-only transaction fails instead of upgrading the
  transaction.

Read-only journaling therefore still participates in the transaction protocol. A
read-only root transaction resolves using the existing one-phase
`CommitReadOnly` path: the journal participant validates the lock and access
counters, appends or batches a `Read(timestamp)` control entry when required to
advance durable transaction metadata, releases the lock, and returns `Ok`. It
does not append `Prepare`, `Commit`, `Confirm`, or `Collect` records because
there is no write fate to coordinate.

This is intentionally conservative. A future version can explore immutable
snapshot reads over the log, but that would be a distinct concurrency-control
mode and must not silently change the behavior of transactions which also touch
existing `ITransactionalState<TState>` participants.

### Reserved transaction state machine id

Reserve runtime state machine id `2` for transactional journaling metadata:

| Id | Owner |
| --- | --- |
| `0` | State machine directory |
| `1` | Retired state machine tracker |
| `2` | Journal transaction state machine |
| `3`-`7` | Reserved for future runtime/control use |
| `8+` | Application durable state machines |

The new journal transaction state machine persists the equivalent of
`ITransactionalStateStorage<TState>` metadata for a journal:

- committed local sequence id;
- causal timestamp metadata;
- pending prepare records;
- transaction-manager recovery identity for remote prepares;
- transaction-manager commit records and write participants for confirmation
  recovery;
- compacted snapshot state for unresolved transactions.

In the primary in-place design, the transaction state machine stores Control Log
Entries which bracket ordinary application Log Entries. A prepared transaction
therefore consists of the `Begin`/`Prepare` metadata plus the buffered
application entries between them. The nested-log fallback can instead store
opaque nested journal bytes inside `Prepare`, using the same `ILogFormat` as the
grain log.

### Why transaction brackets require replay filtering

A natural idea is to append a special "begin transaction" entry, then ordinary
state-machine entries, then a special "commit" or "abort" entry. That is only
safe if recovery understands transaction regions.

Current recovery dispatches each ordinary log entry to its target state machine
as soon as the format reader sees it. Log extents are explicitly not semantic
recovery boundaries, and storage is not required to preserve append boundaries.
If aborted transaction operations are written as ordinary entries between
control markers, recovery must not dispatch them immediately to application
state machines. The transactional manager therefore needs a replay filter:
`Begin` enters transaction replay mode, application entries are buffered by state
machine id and payload bytes, and `Confirm` or `Cancel` decides whether the
buffer is applied or discarded.

The conservative alternative is for the transaction state machine to carry
transaction operations as a nested opaque log extent:

```text
outer entry: id=2, payload=Prepare(seq, txId, tm, timestamp, nestedLogBytes)
outer entry: id=2, payload=Confirm(seq)
```

Recovery applies `nestedLogBytes` to application state machines only when a
commit/confirm control entry makes the prepare durable and committed. Abort or
cancel simply discards the pending nested bytes. That alternative avoids replay
filtering but adds copy-on-write state, duplicate encoding, and commit-time
reapplication, so it is not the preferred path when aborts are rare.

### Transaction control operations

The transaction state machine should define a small operation set:

| Operation | Purpose |
| --- | --- |
| `Begin(sequenceId, transactionId, transactionManager, timestamp)` | Start a bracketed transaction region in the journal. |
| `Read(timestamp)` | Persist the causal timestamp for read-only transactions when required by the transaction algorithm. |
| `Prepare(sequenceId, transactionId, timestamp, transactionManager)` | Persist that the preceding bracketed operation region is prepared before reporting prepared. |
| `Commit(transactionId, timestamp, writeParticipants)` | Persist the transaction-manager commit record used to recover confirmation. |
| `Confirm(sequenceId)` | Make a prepared transaction committed locally and eligible to apply to stable state. |
| `Cancel(sequenceId, status)` | Abort a prepared transaction that has not committed locally. |
| `Collect(transactionId)` | Remove a transaction-manager commit record after all write participants confirm. |
| `Snapshot(...)` | Preserve committed metadata, pending prepares, and commit records during compaction. |

The operation names can be encoded however each log format's control-operation
codec chooses. The important invariant is that application log entries between
`Begin` and `Prepare` are not considered committed for recovery until `Confirm`
is observed. `Cancel` discards that bracketed region.

### Transactional mutation path

Non-transactional writes keep the existing path:

1. A durable collection writes an operation to its manager-backed
   `LogStreamWriter`.
2. The collection applies the operation to its live state.
3. `WriteStateAsync` appends or snapshots the current manager batch.

Transactional writes use the same write-then-apply shape under the transaction
gate:

1. The first transactional read or write records the grain-log participant in
   `TransactionInfo`.
2. The first transactional write emits `Begin` into a transaction-owned pending
   log batch.
3. Durable collections write ordinary durable operations through their existing
   `LogStreamWriter` path and immediately mutate their live in-memory state.
4. Remote prepare flushes `Begin`, the ordinary application entries, and
   `Prepare` atomically before sending `Prepared(Ok)`.
5. Local commit can flush `Begin`, ordinary application entries, `Prepare`,
   `Commit`, and `Confirm` in one append.
6. Confirm appends `Confirm` if needed; no reapplication is needed because the
   live state already contains the committed changes.
7. Abort or cancel appends `Cancel` when necessary, resets all state machines,
   and replays the journal while omitting the canceled transaction.

The transaction state machine therefore brackets ordinary application log
entries:

```text
outer entry: id=2, payload=Begin(seq, txId, tm, timestamp)
outer entry: id=8, payload=DictionaryRemove(...)
outer entry: id=9, payload=DictionarySet(...)
outer entry: id=2, payload=Prepare(seq, txId, tm, timestamp)
outer entry: id=2, payload=Confirm(seq) // or Cancel(seq)
```

This removes the largest fast-path costs from the conservative design:

- no copy-on-write replicas for every touched state machine;
- no duplicate nested log encoding;
- no second application of the same operations on commit;
- fewer custom transaction-aware collection implementations for writes, since
  the existing write-then-apply pattern is the desired behavior.

The cost is that abort becomes expensive and replay semantics become more
complex. Recovery must understand transaction regions:

- Entries between `Begin` and `Prepare` are buffered as pending transaction
  entries, not immediately applied to application state machines.
- `Confirm` applies the buffered entries in sequence and advances the committed
  sequence id.
- `Cancel` discards the buffered entries.
- A recovered remote prepare without `Confirm` or `Cancel` remains pending and
  contacts the transaction manager to learn its fate.
- A recovered local prepare without a commit decision is presumed aborted.
- A malformed or incomplete transaction region is treated as a hard recovery
  failure unless the transaction state machine rules can classify it as an
  unprepared local abort.

This replay filter can be implemented without making storage format-aware. The
manager can enter a transaction replay mode when the id `2` state machine
applies `Begin`, and the resolver can return buffering state machines for
application ids until the corresponding transaction decision is observed. The
buffer stores state machine id plus durable operation payload bytes, similar in
spirit to retired state machine preservation.

This optimization also tightens isolation requirements. While a speculative
in-place transaction is active, the live state contains uncommitted data. The
design relies on ordinary non-interleaved activation scheduling, same-call-chain
reentrancy, and rejection of application-level interleaving to prevent unrelated
requests from observing durable state until the transaction commits or the abort
recovery pass completes. The manager should still hold the grain-log transaction
lock across the prepared interval and route every durable read through the
manager.

The resulting design is lower overhead on the expected commit path, but it is
not strictly simpler overall. It replaces copy-on-write state with a more
careful transaction-aware replay filter and an explicit "abort by recover"
mechanism.

### Abort and reset semantics

Abort-by-recovery is a live-memory cleanup strategy, not a replacement for the
two-phase commit durability rules.

Before the journal participant has appended a durable `Prepare` control entry, it
can still abort locally. If the transaction has already mutated live durable
state, the participant must make the activation safe before any more user code
observes it:

1. mark the journal manager as poisoned for normal user requests;
2. reject new transactional journal work and stop admitting queued application
   work;
3. abort or cascade-abort executing and queued transactions whose reads or writes
   may depend on the aborted speculative state;
4. reset every durable state machine in the grain log, not just the state
   machines touched by the aborted transaction;
5. replay durable storage through the transaction-aware replay filter; and
6. resume normal work only after recovery reconstructs a valid committed and
   pending-prepared state.

The implementation can perform those steps inside the activation, or it can
deactivate the grain and rely on normal activation recovery. Deactivation is the
simplest correctness boundary because it also discards any non-journaled volatile
fields which user code may have derived from speculative durable state.

After a durable `Prepare` control entry has been appended, the participant is
prepared. From that point on, the participant must not make a unilateral local
abort decision, even if `Prepared(Ok)` has not yet been delivered to the
transaction manager. The prepared redo region and its transaction-manager
identity are durable recovery state. Recovery must preserve the pending prepare
and use the existing Orleans Transactions recovery path: send or retry prepared,
ping the transaction manager, and wait for `Confirm` or `Cancel`.

When `Cancel` arrives from the transaction manager, it is the coordinator's
decision. The participant appends `Cancel`, then uses the same reset/replay path
to remove the canceled region from live memory. When `Confirm` arrives, the
participant appends `Confirm`; no live reapplication is needed because in-place
execution already applied the operations, but recovery uses `Confirm` to decide
that the bracketed region is committed.

### Transactional reads

Reads under an ambient transaction must use the transaction's current view:

- In the nested-log design, if the transaction has already written to a state
  machine, read the transaction-local replica.
- In the in-place design, if the transaction has already written to any state
  machine in the grain log, read the live state under the transaction lock.
- If the transaction has not written to the grain log, read the most recent
  committed view allowed by the transaction lock.
- Record the read participant and causal timestamp in `TransactionInfo`.

Read APIs which expose collection views, such as dictionary keys or values,
must return data from the current transaction view when a transaction is active:
the replica view for the nested-log design, or the locked live view for the
in-place design. They must not expose mutable collections that can bypass the
transaction dispatch path.

The first implementation should use grain-log-level locking, mirroring
`ReadWriteLock<TState>`. This is conservative but correct: any transaction
which writes any state machine in a grain log conflicts with concurrent writes
to any other state machine in that same grain log.

Finer-grained per-state-machine locks can be considered later, but they would
need careful interaction with one shared log sequence and snapshot boundary.

### Commit protocol

The journaling transaction manager should follow the existing Orleans
Transactions roles.

#### Local transaction manager participant

When the grain log is the transaction manager for a writing transaction:

1. Validate the transaction lock and access counters.
2. Append a batch containing:
   - `Begin(sequenceId, transactionId, null, timestamp)`;
   - ordinary application log entries;
   - `Prepare(sequenceId, transactionId, timestamp, null)`;
   - `Commit(transactionId, timestamp, writeParticipants)`;
   - `Confirm(sequenceId)`.
3. After the append succeeds, mark the transaction committed locally and release
   the transaction lock. The live state already contains the changes.
4. Reply `Ok` to the transaction agent.
5. If there are remote write participants, start the confirmation worker.
6. After all write participants confirm, append `Collect(transactionId)`.

For a local transaction with no remote write participants, `Collect` can be
included in the same append as the commit, matching the existing
`StorageBatch<TState>` optimization.

The nested-log fallback persists the same protocol decision by placing the
transaction's operation bytes inside `Prepare`; it then applies those bytes after
the commit append succeeds.

#### Remote participant prepare

When another grain is the transaction manager:

1. Validate the transaction lock and access counters.
2. Append `Begin`, ordinary application entries, and
   `Prepare(sequenceId, transactionId, timestamp, transactionManager)`.
3. Only after the append succeeds, send `Prepared(..., Ok)` to the transaction
   manager.
4. Keep the prepared transaction in the commit queue until `Confirm` or
   `Cancel` arrives.

If the append fails before the durable `Prepare` record is stored, the
participant can abort locally and report a non-`Ok` status. Once the append
succeeds, the participant is prepared even if `Prepared(Ok)` has not yet been
sent or delivered. From that point forward, the participant must not
unilaterally abort; it must recover the transaction's fate from the transaction
manager and obey the eventual `Confirm` or `Cancel`.

The nested-log fallback appends a `Prepare` record containing nested operation
bytes instead of bracketed ordinary application entries.

#### Remote participant confirm

On `Confirm(transactionId, timestamp)`:

1. Find the prepared transaction.
2. Append `Confirm(sequenceId)`.
3. Complete the `Confirm` call so the transaction manager can collect. The live
   state already contains the prepared changes. Recovery remains responsible for
   applying the bracketed entries if the activation crashed before or after the
   confirm append.

#### Remote participant cancel

On `Cancel(transactionId, timestamp, status)`:

1. Find the prepared transaction.
2. Append `Cancel(sequenceId, status)`.
3. Reset durable state machines and replay the journal while omitting the
   canceled transaction region.
4. Release the transaction lock after replay completes.

### Recovery

Recovery must reconstruct both stable state and unresolved transaction state.

The transaction state machine owns this during replay:

1. `Begin` records enter transaction replay mode and cause subsequent
   application entries to be buffered by state machine id and payload bytes.
2. `Prepare` records make the buffered region a pending prepared transaction,
   but do not apply it immediately to application state machines.
3. `Confirm` applies the buffered entries in sequence order and advances the
   committed sequence id.
4. `Cancel` removes the buffered region without applying it.
5. `Commit` recreates transaction-manager commit records so confirmation can
   resume after activation.
6. `Collect` removes transaction-manager commit records.

After replay:

- Stable application state machines reflect only committed transactions and
  non-transactional journal entries.
- Pending remote prepares with a transaction manager identity are placed back
  in the commit queue. The participant pings the transaction manager to learn
  whether to confirm or cancel.
- Local pending prepares without a transaction manager identity are presumed
  aborted, matching existing Azure transactional storage recovery behavior.
- Commit records restart confirmation and collection for transactions whose
  manager committed locally before all participants confirmed.

### Compaction and snapshots

Compaction must preserve the distinction between committed state and unresolved
prepared state.

During a snapshot:

1. Write the state machine directory first.
2. Write the transaction state machine snapshot before application state
   machine snapshots.
3. Application state machine snapshots contain only committed state.
4. The transaction state machine snapshot contains unresolved pending prepares,
   their buffered operation entries, committed sequence metadata, timestamp
   metadata, and uncollected commit records.
5. Retired state machine preservation continues to use the retired tracker;
   pending prepares which reference a retired state machine must keep the
   buffered entries until the prepare is confirmed or canceled.

The manager should make runtime state machine snapshot ordering deterministic:
directory (`0`), retired tracker (`1`), transaction state (`2`), then
application ids in ascending id order. The current dictionary enumeration order
should not become part of the transaction recovery contract.

### Storage requirements

The current `ILogStorage` contract guarantees atomic append/replace/delete for
one grain log but does not expose ETags or compare-and-swap writes. Existing
transactional storage implementations use optimistic ETags to detect conflicts.

For transactional journaling, providers should support a versioned log write
contract when transactions are enabled:

```csharp
public interface IVersionedLogStorage : ILogStorage
{
    string? Version { get; }

    ValueTask<string?> AppendAsync(
        string? expectedVersion,
        ReadOnlySequence<byte> value,
        CancellationToken cancellationToken);

    ValueTask<string?> ReplaceAsync(
        string? expectedVersion,
        ReadOnlySequence<byte> value,
        CancellationToken cancellationToken);
}
```

The exact shape can vary, but the requirement is that a recovering or duplicate
activation cannot silently append transaction control entries over a newer log.
If a provider cannot offer versioned writes, it can still be used for
non-transactional journaling, but it should not advertise full transactional
ACID guarantees.

### Public API sketch

The integration should be opt-in so existing journaling users do not pay for
transaction coordination:

```csharp
builder.Services.AddOrleansJournalingTransactions();
```

or via silo configuration:

```csharp
siloBuilder
    .AddJournaling()
    .UseTransactions();
```

Transactional grain code should look like ordinary durable collection code
inside any grain method:

```csharp
[TransactionalJournaled]
public sealed class InventoryGrain : DurableGrain, IInventoryGrain
{
    private readonly IDurableDictionary<string, InventoryItem> _items;

    public async Task MoveItem(IInventoryGrain target, string itemId)
    {
        var item = _items[itemId];
        _items.Remove(itemId);
        await target.AddItem(itemId, item);
    }

    [ReadOnly]
    public Task<int> CountItems() => Task.FromResult(_items.Count);
}
```

The journaling manager supplies the participant behavior; the durable
dictionary does not need to know whether the target grain is involved in the
same distributed transaction. Explicit `[Transaction]` attributes remain useful
as method-level overrides, but they are not required on normal transactional
journaled grain methods.

### Implementation plan

1. Add the transaction integration package or feature layer, avoiding a hard
   dependency from non-transactional journaling on transactions unless the
   dependency is acceptable for `Microsoft.Orleans.Journaling`.
2. Add the `[TransactionalJournaled]` attribute and grain-type metadata key.
3. Add a grain-type configurator which installs the transaction-enabled
   journaling manager, method bracketing, and validation that rejects
   application-level interleaving on transactional journaled grains.
4. Add a runtime-level transaction propagation envelope for ordinary request and
   response messages, or generalize `TransactionRequestBase` so propagation is
   no longer tied to per-method generated request base types.
5. Add method-bracketing logic on the incoming invocation path so root requests
   enter call-chain reentrancy, start, resolve, abort, and clear transactions
   automatically.
6. Verify that Orleans transaction protocol extension calls remain schedulable
   while a transactional journaled request is resolving.
7. Reserve state machine id `2` and add `JournalTransactionStateMachine`.
8. Define transaction control operation codecs for each built-in log format.
9. Implement in-place speculative execution with abort-by-recovery as the
   primary mutation path. Keep the conservative nested-log approach documented as
   a fallback/reference design.
10. Add transaction-aware routing to manager-backed `LogStreamWriter` creation.
11. Route reads through the manager and prevent unrelated durable access while
    speculative state is visible.
12. Implement the transaction-aware replay filter which buffers bracketed
    application entries and omits canceled regions during recovery.
13. Extract reusable participant machinery from the existing
   `TransactionQueue<TState>`, `ReadWriteLock<TState>`,
   `TransactionManager<TState>`, `TransactionalResource<TState>`, confirmation
   worker, and resource extension plumbing so a journal log can be an
   `ITransactionalResource` and `ITransactionManager` without implementing a
   parallel transaction algorithm.
14. Persist prepare, commit, confirm, cancel, collect, and read timestamp events
   as transaction state machine entries.
15. Teach recovery to buffer bracketed ordinary log entries and apply them only
    on confirm.
16. Add versioned write support to transactional journaling storage providers.
17. Add tests covering multi-grain commit, abort, crash recovery, compaction,
    storage conflicts, and mixed transactional/non-transactional use.

## ACID properties

| Property | How the design provides it |
| --- | --- |
| Atomicity | Application entries are bracketed by transaction Control Log Entries, and replay filters omit canceled regions. Cross-grain atomicity comes from the existing Orleans Transactions two-phase protocol. The nested-log fallback can provide the same property by storing pending nested bytes and applying them only after `Confirm`. |
| Consistency | Each participant applies the same deterministic journal operations it would have applied non-transactionally under the transaction lock. In-place speculative effects are hidden from unrelated requests until commit, and abort-by-recovery removes them before normal work resumes. |
| Isolation | The grain-log transaction participant uses transaction locks, access counters, and transaction-aware request scheduling. The first implementation locks at grain-log granularity. In-place speculative execution additionally blocks unrelated durable access while uncommitted changes are visible in memory. |
| Durability | Prepare, commit, confirm, cancel, and collect decisions are persisted in the grain journal using atomic appends and recovered before the grain resumes normal work. |

## Failure scenarios

| Failure point | Recovery behavior |
| --- | --- |
| Abort before prepare is stored | Poison or deactivate the activation, discard any unflushed transaction batch, cascade-abort dependent in-memory transactions, reset state machines, and replay storage. The nested-log fallback would instead drop speculative state and nested bytes. |
| User method throws before root transaction resolves | The method-bracketing component records the exception, aborts the ambient transaction, performs any required abort recovery, and returns the transaction exception to the caller. |
| Crash after prepare is stored but before `Prepared(Ok)` is sent | Recover the pending prepare and contact the transaction manager. |
| Crash after `Prepared(Ok)` is sent but before confirm/cancel | Recover the pending prepare and ping the transaction manager until the fate is known. |
| Transaction manager commits locally but crashes before all participants confirm | Recover the commit record and resume sending confirmations. |
| Participant crashes after confirm is stored but before replying to manager | Recovery applies the prepared operations if needed, treats the transaction as committed locally, and makes confirm idempotent. |
| Cancel is stored | In-place recovery omits the canceled bracketed entries and releases the transaction. The nested-log fallback discards the pending prepare bytes. |
| Compaction occurs with pending prepares | The transaction state snapshot carries buffered operation entries; application snapshots include only committed state. |
| Storage version conflict | Abort executing transactions, reload the log, and recover transaction state before accepting new work. |

## Testing strategy

- Unit-test the transaction control operation codec for each log format.
- Unit-test recovery ordering: prepare without confirm, prepare plus cancel,
  prepare plus confirm, commit plus collect, and mixed transactional and
  non-transactional entries.
- Add a two-grain durable dictionary transfer test: remove from one grain and
  add to another, then verify both commit and abort paths.
- Add method-bracketing tests where transactional journaled grains commit and
  abort without explicit `[Transaction]`, `PerformRead`, or `PerformUpdate`
  calls.
- Add propagation tests where an implicit root transaction calls another
  transactional journaled grain and the caller reconciles the returned
  transaction metadata.
- Add validation tests proving transactional journaled grains reject
  application `[AlwaysInterleave]`, `[Reentrant]`, and `[MayInterleave]` by
  default.
- Add read-only transaction tests proving durable writes fail before log append
  or live mutation, including writes attempted by a callee in the same read-only
  transaction.
- Add same-transaction call-chain reentrancy and transaction protocol extension
  tests so callbacks plus `Prepare`, `PrepareAndCommit`, `Prepared`, `Ping`,
  `Abort`, `Cancel`, and `Confirm` remain schedulable while a user request is
  resolving.
- Add failure-injection tests at every protocol boundary: before/after prepare
  append, before/after prepared response, before/after confirm append, and
  before/after collect.
- Add tests which abort after live mutation and verify that reset/replay restores
  the pre-transaction state while preserving unrelated committed entries.
- Add compaction tests with unresolved remote prepares and uncollected commit
  records.
- Re-run existing journaling tests to ensure non-transactional behavior and log
  format compatibility are unchanged.
- Re-run existing transactions golden path, concurrency, exclusive-lock, and
  recovery tests with a journaling-backed participant.

## Open questions

- What is the smallest factoring boundary which lets journaling reuse the
  existing transaction participant engine while substituting journal control log
  entries for `ITransactionalStateStorage<TState>` snapshots?
- Should implicit transaction propagation use `RequestContext`, new message
  headers, a generalized response envelope, or generated invokable base types?
- Is grain-log-level locking acceptable for the first release, or do we need
  per-state-machine lock granularity immediately?
- Should transaction-enabled journaling require `IVersionedLogStorage`, or can
  some providers rely on Orleans single-activation guarantees?
- How should custom `IDurableStateMachine` implementations declare that their
  `Reset`/`Apply` behavior is safe for abort-by-recovery?
- Should non-transactional writes be allowed on a transaction-enabled manager,
  or should users opt into a mode that requires an ambient transaction for all
  durable writes?
- How should timers, reminders, stream deliveries, and grain extension calls be
  classified: implicit transactional requests, suppressed protocol/system calls,
  or opt-in policy decisions?
- What exact public configuration surface should enable the feature, especially
  if the implementation lives in a separate package?

