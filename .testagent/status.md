# Test Quality Status

## Scope

The shared directory-entry message target cache is covered by:

- `test/Orleans.Runtime.Tests/Directories/GrainDirectoryCacheFactoryTests.cs`
- `test/Orleans.Runtime.Tests/SharedEntryMessageTargetFastPathTests.cs`

## Results

- 28 focused tests pass on `net10.0`.
- No generated test is assertion-free or relies only on presence/truthiness checks.
- Assertions cover equality, identity, type, null, negative behavior, collection state, lifecycle state, and side effects.
- Cache lifetime tests verify replacement, conditional removal, clear, TTL expiration, eviction, tombstoning, target release, one-shot binding, and identity-checked clearing.
- Integration tests verify local target binding, invalidation and recapture, compatible-interface casts, and explicit remote, external-client, and stateless-worker exclusion.

## Pseudo-Mutation Review

Three high-risk mutations were injected individually and empirically killed by the focused tests:

1. Breaking the atomic first-target bind caused six cache lifetime tests to fail.
2. Removing the directory-backed placement guard caused `StatelessWorkerCalls_DoNotAttachDirectoryEntries` to fail.
3. Allowing messages with cache invalidation headers onto the fast path caused `CacheInvalidationHeader_BypassesFastPathWithoutDiscardingLiveHandle` to fail.

Every mutation was reverted immediately. The focused suites were rerun green afterward.

## Remaining Risk

The tests intentionally allow the same stale local-dispatch race as the existing activation-directory lookup path: invalidation can race after a valid target is read. Activation state and address validation preserve the existing recovery semantics. Remote connections and response routing remain on the established path.
