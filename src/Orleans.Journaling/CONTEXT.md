# Orleans Journaling

Orleans Journaling persists durable state changes as ordered journal data which can be replayed to recover in-memory durable collections and values.

## Language

**Journal Batch**:
A physical batch of journal data containing one or more **Journal Entries**.
_Avoid_: Segment

**Journal Entry**:
The logical item inside a **Journal Batch**, consisting of a state id and one durable operation payload.
_Avoid_: Record

**Control Journal Entry**:
A **Journal Entry** with a **Runtime State Id** whose payload carries journaling infrastructure metadata instead of an application **Durable Operation**.
_Avoid_: Physical boundary marker

**Journal Entry Writer**:
A reusable `IBufferWriter<byte>` used inside a `ref struct` lexical scope to encode one pending **Journal Entry**. Disposing the scope aborts the entry unless it was committed.
_Avoid_: Per-entry writer allocation

**Journal Format**:
A narrow physical-format service which creates **Journal Writers** and reads stored journal bytes.
_Avoid_: Public codec-family abstraction

**Journal Writer**:
A reusable, format-owned writer for one pending **Journal Batch**. The manager owns it and storage may consume its read-only view only for the duration of an append or replace operation.
_Avoid_: Storage-owned writer

**Journal Storage**:
Opaque persistence for encoded **Journal Entries**. It exposes the selected **Journal Format** key but does not own the format implementation or decode **Journal Entries**.
_Avoid_: State Storage, storage codec

**Journal Storage Id**:
A stable, provider-neutral hierarchical identity for one **Journal Storage** instance, such as a grain journal or a DurableJobs shard journal.
_Avoid_: Journal Id, State Storage Id

**Journal Storage Prefix**:
A provider-neutral hierarchical prefix used by a **Journal Storage Catalog** to list related **Journal Storage Ids**.
_Avoid_: Blob prefix, physical path

**Journal Storage Catalog**:
A provider capability for creating, listing, inspecting, and conditionally updating **Journal Storage Properties** for **Journal Storage** instances.
_Avoid_: Shard manager, provider registry

**Journal Storage Properties**:
Provider-visible coordination data for a **Journal Storage** instance, used for discovery, ownership, and conditional updates.
_Avoid_: Control Journal Entry, Durable Operation

**Journal Storage ETag**:
A storage concurrency token used for conditional property updates, appends, replaces, and deletion.
_Avoid_: Version, lease token

**Compaction Request**:
A provider signal that a **Journal Storage** instance should be replaced with a snapshot on the next write.
_Avoid_: DurableJobs policy, storage rewrite command

**Durable Operation**:
A state-specific change encoded inside a **Journal Entry**, such as list add, dictionary set, or queue dequeue.
_Avoid_: Command object, wrapper

**Value Codec**:
A format-specific expert extension point which serializes and deserializes values embedded in **Durable Operations**.
_Avoid_: Universal value codec, journal data codec

**Codec Family**:
A cohesive set of codecs for **Journal Batches**, **Journal Entries**, **Durable Operations**, and values in one serialization format.
_Avoid_: Mix-and-match codecs

**Steady-state Journaling Overhead**:
The heap allocation caused by journaling infrastructure after warm-up, excluding durable collection growth and user value codec behavior.
_Avoid_: Total allocation

**JSONL Journal Format**:
A human-readable physical journal format which writes each **Journal Entry** as one JSON object line. A **Journal Batch** can contain multiple newline-delimited **Journal Entries**.
_Avoid_: Performance format

**Retired State**:
A durable state which appears in persisted journal data but is not currently registered by user code.
_Avoid_: Unknown stream

**Runtime State Id**:
A state id in the reserved range 0-7, used by Orleans runtime journaling infrastructure. Application state ids begin at 8.
_Avoid_: User-reserved id

**State Manager**:
The runtime component which binds durable states to one **Journal Storage**, replays **Journal Entries**, and flushes pending **Durable Operations**.
_Avoid_: Grain context, storage provider

**State Manager Handle**:
An owned lifecycle wrapper for a **State Manager** created outside grain activation.
_Avoid_: Synthetic grain context

**Transactional Journaled Grain**:
A durable grain mode whose normal requests are method-bracketed transactions over all durable states in the grain journal.
_Avoid_: Explicit transactional state wrapper

**In-place Speculative Execution**:
A transactional journaling mode where uncommitted **Durable Operations** mutate live durable states and isolation depends on non-interleaved request execution.
_Avoid_: Copy-on-write transaction state

**Abort-by-Recovery**:
A transaction abort strategy which resets durable states and replays stored **Journal Entries** while omitting the aborted transaction region.
_Avoid_: Per-operation undo

## Relationships

- A **Journal Batch** contains one or more **Journal Entries**.
- A **Journal Batch** is an atomic write or compaction unit; its boundary is not a semantic recovery boundary.
- **Journal Batch** boundaries are not represented by special entries or markers in the current format.
- A physical format can frame each **Journal Entry** directly in an ordered byte stream without adding a **Journal Batch** envelope.
- A **Journal Entry** belongs to exactly one durable state.
- A **Journal Entry** with a **Runtime State Id** belongs to Orleans journaling infrastructure, not user code.
- A **Control Journal Entry** is still framed and ordered like any other **Journal Entry**; its meaning is owned by the manager for that **Runtime State Id**.
- **Journal Format** readers parse physical framing and dispatch ids and payloads; they do not interpret runtime id semantics.
- A **Journal Entry** contains exactly one **Durable Operation** payload.
- A **Journal Format** owns physical journal byte framing but does not by itself define a full **Codec Family**.
- A **Journal Writer** is caller-owned; storage must not retain the `GetCommittedBuffer()` view after an append or replace operation returns.
- **Journal Storage** stores and returns encoded bytes; the manager uses the selected **Journal Format** to decode those bytes into **Journal Entries**.
- A **Journal Storage Id** identifies exactly one **Journal Storage** instance.
- A **Journal Storage Id** is logical; providers map it to physical storage names.
- A **Journal Storage Id** is separate from a **Journal Format** key.
- A **Journal Storage Prefix** can match many **Journal Storage Ids**.
- A **Journal Storage Catalog** manages **Journal Storage** instances, not **Journal Entries** or **Journal Formats**.
- A **Journal Storage Catalog** creates **Journal Storage** conditionally and reports identity conflicts to callers.
- A **Journal Storage Catalog** reads and updates **Journal Storage Properties**.
- A **Journal Storage Catalog** lists **Journal Storage Ids** in lexicographic **Journal Storage Id** order for prefix listings.
- A **Journal Storage Catalog** streams listing results so callers can stop before reading every matching **Journal Storage Id**.
- **Journal Storage Properties** are outside the replayed **Journal Entries**.
- **Journal Storage Properties** are for coordination and discovery; durable state belongs in **Journal Entries** or snapshots.
- **Journal Storage Properties** are string key/value pairs; higher layers own typed interpretation.
- Provider-owned physical metadata may be visible as **Journal Storage Properties** for diagnostics.
- Provider-owned **Journal Storage Properties** are protected from caller mutation.
- **Journal Storage Properties** updates use patch or transform semantics so protected provider keys are preserved.
- A **Journal Storage ETag** follows Orleans grain persistence ETag semantics.
- A **Journal Storage ETag** is set, cleared, and refreshed by storage operations.
- A **Journal Storage ETag** is supplied to conditional catalog updates where the provider supports it.
- An opened `IJournalStorage` acquires its **Journal Storage ETag** from its own read, create, append, replace, and delete operations.
- Catalog property ETags are not injected into opened `IJournalStorage` instances.
- `IJournalStorage.DeleteAsync` uses the storage instance's current **Journal Storage ETag** for conditional deletion where the provider supports it.
- A **Compaction Request** is raised by **Journal Storage** and honored by the **State Manager**.
- Grain journaling can open one **Journal Storage Id** without using a **Journal Storage Catalog**.
- DurableJobs uses a **Journal Storage Catalog** to discover and claim shard journals.
- The storage provider can choose a **Journal Format** key from the grain type; the manager resolves the keyed **Journal Format** service from that key.
- The storage provider can choose a **Journal Format** key from a **Journal Storage Id** for non-grain logs.
- A **Journal Format** key selects the cohesive **Codec Family**: physical journal format, durable operation codecs, and value codecs.
- A grain has one active **Journal Format** key. All runtime and user durable states managed by that grain's manager must use that same key.
- JSONL is the default built-in **Journal Format** key for new storage-provider configurations; Orleans binary remains available only when a provider explicitly selects its key.
- Durable state services are unkeyed within a grain and resolve codecs using the grain's active **Journal Format** key.
- A **State Manager** owns recovery and write ordering for one **Journal Storage**.
- A **State Manager Handle** owns shutdown and disposal for a **State Manager** created by services such as DurableJobs.
- **Journal Format** keys are separate from storage provider names or storage identity.
- Malformed recovery data is a hard failure. The next recovery attempt resets volatile state and replays from storage.
- A **Journal Writer** is reused across entries and must not be retained by codecs after the write call returns.
- A pending **Journal Entry** should not escape the lexical scope which began it.
- Durable operation codecs synchronously encode one operation and must not store payload writers.
- The lexical entry scope type is `JournalEntryScope`.
- A pending **Journal Entry** can complete only once. `Commit` finalizes it; `Dispose` aborts it only if it was not committed; double completion is invalid.
- `JournalWriter` implements `IBufferWriter<byte>` for the active entry payload only. Entry lifecycle operations belong to the lexical scope, not to durable operation codecs.
- Aborting a pending **Journal Entry** truncates the caller-owned **Journal Writer** to its pre-entry state; aborted data must never affect stored data or later writes.
- Successful **Journal Entries** are batched in the manager-owned pending **Journal Writer** until `WriteStateAsync` flushes them.
- A **State Manager** may flush **Journal Entries** from multiple callers in one **Journal Batch** when each caller waits for the flush containing its changes.
- After a successful flush, a **Journal Writer** should be reset and reused when the format supports safe reuse.
- A **Durable Operation** may contain values encoded by a **Value Codec**.
- A **Codec Family** owns the **Journal Batch**, **Journal Entry**, **Durable Operation**, and **Value Codec** choices together.
- **Steady-state Journaling Overhead** excludes allocations caused by the in-memory collection or the journaled value itself.
- The **JSONL Journal Format** is newline-delimited by **Journal Entry** and is not the strict zero-allocation performance target.
- A **Retired State** can temporarily retain copied **Journal Entries** so its data is preserved across recovery and compaction.
- Retired preservation stores the state id plus durable operation payload bytes, not exact physical extent bytes. JSONL may re-emit a fresh line during compaction.
- **In-place Speculative Execution** keeps the commit path close to normal journaling: write the **Journal Entry**, mutate live state, and rely on scheduling to hide uncommitted state.
- **Abort-by-Recovery** treats transaction abort as a replay concern, not as a **Durable Operation** undo concern.
- A **Transactional Journaled Grain** uses call-chain reentrancy for same-transaction callbacks but rejects application-level interleaving by default.
- A read-only request on a **Transactional Journaled Grain** must not append **Durable Operations**.

## Example dialogue

> **Dev:** "When a durable list adds an item in a transaction, do we build a copy-on-write list first?"
> **Domain expert:** "No — a **Transactional Journaled Grain** uses **In-place Speculative Execution** for the fast path: the list writes the same **Journal Entry** shape and mutates live state, while ordinary non-interleaved execution prevents unrelated requests from observing it."

## Flagged ambiguities

- "record" was used for the framed item inside an extent, but this context uses **Journal Entry** for that concept.
- "segment" appears in older comments and tests, but this context uses **Journal Batch** for the physical batch.
- JSONL is a readable/debug/interchange format with best-effort allocation reduction, not the strict zero-allocation path. JSONL lines are **Journal Entries**, not **Journal Batches**.
- `IJournalValueCodec<T>` was the earlier value serialization term, but this context uses **Value Codec** for the replacement API.
- Universal value codecs were considered, but **Value Codecs** are format-specific to avoid mixing JSON, protobuf, MessagePack, and Orleans binary responsibilities.
- A public **Journal Format** is intentionally narrower than a public codec-family abstraction.
- Storage is intentionally format-agnostic: it may expose a **Journal Format** key, but it should not inject, own, or call a **Journal Format** implementation.
- Recovery parses ordered concatenated journal data; it must not depend on storage preserving individual **Journal Batch** boundaries.
- Existing persisted data can be read with a stored **Journal Format** key supplied by metadata-capable storage providers, then rewritten as a snapshot using the configured write format.
- The **Journal Format** key is not persisted in journal entry bytes; metadata-capable storage providers persist it as storage metadata, and metadata-less non-empty journals use a configured legacy fallback key.
- Format selection delegates receive the grain type only, not the full `IGrainContext` or `GrainId`.
- Non-grain format selection uses **Journal Storage Id**, not a synthetic `IGrainContext`.
- Builder-style extent APIs are retired from built-in paths; do not describe new designs in terms of builders or post-hoc extent codecs.
- Journal-level metadata is modeled explicitly as a **Control Journal Entry**, not as a hidden physical boundary marker.
- State ids 0-7 are reserved for runtime/control use; do not allocate them to application durable states.
- The manager, not the physical reader, owns reserved-id semantics.
- MessagePack journaling is closed-generic and AOT-friendly; it must not fall back to generalized runtime `Type` dispatch.
- Transaction abort in journaling means **Abort-by-Recovery**, not per-state undo or disposal of a pending **Journal Entry** writer.
- Interleaving in a **Transactional Journaled Grain** means same-transaction call-chain reentrancy, not arbitrary `[Reentrant]`, `[MayInterleave]`, or user `[AlwaysInterleave]` execution.
- `[ReadOnly]` on a **Transactional Journaled Grain** means durable writes fail; it is not an upgradeable transaction hint.
- "storage identity" in new API discussions means **Journal Storage Id**, not **Journal Id** or **State Storage Id**.
- "catalog" in new API discussions means **Journal Storage Catalog**, not a DurableJobs-specific shard manager.
- DurableJobs shard ownership, membership version, adopted count, poisoned status, and shard time range are **Journal Storage Properties**, not **Control Journal Entries**.
- DurableJobs shard ownership is built using **Journal Storage ETag** compare-and-swap; Journaling does not define a lease abstraction.
- DurableJobs creates **State Managers** through **State Manager Handles**, not synthetic grain contexts.
- DurableJobs does not introduce a separate compaction policy unless a future need emerges.
