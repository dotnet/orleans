# Microsoft Orleans S3 Journaling

`Microsoft.Orleans.Journaling.S3` provides an `IJournalStorage` implementation backed by Amazon S3 Express One Zone directory buckets.

The provider uses S3 Express append writes (`WriteOffsetBytes`) for WAL appends. For local development and tests against S3-compatible emulators such as MinIO, set `UseS3ExpressAppend = false`, `UseConditionalDelete = false`, and `StorageClass = null` to use portable conditional writes and deletes.

Buckets should be created ahead of time for AWS S3 Express One Zone. `CreateBucketIfNotExists` is intended for local emulators.

Metadata updates rewrite the current WAL using a conditional single-object upload. Publish a checkpoint to compact the WAL before updating metadata when the replacement object would exceed S3's 5 GB (5,000,000,000 byte) single-upload limit. Checkpoint snapshots use the same upload limit.

## Catalog enumeration

`IJournalStorageCatalog.ListAsync` returns journal identities incrementally in S3 traversal order. `ListOptions.Prefix` is a raw ordinal string prefix, including partial segments; use a trailing `/` to select only entries inside a namespace. `MinId` and `MaxId` provide inclusive ordinal bounds, unlimited by default. All constraints are snapshotted when enumeration begins and checked before yielding. Empty intersections issue no request. Consumers needing due order must sort selected ids using `StringComparer.Ordinal`.

The provider handles `ListObjectsV2` continuations internally and requests up to 1000 objects per page. `UseOrderedListing` defaults to `false`, matching S3 Express directory buckets. Directory mode widens a raw native prefix to its nearest slash-terminated directory boundary, or lists the bucket when no such boundary exists. Directory buckets are unordered and do not support `StartAfter`: the provider scans that selected namespace and filters both bounds without stopping at the first future id.

Set `UseOrderedListing = true` only for general-purpose buckets or compatible services guaranteeing lexically ordered `ListObjectsV2` results and `StartAfter` support. With the default identity `GetObjectKey` mapping, ordered mode sends the raw prefix, further narrowed by the common prefix of both bounds when possible. An ASCII lower bound becomes `StartAfter` using the journal base name rather than its WAL key, preserving the inclusive minimum. The provider stops after a safe ASCII WAL-name upper bound, including any shorter qualifying ids whose WAL sorts later. Non-ASCII bounds are filtered without unsafe native key seeks or cutoffs. There is no native upper-end parameter, so the crossing page is fetched but later pages are not. `UseS3ExpressAppend` does not imply a listing-order guarantee.

Custom `GetObjectKey` mappings must also configure `GetObjectKeyPrefix` for explicitly prefixed catalog listings. Arbitrary object-key mapping functions cannot safely be applied to a journal prefix. The explicit prefix mapper must return a non-empty raw native prefix covering every matching journal, including partial segments. Directory mode then widens it to a slash boundary. Missing or empty prefix configuration throws before listing; unprefixed listing, including bounds-only queries, still works without the mapper. Custom mappings never use identity-based native lower or upper key bounds, even in ordered mode.

```csharp
options.GetObjectKey = id => $"journals/{id.Value}";
options.GetObjectKeyPrefix = prefix => $"journals/{prefix.Value}";
options.TryParseJournalId = key => key.StartsWith("journals/", StringComparison.Ordinal)
    ? new JournalId(key["journals/".Length..]) : null;
```

`TryParseJournalId` and canonical-WAL validation still apply after native prefix selection. Checkpoints, aliases, and unrelated objects within the selected namespace consume space in the native page before filtering.

| Listing mode | Native prefix | Lower bound | Upper bound |
| --- | --- | --- | --- |
| Ordered, default identity mapping | Raw prefix/common range prefix | ASCII `StartAfter` seek | Stop after crossing safe ASCII WAL bound |
| Directory/unordered | Nearest slash-terminated directory, or whole bucket | Local filtering | Local filtering; entire selected namespace is traversed |
| Custom mapping | Explicit mapped prefix, widened in directory mode | Local filtering | Local filtering; no identity-order assumption |

Client traversal memory is proportional to the current native page. An enumerator advance can cross multiple filtered or empty pages, and the storage service determines scan work, latency, and retries. Enumeration observes the live bucket; concurrent changes follow S3 listing semantics. Use subsequent enumerations to discover later changes and tolerate repeated identities during changes.

Dispose the enumerator when stopping early and use a cancellation token covering its lifetime. Cancellation and service failures propagate through enumeration.
