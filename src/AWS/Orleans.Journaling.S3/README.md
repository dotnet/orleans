# Microsoft Orleans S3 Journaling

`Microsoft.Orleans.Journaling.S3` provides an `IJournalStorage` implementation backed by Amazon S3 Express One Zone directory buckets.

The provider uses S3 Express append writes (`WriteOffsetBytes`) for WAL appends. For local development and tests against S3-compatible emulators such as MinIO, set `UseS3ExpressAppend = false` to use a conditional read-modify-write append emulation.

Buckets should be created ahead of time for AWS S3 Express One Zone. `CreateBucketIfNotExists` is intended for local emulators.

