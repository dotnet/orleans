# Microsoft Orleans S3 Journaling

`Microsoft.Orleans.Journaling.S3` provides an `IJournalStorage` implementation backed by Amazon S3 Express One Zone directory buckets.

The provider uses S3 Express append writes (`WriteOffsetBytes`) for WAL appends. For local development and tests against S3-compatible emulators such as MinIO, set `UseS3ExpressAppend = false`, `UseConditionalDelete = false`, and `StorageClass = null` to use portable conditional writes and deletes.

Buckets should be created ahead of time for AWS S3 Express One Zone. `CreateBucketIfNotExists` is intended for local emulators.

Metadata updates rewrite the current WAL using a conditional single-object upload. Publish a checkpoint to compact the WAL before updating metadata when the replacement object would exceed S3's 5 GB (5,000,000,000 byte) single-upload limit. Checkpoint snapshots use the same upload limit.

## Catalog enumeration

`IJournalStorageCatalog.ListAsync` returns journal identities incrementally in S3 traversal order, including unordered directory-bucket listings. Set `ListOptions.Prefix` to select an exact journal id and its descendants. `MaxId` supplies an inclusive ordinal upper bound; its default value is unlimited. Both options are snapshotted when enumeration begins. The provider filters future ids but cannot stop at the first one: directory buckets are unordered. Consumers needing due order must sort the selected ids using `StringComparer.Ordinal`.

The provider handles `ListObjectsV2` continuations internally, requests up to 1000 objects per page, and yields canonical WAL identities from that page before fetching more objects. With the default identity `GetObjectKey` mapping, the native request uses `<Prefix>/`, including the exact journal's WAL and every descendant, while excluding similarly named sibling segments.

Custom `GetObjectKey` mappings must also configure `GetObjectKeyPrefix` for prefixed catalog listings. Arbitrary object-key mapping functions cannot safely be applied to a journal prefix. The explicit prefix mapper must return a non-empty native prefix ending in `/` (required by directory buckets) which contains the canonical WAL for the exact id and all descendants. Missing or invalid prefix configuration throws before listing; unprefixed listing still works without the mapper.

```csharp
options.GetObjectKey = id => $"journals/{id.Value}";
options.GetObjectKeyPrefix = prefix => $"journals/{prefix.Value}/";
options.TryParseJournalId = key => key.StartsWith("journals/", StringComparison.Ordinal)
    ? new JournalId(key["journals/".Length..]) : null;
```

`TryParseJournalId` and canonical-WAL validation still apply after native prefix selection. Checkpoints, aliases, and unrelated objects within the selected namespace consume space in the native page before filtering.

Client traversal memory is proportional to the current native page. An enumerator advance can cross multiple filtered or empty pages, and the storage service determines scan work, latency, and retries. Enumeration observes the live bucket; concurrent changes follow S3 listing semantics. Use subsequent enumerations to discover later changes and tolerate repeated identities during changes.

Dispose the enumerator when stopping early and use a cancellation token covering its lifetime. Cancellation and service failures propagate through enumeration.
