# Microsoft Orleans S3 Journaling

`Microsoft.Orleans.Journaling.S3` provides an `IJournalStorage` implementation backed by Amazon S3 Express One Zone directory buckets.

The provider uses S3 Express append writes (`WriteOffsetBytes`) for WAL appends. For local development and tests against S3-compatible services such as SeaweedFS, set `UseS3ExpressAppend = false`, `UseConditionalDelete = false`, and `StorageClass = null` to use portable conditional writes and deletes.

The S3 integration tests run SeaweedFS in a container and exercise conditional-rewrite appends, ETag-conditional deletes, metadata updates, and concurrent writes. The CI pre-pull and both test fixtures use the same digest-pinned official image.

Buckets should be created ahead of time for AWS S3 Express One Zone. `CreateBucketIfNotExists` is intended for local emulators.

Metadata updates rewrite the current WAL using a conditional single-object upload. Publish a checkpoint to compact the WAL before updating metadata when the replacement object would exceed S3's 5 GB (5,000,000,000 byte) single-upload limit. Checkpoint snapshots use the same upload limit.

## Catalog enumeration

`IJournalStorageCatalog.ListAsync` returns journal identities incrementally in S3 traversal order, including unordered directory-bucket listings. Set `ListOptions.Prefix` to select an exact journal id and its descendants.

The provider handles `ListObjectsV2` continuations internally, requests up to 1000 objects per page, and yields canonical WAL identities from that page before fetching more objects. The bucket traversal supports `GetObjectKey` and `TryParseJournalId` mappings; checkpoints, aliases, and unrelated objects consume space in the native page before filtering.

Client traversal memory is proportional to the current native page. An enumerator advance can cross multiple filtered or empty pages, and the storage service determines scan work, latency, and retries. Enumeration observes the live bucket; concurrent changes follow S3 listing semantics. Use subsequent enumerations to discover later changes and tolerate repeated identities during changes.

Dispose the enumerator when stopping early and use a cancellation token covering its lifetime. Cancellation and service failures propagate through enumeration.
