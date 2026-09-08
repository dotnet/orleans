# Microsoft Orleans S3 Journaling

`Microsoft.Orleans.Journaling.S3` provides an `IJournalStorage` implementation backed by Amazon S3 Express One Zone directory buckets.

The provider uses S3 Express append writes (`WriteOffsetBytes`) for WAL appends. For local development and tests against S3-compatible emulators such as MinIO, set `UseS3ExpressAppend = false`, `UseConditionalDelete = false`, and `StorageClass = null` to use portable conditional writes and deletes.

Buckets should be created ahead of time for AWS S3 Express One Zone. `CreateBucketIfNotExists` is intended for local emulators.

Metadata updates rewrite the current WAL using a conditional single-object upload. Publish a checkpoint to compact the WAL before updating metadata when the replacement object would exceed S3's 5 GB (5,000,000,000 byte) single-upload limit. Checkpoint snapshots use the same upload limit.

## Catalog paging

The registered `IJournalStorageCatalog` also implements `IPagedJournalStorageCatalog`. Each page makes one `ListObjectsV2` call with `MaxKeys = min(pageSize, 1000)` and filters the returned objects to canonical WAL identities matching the requested journal prefix. The bucket traversal supports `GetObjectKey` and `TryParseJournalId` mappings; checkpoints, aliases, and unrelated objects consume space in the native page before filtering.

An empty result can have a continuation token. Pass that opaque token with the same journal prefix and initialized provider instance until the returned token is null. Page allocation is proportional to the native page; the storage service determines scan work, latency, and retries. Pages follow S3 traversal order, including unordered directory-bucket listings, while `ListAsync` retains ordinal journal id ordering.

Each page observes the live bucket. Concurrent changes follow S3 listing semantics; use subsequent traversals to discover later changes and tolerate repeated identities.
