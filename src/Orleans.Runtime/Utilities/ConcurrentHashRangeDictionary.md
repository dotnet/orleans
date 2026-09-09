# Concurrent hash-range dictionary

`ConcurrentHashRangeDictionary<TKey, TValue, TComparer>` is an Orleans-owned adaptation of
`ConcurrentDictionary<TKey, TValue>` with a range-aware primary hash table.
Point operations, prehashed lookup, hash-only enumeration, and range operations
use the same nodes and bucket array.

## Provenance and maintenance

The starting implementation is
[`ConcurrentDictionary.cs` at dotnet/runtime revision b5881242dc356bd8c464026387545f2684079d30](https://github.com/dotnet/runtime/blob/b5881242dc356bd8c464026387545f2684079d30/src/libraries/System.Private.CoreLib/src/System/Collections/Concurrent/ConcurrentDictionary.cs).
The source file retains the .NET Foundation MIT license header.

The adaptation retains the runtime's linked nodes, volatile bucket publication,
striped writer locks, table-generation retry after taking a writer lock,
copy-and-publish resizing, increasing lock acquisition order, and replacement
nodes for values requiring multiword writes. It implements the internal
operations used by Orleans and the accompanying benchmarks.

Intentional layout changes:

- `IConsistentHashComparer<TKey>` supplies equality and unsigned 32-bit hashes,
  following the structure of `IEqualityComparer<TKey>`. A concrete `TComparer`
  allows the JIT to specialize calls for struct comparers.
- The readonly `GrainIdUniformHashComparer` struct supplies
  `GrainId.GetUniformHashCode()` for cluster-wide grain coordinates. That hash
  combines values already cached in the grain type and key.
- Power-of-two bucket arrays use the hash's high bits. Doubling the table splits
  each hash prefix into two narrower prefixes.
- Initial sizing targets at most 75% occupancy before rounding to a power of two.
  This accommodates per-lock variation while filling a presized collection.
- Lock arrays also have power-of-two lengths. Bucket indices select striped
  locks using a mask.
- Nodes store the key, value, and next link. Resizing recomputes hashes; range
  traversal hashes keys only in boundary buckets. Fully covered buckets are
  traversed directly.
- Struct enumerators support allocation-free direct `foreach` and visitation.
- The maximum bucket count is `1 << 30`, preserving power-of-two addressing.

When updating the runtime baseline, compare the upstream insertion, update,
removal, resize, enumeration, and atomic-write implementations against this
file. Port relevant correctness and memory-ordering fixes explicitly. Review
every bucket calculation against stable-hash addressing and rerun boundary,
collision, generation-race, multiword-value, and recovery coverage on supported
runtime targets. Update this revision after completing that review. Performance
changes should retain side-by-side baselines.

## Hash and range contracts

The consistent comparer contract gives equal keys equal hash codes. Keys retain
their hash and equality identity while stored. The comparer supports concurrent
calls; equality comparisons run under writer locks during mutations. A prehashed
lookup's coordinate is `Comparer.GetHashCode(key)`. Comparers supply inexpensive,
deterministic hashes, stable across processes when coordinates are shared by silos.

Point lookup selects a bucket using the hash and compares keys. Hash-only enumeration
returns all entries at the coordinate, including distinct colliding keys.
Range enumeration implements the existing exclusive-start, inclusive-end ring
semantics, including explicit empty/full ranges and wrap-around. The first and
last buckets are calculated directly. When a wrapped interval intersects the
same bucket at both ends, that bucket is visited once.

Range work is proportional to intersecting buckets and their entries. For a
uniform hash distribution, growth keeps boundary buckets small. Concentrated
prefixes or identical hashes increase chain traversal, boundary filtering, and
writer contention. Like the runtime implementation, the growth budget expands
instead of repeatedly enlarging an underutilized table.

## Concurrency and extraction

Point reads traverse volatile node links without writer locks. Point mutations
take one lock and verify that their captured table is still current before
changing it. Resizing first takes lock zero, allocates the new table, then takes
the remaining locks in increasing order and copies nodes. The new table is
published after copying; old nodes stay available to readers. Rehashing or
allocation failures preserve the original table, including the insertion which
triggered growth. New lock arrays
retain the old lock objects. A writer waiting on an old table retries against
the published generation.

An enumerator captures one table on its first `MoveNext`. It can observe
concurrent mutations to that table and continues safely across resizing.
Quiescing writers while materializing an enumeration gives a point-in-time
snapshot. Synchronous visitors execute outside the collection's locks and may
perform point operations; visitor exceptions propagate.

`RemoveRange` materializes candidates and reserves result capacity before the
first removal. It then performs expected-value removal for each candidate and
appends each successfully removed entry to the caller-owned result list.
Each removal is atomic. Quiescing writers in the range gives complete
extraction; with concurrent writers, changed values survive and the result
records successful removals. Equal replacement values follow the ordinary
key/value removal contract. If hashing or equality throws during removal,
completed removals remain in the supplied list.

The activation directory uses selective enumeration for recovery. Its existing
membership watermark, registration-publication protocol, expected-context
removal, and activation count tracking govern lifecycle consistency.
