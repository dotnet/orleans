# Orleans Journaling

Orleans Journaling persists durable state machine changes as ordered log data which can be replayed to recover in-memory durable collections and values.

## Language

**Log Extent**:
A physical batch of journal data containing one or more **Log Entries**.
_Avoid_: Segment

**Log Entry**:
The logical item inside a **Log Extent**, consisting of a state machine id and one durable operation payload.
_Avoid_: Record

**Control Log Entry**:
A reserved future concept for non-state-machine journal metadata if a concrete need emerges.
_Avoid_: Physical boundary marker

**Log Entry Writer**:
A reusable `IBufferWriter<byte>` used inside a `ref struct` lexical scope to encode one pending **Log Entry**. Disposing the scope aborts the entry unless it was committed.
_Avoid_: Per-entry writer allocation

**Log Format**:
A narrow physical-format service which creates **Log Extent** writers and reads stored log bytes.
_Avoid_: Public codec-family abstraction

**Log Extent Writer**:
A reusable, format-owned writer for one pending **Log Extent**. The manager owns it and storage may consume its read-only view only for the duration of an append or replace operation.
_Avoid_: Storage-owned writer

**State Machine Storage**:
Opaque persistence for encoded journal bytes. It exposes the selected **Log Format** key but does not own the format implementation or decode **Log Entries**.
_Avoid_: Storage codec

**Durable Operation**:
A state-machine-specific change encoded inside a **Log Entry**, such as list add, dictionary set, or queue dequeue.
_Avoid_: Command object, wrapper

**Value Codec**:
A format-specific expert extension point which serializes and deserializes values embedded in **Durable Operations**.
_Avoid_: Universal value codec, log data codec

**Codec Family**:
A cohesive set of codecs for **Log Extents**, **Log Entries**, **Durable Operations**, and values in one serialization format.
_Avoid_: Mix-and-match codecs

**Steady-state Journaling Overhead**:
The heap allocation caused by journaling infrastructure after warm-up, excluding durable collection growth and user value codec behavior.
_Avoid_: Total allocation

**JSONL Log Format**:
A human-readable physical log format which writes each **Log Entry** as one JSON object line. A **Log Extent** can contain multiple newline-delimited **Log Entries**.
_Avoid_: Performance format

**Retired State Machine**:
A durable state machine which appears in persisted log data but is not currently registered by user code.
_Avoid_: Unknown stream

**Runtime State Machine Id**:
A state machine id in the reserved range 0-7, used by Orleans runtime journaling infrastructure. Application state machine ids begin at 8.
_Avoid_: User-reserved id

## Relationships

- A **Log Extent** contains one or more **Log Entries**.
- A **Log Extent** is an atomic write or compaction unit; its boundary is not a semantic recovery boundary.
- **Log Extent** boundaries are not represented by special entries or markers in the current format.
- A physical format can frame each **Log Entry** directly in an ordered byte stream without adding a **Log Extent** envelope.
- A **Log Entry** belongs to exactly one durable state machine.
- A **Log Entry** with a **Runtime State Machine Id** belongs to Orleans journaling infrastructure, not user code.
- **Log Format** readers parse physical framing and dispatch ids and payloads; they do not interpret runtime id semantics.
- A **Log Entry** contains exactly one **Durable Operation** payload.
- A **Log Format** owns physical log byte framing but does not by itself define a full **Codec Family**.
- A **Log Extent Writer** is caller-owned; storage must not retain the `GetCommittedBuffer()` view after an append or replace operation returns.
- **State Machine Storage** stores and returns encoded bytes; the manager uses the selected **Log Format** to decode those bytes into **Log Entries**.
- The storage provider can choose a **Log Format** key from the grain type; the manager resolves the keyed **Log Format** service from that key.
- A **Log Format** key selects the cohesive **Codec Family**: physical log format, durable operation codecs, and value codecs.
- A grain has one active **Log Format** key. All runtime and user durable state machines managed by that grain's manager must use that same key.
- Durable state machine services are unkeyed within a grain and resolve codecs using the grain's active **Log Format** key.
- **Log Format** keys are separate from storage provider names or storage identity.
- Malformed recovery data is a hard failure. The next recovery attempt resets volatile state and replays from storage.
- A **Log Entry Writer** is reused across entries and must not be retained by codecs after the write call returns.
- A pending **Log Entry** should not escape the lexical scope which began it.
- Durable operation codecs synchronously encode one operation and must not store **Log Entry Writers**.
- The lexical entry scope type is `LogEntry`.
- A pending **Log Entry** can complete only once. `Commit` finalizes it; `Dispose` aborts it only if it was not committed; double completion is invalid.
- `LogEntryWriter` is payload-only. Entry lifecycle operations belong to the lexical scope, not to durable operation codecs.
- Aborting a pending **Log Entry** truncates the caller-owned **Log Extent Writer** to its pre-entry state; aborted data must never affect stored data or later writes.
- Successful **Log Entries** are batched in the manager-owned pending **Log Extent Writer** until `WriteStateAsync` flushes them.
- After a successful flush, a **Log Extent Writer** should be reset and reused when the format supports safe reuse.
- A **Durable Operation** may contain values encoded by a **Value Codec**.
- A **Codec Family** owns the **Log Extent**, **Log Entry**, **Durable Operation**, and **Value Codec** choices together.
- **Steady-state Journaling Overhead** excludes allocations caused by the in-memory collection or the journaled value itself.
- The **JSONL Log Format** is newline-delimited by **Log Entry** and is not the strict zero-allocation performance target.
- A **Retired State Machine** can temporarily retain copied **Log Entries** so its data is preserved across recovery and compaction.
- Retired preservation stores the state machine id plus durable operation payload bytes, not exact physical extent bytes. JSONL may re-emit a fresh line during compaction.

## Example dialogue

> **Dev:** "When a durable list adds an item, do we allocate an Add command and then write it?"
> **Domain expert:** "No — the list writes one **Log Entry** directly into the current **Log Extent**, and the encoded **Durable Operation** is the add."

## Flagged ambiguities

- "record" was used for the framed item inside an extent, but this context uses **Log Entry** for that concept.
- "segment" appears in older comments and tests, but this context uses **Log Extent** for the physical batch.
- JSONL is a readable/debug/interchange format with best-effort allocation reduction, not the strict zero-allocation path. JSONL lines are **Log Entries**, not **Log Extents**.
- `ILogValueCodec<T>` was the earlier value serialization term, but this context uses **Value Codec** for the replacement API.
- Universal value codecs were considered, but **Value Codecs** are format-specific to avoid mixing JSON, protobuf, MessagePack, and Orleans binary responsibilities.
- A public **Log Format** is intentionally narrower than a public codec-family abstraction.
- Storage is intentionally format-agnostic: it may expose a **Log Format** key, but it should not inject, own, or call a **Log Format** implementation.
- Recovery parses ordered concatenated log data; it must not depend on storage preserving individual **Log Extent** boundaries.
- Existing persisted data must match the configured **Log Format**. Switching formats over existing data is a migration concern outside this design.
- The **Log Format** key is not persisted in journal data; storage provider configuration must remain consistent with stored bytes.
- Format selection delegates receive the grain type only, not the full `IGrainContext` or `GrainId`.
- `LogExtentBuilder` and `IStateMachineLogExtentCodec` are retired from built-in paths; do not describe new designs in terms of builders or post-hoc extent codecs.
- If future metadata needs a journal-level entry, model it explicitly as a **Control Log Entry**, not as a hidden physical boundary marker.
- State machine ids 0-7 are reserved for runtime/control use; do not allocate them to application durable state machines.
- The manager, not the physical reader, owns reserved-id semantics.
- MessagePack journaling is closed-generic and AOT-friendly; it must not fall back to generalized runtime `Type` dispatch.
