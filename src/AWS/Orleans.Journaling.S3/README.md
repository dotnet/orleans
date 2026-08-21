# Microsoft Orleans S3 Journaling

`Microsoft.Orleans.Journaling.S3` provides an `IJournalStorage` implementation backed by Amazon S3 Express One Zone directory buckets.

The provider uses S3 Express append writes (`WriteOffsetBytes`) for WAL appends. For local development and tests against S3-compatible emulators such as MinIO, set `UseS3ExpressAppend = false`, `UseConditionalDelete = false`, and `StorageClass = null` to use portable conditional writes and deletes.

Buckets should be created ahead of time for AWS S3 Express One Zone. `CreateBucketIfNotExists` is intended for local emulators.

Metadata updates rewrite the current WAL using a conditional single-object upload. Publish a checkpoint to compact the WAL before updating metadata when the replacement object would exceed S3's 5 GB (5,000,000,000 byte) single-upload limit. Checkpoint snapshots use the same upload limit.
