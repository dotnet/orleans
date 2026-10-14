# Microsoft Orleans Journaling Cassandra Provider

This package stores Orleans journals in Cassandra. It uses an immutable row sequence per journal generation and lightweight transactions (LWTs) to publish appends and generation replacements. Generation IDs are UUIDv7 values, which Cassandra orders by creation timestamp; IDs created in the same millisecond are ordered by their random bits. Entry sequence numbers restart at zero for each generation.

Maintainers can read [Implementation.md](Implementation.md) for the data model, consistency protocol, Cassandra query rationale, and cleanup behavior.

Configure the provider on a silo:

```csharp
 siloBuilder.AddCassandraJournalStorage(options =>
 {
     options.ConfigureClient("Contact Points=localhost;Port=9042", keyspace: "orleans");
 });
```

The provider issues `CREATE TABLE IF NOT EXISTS` when the silo starts, so its Cassandra identity needs table-creation permission even when the table is pre-provisioned. Appends are limited by `MaxAppendBytes`; replacement snapshots are written in rows no larger than `RowSizeBytes`. Reads use `ReadPageSize` to page through the active generation and await subsequent pages asynchronously, so cancellation does not block on the driver's synchronous page enumerator.

Delete uses optimistic content fencing: a handle which observed older journal content, or observed that the journal was absent, cannot delete a later content version. Metadata-only updates do not block deletion.

Each journal is one Cassandra partition so its header and entry rows can participate in same-partition conditional batches. The append-count and appended-byte compaction thresholds request a replacement snapshot before a later write; they do not impose a hard limit on snapshot size or total partition size. Choose thresholds and row sizes for the expected workload, and account for Cassandra tombstone compaction and repair behavior in the table's operational policy.

Before publication, a failed replacement attempts to delete its staged generation. After publication, the provider attempts to delete the previous generation and then issues a range delete for generations older than one day, even if the exact delete fails. That range delete excludes the reserved header generation and is scoped to the journal's partition; it can remove older abandoned generations without listing their rows. Newer generations and generations within the one-day safety window remain until a later replacement. There is no background sweep or lease-based recovery, so a process failure can leave inactive generations behind.

Cleanup runs after the replacement is published. Cleanup failures are logged and do not make `ReplaceAsync` fail after the new generation is active; a later replacement can retry the cleanup. Cassandra deletes create tombstones, and physical removal occurs later during compaction.
