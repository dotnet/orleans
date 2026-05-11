# Orleans DurableJobs

Orleans DurableJobs schedules one-time durable work for Orleans grains and coordinates execution across silos.

## Language

**Durable Job**:
A one-time scheduled work item targeting an Orleans grain.
_Avoid_: Reminder, recurring job

**Live Job**:
A **Durable Job** which remains eligible for future execution, retry, cancellation, or completion.
_Avoid_: Pending record

**Job Metadata**:
Caller-provided string key/value data carried by a **Durable Job**.
_Avoid_: Log Storage Properties, shard metadata

**Job Shard**:
A time-window group of **Durable Jobs** coordinated as one ownership and recovery unit.
_Avoid_: Queue partition, blob

**Job Shard Id**:
An opaque DurableJobs identity for one **Job Shard**.
_Avoid_: Log Storage Id, string shard id

**Shard Time Window**:
The due-time interval covered by one or more **Job Shards**.
_Avoid_: Global bucket

**Writable Job Shard**:
A **Job Shard** owned by the local scheduler and still accepting new **Schedule Job Operations**.
_Avoid_: Active shard

**Closed Job Shard**:
A **Job Shard** which no longer accepts new jobs and only drains, retries, or removes existing **Live Jobs**.
_Avoid_: Completed shard

**Shard Owner**:
The silo currently responsible for writing and executing a **Job Shard**.
_Avoid_: Writer, holder

**Schedule Job Operation**:
A durable change which adds a **Live Job** to a **Job Shard**.
_Avoid_: Add command

**Remove Job Operation**:
A durable change which removes a **Live Job** from a **Job Shard** because it no longer needs execution.
_Avoid_: Completion operation, cancellation operation

**Retry Job Operation**:
A durable change which keeps a **Live Job** in a **Job Shard** with a later due time and increased dequeue count.
_Avoid_: Re-add operation

**Dequeue Count**:
The persisted retry attempt count for a **Live Job**.
_Avoid_: Execution attempt count

**Run Id**:
A volatile identity for one in-flight execution attempt of a **Live Job**.
_Avoid_: Job Id, persisted attempt id

**Job Completion**:
The executor outcome where a **Durable Job** finishes successfully.
_Avoid_: Remove

**Job Cancellation**:
A caller request to prevent a **Durable Job** from future execution.
_Avoid_: Delete

**Routed Job Cancellation**:
A **Job Cancellation** forwarded to the current **Shard Owner** before writing the **Remove Job Operation**.
_Avoid_: Any-silo cancellation write

**Poisoned Job Shard**:
A **Job Shard** which exceeded the dead-owner adoption limit and is no longer assigned automatically.
_Avoid_: Failed shard, deleted shard

**Persist-before-return**:
The rule that a DurableJobs state change must be durably written before the initiating call returns.
_Avoid_: Best-effort persistence

## Relationships

- A **Job Shard** contains zero or more **Live Jobs**.
- A **Durable Job** can carry **Job Metadata**.
- A **Job Shard Id** identifies exactly one **Job Shard**.
- A **Job Shard Id** maps internally to a Journaling **Log Storage Id**.
- A **Shard Time Window** can contain multiple **Job Shards**.
- A **Job Shard** is the ownership and recovery unit; it is not the cluster-wide canonical bucket for its **Shard Time Window**.
- A **Shard Time Window** constrains initial scheduling into a **Job Shard**, not later retry due times.
- A **Writable Job Shard** can become a **Closed Job Shard**.
- A claimed orphaned or dead-owner **Job Shard** is a **Closed Job Shard**.
- A **Shard Owner** is the only silo which writes schedule, remove, or retry operations for its **Job Shard**.
- A **Shard Owner** must be an active silo recorded in shard **Log Storage Properties**.
- Only a **Writable Job Shard** accepts **Schedule Job Operations**.
- A **Schedule Job Operation** creates one **Live Job**.
- A **Remove Job Operation** removes one **Live Job** regardless of whether the reason is **Job Completion** or **Job Cancellation**.
- A **Retry Job Operation** preserves one **Live Job** while changing its due time and dequeue count.
- A **Retry Job Operation** keeps the **Live Job** in the same **Job Shard**, even when the new due time is outside the original **Shard Time Window**.
- **Dequeue Count** increases through persisted **Retry Job Operations**, not through crash/takeover reruns.
- A **Run Id** is generated when a **Live Job** is yielded for execution and is not stored in the shard journal.
- **Job Completion** and **Job Cancellation** are reasons for removal, not distinct replay semantics.
- DurableJobs follows **Persist-before-return** for schedule, remove, and retry state changes.
- DurableJobs may apply a state change before or after the physical write, but it must not return to the initiating caller while that change is unpersisted.
- Multiple DurableJobs state changes may share one Journaling **Log Extent** if each initiating caller waits for the flush containing its change.
- DurableJobs shard journals use the configured Journaling **Log Format** and **Codec Family**.
- A **Job Shard** is represented by one custom DurableJobs durable state machine.
- DurableJobs shard snapshots contain **Live Jobs** and their **Dequeue Counts**, not completed or canceled job history.
- A **Job Shard** with no **Live Jobs** should be deleted only by its owner, using conditional storage deletion when available.
- A **Routed Job Cancellation** is forwarded to the current **Shard Owner**; if there is no owner, cancellation returns false.
- A **Routed Job Cancellation** must verify ownership at the recipient before writing.
- A stale **Routed Job Cancellation** returns false instead of writing or forwarding again.
- A **Routed Job Cancellation** does not claim orphaned or dead-owner shards as a side effect.
- **Job Cancellation** does not abort an in-flight grain call.
- **Job Cancellation** prevents future execution, retry, or rerun when the job is still live.
- Repeated **Remove Job Operations** for the same job are idempotent during replay.
- DurableJobs storage extensibility belongs to Journaling **Log Storage** and **Log Storage Catalog** providers, not DurableJobs-specific shard storage providers.
- Public DurableJobs APIs expose **Job Shard Ids**, not Journaling **Log Storage Ids**.
- DurableJobs owns time-based scheduling, shard activation, overload handling, and execution concurrency; Journaling owns durable storage and catalog coordination.
- A **Poisoned Job Shard** is retained for diagnosis and manual intervention instead of being deleted or reassigned automatically.

## Example dialogue

> **Dev:** "Should cancellation and completion be separate journal operations?"
> **Domain expert:** "No — both mean the **Live Job** is removed from the **Job Shard**. The reason matters to the caller or executor, but replay only needs the **Remove Job Operation**."

## Flagged ambiguities

- "remove" is the durable replay action; **Job Completion** and **Job Cancellation** are higher-level reasons for that action.
- Dirty DurableJobs state may exist briefly inside an operation, but **Persist-before-return** must hold at API boundaries.
- **Job Cancellation** does not allow arbitrary silos to write to owned shard journals.
- **Job Metadata** belongs to a **Durable Job**; **Log Storage Properties** belong to a shard journal.
