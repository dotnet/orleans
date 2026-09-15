# Microsoft Orleans S3 Journaling

`Microsoft.Orleans.Journaling.S3` provides an `IJournalStorage` implementation backed by Amazon S3 Express One Zone directory buckets.

The provider uses S3 Express append writes (`WriteOffsetBytes`) for WAL appends. For local development and tests against S3-compatible services such as SeaweedFS, set `UseS3ExpressAppend = false`, `UseConditionalDelete = false`, and `StorageClass = null` to use portable conditional writes and deletes.

The S3 integration tests run SeaweedFS in a container and exercise conditional-rewrite appends, ETag-conditional deletes, metadata updates, and concurrent writes. The CI pre-pull and both test fixtures use the same digest-pinned official image.

Buckets should be created ahead of time for AWS S3 Express One Zone. `CreateBucketIfNotExists` is intended for local emulators.

Metadata updates rewrite the current WAL using a conditional single-object upload. Publish a checkpoint to compact the WAL before updating metadata when the replacement object would exceed S3's 5 GB (5,000,000,000 byte) single-upload limit. Checkpoint snapshots use the same upload limit.

## Object layout

`GetObjectKey` maps a logical journal id to its base object key (the identity mapping by default). WAL and checkpoint objects use separate namespaces:

- WAL: `wal/<base-key>`
- Checkpoint: `checkpoints/<base-key>/<snapshot-id>`

For example, journal `jobs/00001234` uses `wal/jobs/00001234` and checkpoint objects under `checkpoints/jobs/00001234/`. Checkpoint names are stored in the WAL metadata. Catalog requests always stay under `wal/`, so checkpoints do not consume listing pages.

S3 Express directory buckets benefit from slash-delimited prefixes, but this layout does not introduce time partitions or a discovery horizon. Unordered listings scan the selected WAL directory and retain every matching overdue journal, however old. Applications can supply hierarchical base keys, provided their prefix and reverse mappings satisfy the catalog contract.

## Catalog enumeration

`IJournalStorageCatalog.ListAsync` returns `JournalCatalogEntry` values incrementally in S3 traversal order. Each entry's `Id` is the journal identity. S3 entries always have null `Metadata`, including when `ListOptions.IncludeMetadata = true`: `ListObjectsV2` cannot project the complete journal format, ETag, and caller-owned properties together. Enumeration never adds separate per-journal metadata requests. Call `GetMetadataAsync` explicitly when metadata is needed.

`ListOptions.Prefix` is a raw ordinal string prefix, including partial segments; use a trailing `/` to select only entries inside a namespace. `MinId` and `MaxId` provide inclusive ordinal bounds, unlimited by default. All constraints are snapshotted when enumeration begins and checked before yielding. Empty intersections issue no request. Consumers needing due order must sort selected ids using `StringComparer.Ordinal`.

The provider handles `ListObjectsV2` continuations internally and requests up to 1000 objects per page. `UseOrderedListing` defaults to `false`, matching S3 Express directory buckets. Every native prefix begins with `wal/`, including unprefixed catalog requests. Directory mode widens a raw native prefix to its nearest slash-terminated directory boundary, retaining at least `wal/`. Directory buckets are unordered and do not support `StartAfter`: the provider scans that selected namespace and filters both bounds without stopping at the first future id.

Set `UseOrderedListing = true` only for general-purpose buckets or compatible services guaranteeing lexically ordered `ListObjectsV2` results and `StartAfter` support. With the default identity `GetObjectKey` mapping, ordered mode sends `wal/` plus the raw prefix, further narrowed by the common prefix of both bounds when possible. Since `StartAfter` is exclusive, an ASCII lower bound uses a strictly earlier marker: `wal/` plus the lower bound with its final character removed. This marker is sent only when it sorts after the native prefix, and the inclusive minimum is still checked locally. Subsequent requests use the returned opaque `ContinuationToken` alone to resume traversal, including after empty pages. The provider stops after `wal/<MaxId>` for a safe ASCII maximum. Non-ASCII bounds are filtered without unsafe native key seeks or cutoffs. There is no native upper-end parameter, so the crossing page is fetched but later pages are not. `UseS3ExpressAppend` does not imply a listing-order guarantee.

Custom `GetObjectKey` mappings must also configure `GetObjectKeyPrefix` for explicitly prefixed catalog listings. Arbitrary object-key mapping functions cannot safely be applied to a journal prefix. The explicit prefix mapper must return a non-empty **base object-key prefix** covering every matching journal, including partial segments; do not include `wal/`. The provider prepends `wal/`, then directory mode widens the result to a slash boundary. Missing or empty prefix configuration throws before listing; unprefixed listing, including bounds-only queries, still works without the mapper. Custom mappings never use identity-based native lower or upper key bounds, even in ordered mode.

```csharp
options.GetObjectKey = id => $"journals/{id.Value}";
options.GetObjectKeyPrefix = prefix => $"journals/{prefix.Value}";
options.TryParseJournalId = key => key.StartsWith("journals/", StringComparison.Ordinal)
    ? new JournalId(key["journals/".Length..]) : null;
```

`TryParseJournalId` receives the base object key after the catalog strips `wal/`. Canonical-WAL validation still applies after parsing. Checkpoints are outside the selected namespace; aliases and unrelated objects under `wal/` still consume space in the native page before filtering.

| Listing mode | Native prefix | Lower bound | Upper bound |
| --- | --- | --- | --- |
| Ordered, default identity mapping | `wal/` + raw prefix/common range prefix | Strictly earlier ASCII `StartAfter` marker when it narrows the prefix | Stop after crossing ASCII `wal/<MaxId>` |
| Directory/unordered | Nearest slash-terminated directory under `wal/` | Local filtering | Local filtering; entire selected namespace is traversed |
| Custom mapping | `wal/` + explicit mapped base prefix, widened in directory mode | Local filtering | Local filtering; no identity-order assumption |

Client traversal memory is proportional to the current native page. An enumerator advance can cross multiple filtered or empty pages, and the storage service determines scan work, latency, and retries. Enumeration observes the live bucket; concurrent changes follow S3 listing semantics. Use subsequent enumerations to discover later changes and tolerate repeated identities during changes.

Dispose the enumerator when stopping early and use a cancellation token covering its lifetime. Cancellation and service failures propagate through enumeration.

## Metrics

The `Microsoft.Orleans` meter reports both logical journal operations and the S3 SDK calls they cause. The shared metrics use `provider=s3`:

| Metric | Measurement | Other tags |
| --- | --- | --- |
| `orleans-journaling-provider-operations` | Completed logical operations | `operation`, `status` |
| `orleans-journaling-provider-operation-duration` | Logical operation latency in milliseconds | `operation`, `status` |
| `orleans-journaling-provider-operation-bytes` | Successful append/replace payload bytes and bytes delivered to read consumers | `operation`, `status` |
| `orleans-journaling-provider-catalog-entries` | Catalog entries delivered to consumers | None |
| `orleans-journaling-provider-api-calls` | Completed provider-issued SDK calls, including failures | `api`, `status` |
| `orleans-journaling-provider-api-call-duration` | SDK call latency in milliseconds | `api`, `status` |
| `orleans-journaling-provider-api-items` | Objects returned by successful listing pages before local filtering | `api`, `status` |
| `orleans-journaling-provider-retries` | Provider-loop retries | `reason` |

Logical storage operations are `create`, `get_metadata`, `update_metadata`, `append`, `replace`, `read`, and `delete`. The provider also measures `initialize`, `close`, and `list`. Storage instances returned by the provider measure each caller operation once; internal WAL creation and metadata reads contribute SDK calls, not additional logical operations.

Fixed API names are `head_bucket`, `create_bucket`, `head_object`, `get_object`, `put_object`, `delete_object`, and `list_objects_v2`. All provider-issued calls are measured, including part-count HEAD requests, checkpoint cleanup, conditional rewrites, and continued or empty listing pages. A listing records one logical outcome on completion, failure, cancellation, or early disposal. Its latency excludes time the consumer spends between enumerator advances.

SDK outcomes distinguish `ok`, `not_found`, `conflict`, `throttled`, `unavailable`, `timeout`, `canceled`, and `error`. Logical outcomes describe the caller-visible result instead: for example, an unsuccessful conditional create records `already_exists`, a missing metadata lookup records `not_found`, and an unapplied metadata update records `not_applied`. Retried conflicts do not turn a successful logical operation into an error. Retry reasons are the bounded values `metadata_conflict` and `checkpoint_collision`; handled conflicts which do not start another loop attempt do not increment the retry metric.

API counts and latency make request amplification visible for capacity and cost analysis. Each measurement covers one SDK invocation, including any retries performed inside the SDK; it does not count individual wire attempts. GET latency ends when the SDK returns its response, while logical read latency also includes consuming the response streams. Metrics do not calculate AWS charges, and no bucket, object key, journal id, metadata value, or exception message is used as a tag.

The existing `orleans-journaling-s3-operations`, `orleans-journaling-s3-operation-duration`, and `orleans-journaling-s3-operation-bytes` metrics retain their names, tags, and behavior, including for directly constructed storage instances.
