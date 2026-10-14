# Cassandra journaling implementation

This document describes the storage protocol for maintainers. The public
configuration surface is described in [README.md](README.md).

## Design goals

The provider stores an Orleans journal as a sequence of opaque bytes. Storage
chunks and Cassandra row boundaries are not journal-entry boundaries. The
provider must preserve the journal format's byte stream and expose one committed
version of that stream to recovery.

Cassandra is a partitioned, query-driven database. A table is not a general
relational store: efficient queries normally identify a partition key and then
read or mutate a known range of clustering keys. The schema is therefore built
around the operations this provider must make efficient and consistent.

## Table and partition layout

The table's primary key is:

```text
PRIMARY KEY ((journal_id), generation, sequence)
```

Each journal ID is one Cassandra partition. Within that partition, rows are
clustered by generation and then sequence. The partition contains:

| Row | `generation` | `sequence` | Populated data |
|---|---|---:|---|
| Header | `Guid.Empty` | `-1` | Active generation, expected row count and byte length, compaction counters, format, caller metadata, revision tokens, schema version |
| Entry | UUIDv7 generation | `0..N-1` | Operation ID and payload bytes |

The sentinel key reserves one row for the header without requiring a separate
table. Keeping the header and entries in one partition is intentional: a
conditional logged batch can atomically insert an append row and advance its
header. Splitting the header and entries across partitions or tables would
remove that same-partition atomicity.

Do not add queries which scan across journal partitions or use `ALLOW FILTERING`
for ordinary journal operations. Reads and deletes should include the journal
partition key and, where applicable, a generation and sequence range.

The provider projects only the columns needed for each operation. Header reads
fetch the manifest and metadata columns. Entry reads fetch `sequence` and
`payload`; they do not fetch operation IDs or header fields. Cassandra pages
the selected generation's clustering range in sequence order. Clustering order
is a logical query order; SSTable compaction and Cassandra's merge process
determine physical storage layout.

The storage contract permits a row or page boundary to split encoded journal
data. `ReadAsync` appends each row to one incremental buffer and invokes the
consumer with `IsCompleted` false; it allows the consumer to leave an incomplete
trailing record for the next row. After validating the manifest's row and byte
counts, it invokes the consumer once more with `IsCompleted` true and requires
all bytes to have been consumed. Keep tests for records which cross row and
page boundaries, as well as for a truncated final record. Page iteration must
process only rows already buffered by the Cassandra driver and await
`FetchMoreResultsAsync` before reading another page. Synchronous `RowSet`
enumeration can block while fetching a page and cannot observe cancellation
until the fetch returns. Cancellation stops the caller's wait; the driver does
not cancel the underlying page request, so its eventual failure must still be
observed.

## Consistency and revisions

All conditional mutations use Cassandra lightweight transactions (LWTs), with
serial consistency configured as `Serial` and regular consistency configured
as `Quorum` by default. LWTs use Paxos and cost more than ordinary writes; they
are used here because append, replace, metadata, and delete operations require
conditional serialization on the header.

The header has two random revision tokens:

- `revision` changes on every header mutation, including metadata-only updates.
- `content_revision` changes when journal bytes or the active generation change.

Header reads use a `SERIAL` read consistency level to complete any pending Paxos
round for that journal partition before interpreting the header. This is distinct
from the serial consistency setting on LWT statements: those use `QUORUM` for
the regular consistency level and `SERIAL` for the Paxos phase. Entry reads
remain at the configured regular read consistency level.

Append and replacement publication compare the expected header revision,
active generation, and next sequence. A mismatch means another writer changed
the header. A metadata-only revision change can be refreshed and retried when
the content revision is unchanged. A content change is an optimistic
concurrency failure and is reported as an inconsistent-state error.

Before appending or replacing, a storage handle creates a missing header with
an LWT. If it previously observed the journal as absent and that creation loses
to another writer, it rejects the operation and keeps the observed-absence
fence; the caller must recover before writing. A handle which has not observed
the journal can adopt an existing header after its create-if-absent LWT loses,
preserving cold-handle behavior.

An append inserts the next entry with `IF NOT EXISTS` and conditionally updates
the header in one **logged, same-partition conditional batch**. The LWT is on
the header update; the entry insert and header update commit together. This
prevents publishing a sequence/count/length that does not match the inserted
entry and prevents two writers from successfully claiming the same sequence.
When appending the first entry to an empty journal, the same LWT updates the
header's format key to the configured write format. This handles an empty
journal created under a different format, where recovery has no bytes from
which to request a migration snapshot. Non-empty journals retain their format
until replacement publishes a snapshot in the configured format. Empty appends
do not write a row. `MaxAppendBytes` bounds one append payload, not the
lifetime journal size.

## Replacement protocol

Replacement uses a new generation so the large snapshot does not need to be
written inside an LWT or one large Cassandra batch:

1. Generate a fresh UUIDv7 generation ID.
2. Split the snapshot into rows of at most `RowSizeBytes`, starting at sequence
   zero. Write staging rows in unlogged batches of at most 32 rows. Staging
   rows are not visible to recovery because the header still points to the old
   generation.
3. Use one header LWT to set the new active generation, sequence count, byte
   length, format, and fresh revisions. The LWT compares the header state read
   before staging. This header update is the replacement's commit point.
4. After publication, attempt an exact delete of the prior active generation,
   then issue a range delete for older UUID generations as a safety sweep.

An empty replacement publishes a generation with zero entries. Row sequence
numbers restart at zero because generation identity distinguishes replacement
contents from prior contents.

If staging fails before publication, the provider attempts to delete the
staged generation. If that cleanup also fails, both failures are reported.
If the publication LWT returns a conflict, the provider reads the header:
metadata-only changes can be retried, while a content change fails the
replacement and triggers staged-generation cleanup.

If the publication request fails without a definitive LWT result, the
operation's outcome is ambiguous: Cassandra may have committed the header
update even though the client did not receive its result. In this case, the
provider must not delete the staged generation, since the header may already
point to it. The exception is surfaced; recovery reads the header to determine
the committed generation.

After the LWT reports success, cleanup is not part of the commit. Failures of
either post-publication delete are logged and do not cause `ReplaceAsync` to
fail. The range sweep is attempted even if the exact old-generation delete
fails. A later successful replacement can retry cleanup.

Delete compares the freshly read header's `content_revision` with the content
revision last observed by that storage handle, when one exists. A content
change (including deletion and recreation) or a header appearing after the
handle observed absence is reported as a stale-state error. Metadata-only
changes are allowed because they do not change `content_revision`; the delete
uses the fresh header revision in its LWT to fence changes which race after
the read. A handle with no observed state can delete the current journal.
Delete removes the header with an LWT and only then deletes the active
generation. Removing the header makes the journal absent to new readers. Unlike
replacement cleanup, a failure to delete the active generation is surfaced by
`DeleteAsync` after the header has been removed; the surviving rows are
unreachable. A later replacement's age-based sweep may tombstone them after they
pass the cleanup cutoff. Cassandra removes tombstoned data later during
compaction; no provider background task currently reclaims unreachable rows.

## UUIDv7 generations and best-effort cleanup

Generation IDs are UUID version 7. Their leading bits encode Unix time in
milliseconds, followed by random bits. Cassandra's `uuid` clustering-key order
therefore allows generation-time range deletes without first listing every
generation or entry row in the partition.

After a replacement is committed and the exact previous generation delete is
attempted, the provider issues the equivalent of:

```sql
DELETE FROM journal_table
WHERE journal_id = ?
  AND generation > <header-sentinel>
  AND generation < <UUIDv7 cutoff>;
```

The cutoff is the minimum UUIDv7 value for the UTC timestamp one day before
cleanup runs. The lower bound excludes the header sentinel. The partition-key
restriction limits the delete to the journal being replaced; it does not scan
other journals. Cassandra represents the range delete with tombstones, so
physical bytes are reclaimed later by compaction.

This is deliberately best-effort eventual cleanup, not a lease or a proof that
every deleted generation is abandoned:

- A process can stop before or during staging, leaving rows behind.
- A newer orphan generation is not included by a range below the cutoff; a
  later replacement may clean it once it is old enough.
- Generations within the one-day grace period are retained.
- A large clock error or an operation which stages for more than a day can
  violate the assumption that generations below the cutoff are inactive.
- A reader which already selected a generation can race its deletion. The read
  may fail its consistency checks; callers decide whether and how to retry.
- A cleanup failure is logged. Cleanup is retried only by a later successful
  replacement; there is no background reaper.

UUIDv7 provides time-sortable IDs, not a distributed clock or a commit-order
service. Do not shorten the grace period or change the range semantics without
reviewing clock skew, maximum replacement duration, active-reader behavior,
and the reserved header key. If stronger guarantees are required, generation
ownership must be coordinated in durable header state and cleanup must fence
writers before deleting their staged rows.

## Compaction and capacity

The storage layer counts successful appends and appended bytes. When either
`CompactionAppendCount` or `CompactionThresholdBytes` is reached, it requests a
snapshot. The journaling state manager observes that request before a later
write and calls `ReplaceAsync`. These thresholds are triggers, not hard caps:
one append can cross a threshold, replacement snapshots are not capped by
`MaxAppendBytes`, and failed-generation debris can remain until a later
replacement.

Each journal's active rows and inactive generations share one partition.
`RowSizeBytes`, append thresholds, journal workload, partition growth, and
tombstone accumulation must be considered together. Do not treat range deletes
as immediate physical erasure or as a substitute for Cassandra repair and
compaction operations.

## Schema and deployment

Provider initialization executes `CREATE TABLE IF NOT EXISTS` and prepares
statements. The Cassandra identity therefore needs table-creation permission
even if an operator pre-provisions the table. Keep schema changes explicit and
versioned: the header includes `schema_version`, and recovery rejects unknown
versions instead of silently interpreting them.

The table name is validated as a simple CQL identifier because it is embedded
in prepared CQL text; values such as journal IDs, generations, and payloads are
bound parameters.

## Tests that protect the design

The Cassandra integration tests use Testcontainers, the `CASSANDRAVERSION`
environment variable, and a single-node keyspace. The important protocol
coverage includes:

- round-trip recovery and Cassandra paging;
- absent-journal reads, create-if-absent metadata, and empty
  append/replacement behavior;
- first append updates the format key for an empty journal after a format
  change;
- append and replacement rejection after another handle creates a journal
  which this handle observed as absent, plus cold-handle adoption of an
  existing journal;
- detection of missing active-generation rows before reporting a complete read;
- generation replacement, restarted sequence numbers, and exact old-generation
  deletion;
- replacement spanning multiple full staging batches plus a partial final
  batch, and failure in the second batch with old-generation recovery intact;
- range cleanup of an old UUIDv7 generation while retaining a recent one;
- successful replacement when a post-publication delete fails;
- replacement whose publication commits but whose client response is lost;
- delete LWT conflict when a concurrent append wins after the delete header read;
- a surfaced active-generation cleanup failure after the delete LWT, with the
  journal remaining absent and leftover rows unreachable;
- LWT metadata ETags and stale-writer conflicts;
- stale replacement publication and staged-row cleanup;
- stale delete rejection after append, replace, delete/recreate, and a prior
  observation that the journal was absent, while allowing metadata-only
  changes;
- cancellation while waiting for a subsequent Cassandra read page;
- delete and recreation with a fresh generation; and
- append-size validation before storage mutation.

When changing the schema or protocol, extend these tests for the failure or
concurrency transition introduced by the change. Run against the Cassandra
versions in the repository's CI matrix, not only an in-memory substitute.
