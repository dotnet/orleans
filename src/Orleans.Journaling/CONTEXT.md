# Orleans Journaling

Orleans Journaling persists durable state machine changes as ordered log data which can be replayed to recover in-memory durable collections and values.

## Language

**Log Extent**:
A physical batch of journal data containing one or more **Log Entries**.
_Avoid_: Segment

**Log Entry**:
The logical item inside a **Log Extent**, consisting of a state machine id and one durable operation payload.
_Avoid_: Record

**Log Entry Writer**:
A reusable `IBufferWriter<byte>` used to encode one pending **Log Entry**.
_Avoid_: Per-entry writer allocation

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
A human-readable physical log format which writes each **Log Extent** as one JSON array line.
_Avoid_: Performance format

**Retired State Machine**:
A durable state machine which appears in persisted log data but is not currently registered by user code.
_Avoid_: Unknown stream

## Relationships

- A **Log Extent** contains one or more **Log Entries**.
- A **Log Entry** belongs to exactly one durable state machine.
- A **Log Entry** contains exactly one **Durable Operation** payload.
- A **Log Entry Writer** is reused across entries and must not be retained by codecs after the write call returns.
- A **Durable Operation** may contain values encoded by a **Value Codec**.
- A **Codec Family** owns the **Log Extent**, **Log Entry**, **Durable Operation**, and **Value Codec** choices together.
- **Steady-state Journaling Overhead** excludes allocations caused by the in-memory collection or the journaled value itself.
- The **JSONL Log Format** preserves **Log Extent** boundaries but is not the strict zero-allocation performance target.
- A **Retired State Machine** can temporarily retain copied **Log Entries** so its data is preserved across recovery and compaction.

## Example dialogue

> **Dev:** "When a durable list adds an item, do we allocate an Add command and then write it?"
> **Domain expert:** "No — the list writes one **Log Entry** directly into the current **Log Extent**, and the encoded **Durable Operation** is the add."

## Flagged ambiguities

- "record" was used for the framed item inside an extent, but this context uses **Log Entry** for that concept.
- "segment" appears in older comments and tests, but this context uses **Log Extent** for the physical batch.
- JSONL is a readable/debug/interchange format with best-effort allocation reduction, not the strict zero-allocation path.
- `ILogDataCodec<T>` was the earlier value serialization term, but this context uses **Value Codec** for the replacement API.
- Universal value codecs were considered, but **Value Codecs** are format-specific to avoid mixing JSON, protobuf, MessagePack, and Orleans binary responsibilities.
- MessagePack journaling is closed-generic and AOT-friendly; it must not fall back to generalized runtime `Type` dispatch.
