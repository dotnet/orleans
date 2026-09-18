# Orleans clustering TestKit

`Microsoft.Orleans.Clustering.TestKit` supplies reusable membership provider
conformance tests for any test framework. It builds on Orleans Core and
Microsoft.Accordant. Contract violations throw `ClusteringConformanceException`.

## Factories and ownership

Use an isolated fixture **per direct scenario**. Each factory invocation must
construct a new `IMembershipTable`, using the supplied cluster ID and the same
backend/service configuration. Independent provider instances exercise
cross-instance coordination. Allocate dedicated test membership partitions:
the fixture owns and deletes their data during teardown.

```csharp
MembershipTableTestFixture CreateFixture() => new(
    "MyProvider",
    (serviceId, clusterId, cancellationToken) =>
    {
        var owner = CreateProviderResources(serviceId, clusterId);
        return ValueTask.FromResult(
            new MembershipTableTestHandle(owner.Table, owner.DisposeAsync));
    },
    isDeletedAsync: IsMembershipDataDeletedAsync,
    serviceId: "my-provider-conformance");

await CreateFixture().RunAsync(
    (fixture, ct) => new MembershipTableTestRunner(fixture, seed: 17)
        .UpdateRow_FreshTokensAndOldHeartbeat_PreservesMaximum(ct),
    cancellationToken);

await new MembershipTableModelBasedTestRunner(
    CreateFixture,
    new MembershipTableModelBasedConformanceOptions
    {
        ProviderName = "MyProvider",
        Seed = 17,
        MaxDepth = 3,
        MaxSequenceLength = 3
    }).RunGeneratedConformanceTests(cancellationToken);
```

Implement `CreateProviderResources` using the provider's configuration and
resource ownership rules. The optional handle disposer owns those resources
explicitly. A simpler synchronous
`new MembershipTableTestFixture("MyProvider", clusterId => CreateTable(clusterId), IsMembershipDataDeletedAsync)`
works when no per-handle resource disposal is needed. An asynchronous
`(clusterId, cancellationToken) => ValueTask<MembershipTableTestHandle>` overload
is also available.

Every constructor requires an `isDeletedAsync` delegate with signature
`Func<string, CancellationToken, ValueTask<bool>>`. Implement it as a read-only
probe of the supplied cluster ID's **original backing scope**. Return `false`
while seeded membership data remains, and `true` when the data is deleted or
the native owner has terminally invalidated its store. Persistent providers can
query the same backend through independent access; a terminal provider can
inspect its owner's native invalidation state. Keep that owner and scope alive
until verification finishes. Propagate probe and backend failures.

The suite first requires `false` for populated data, invokes the provider's
real deletion method, then requires `true` for own-scope deletion, all before
disposing any handles or owners. Probe access preserves the original state:
read existing storage or native invalidation directly, rather than initializing
tables, replacing a backend, or resetting an owner. This ordering exposes no-op
deletion even when owner disposal would destroy the backing data.

`InitializeAsync` creates A1/A2 in `ClusterId` and B1 in `OtherClusterId`;
`First`, `Second`, and `OtherCluster` expose those distinct providers.
The second cluster ID extends the first with a suffix, exercising isolation of
storage keys whose cluster names share a prefix.
`CreateAdditionalHandleAsync` accepts only those two owned, live cluster scopes.
Initialization of an additional handle is explicit. `RunAsync` initializes,
executes, and tears down while preserving the primary failure. Cleanup uses
independent 30-second bounds per deletion/disposer, deletes only the fixture's
cluster partitions, and attempts every owner even after a failure. Deleted
scopes are retired; teardown disposes their owners directly. A deletion request
which fails after invocation also retires its handles, since it may have
committed. Subsequent histories use a new fixture with fresh owners.
Use non-secret provider labels: diagnostics include labels, cluster IDs, opaque
tokens, identities, and mismatched persisted fields.

## Independently discoverable direct guarantees

Expose each runner method as a separate fact/test in your framework. All return
`Task` and take `CancellationToken cancellationToken = default`. Every provider
runs the same behavioral assertions.

| ID | Public method |
|---|---|
| G01 | `InsertRow_CurrentTableVersion_CommitsExactlyOneVersion` |
| G02 | `UpdateRow_CurrentTokens_CommitsExactlyOneVersion` |
| G03 | `Reads_SameVersion_PreservesRetainedCanonicalFields` |
| G04 | `Reads_MaySkipCommittedVersions_WithoutSkippingHistoryValidation` |
| G05 | `Lifecycle_DeadRemainsTerminalAfterCompaction_SuccessorUsesNewGeneration` |
| G06 | `UpdateIAmAlive_NewerThenOlderAndRepeated_PreservesMaximum` |
| G07 | `UpdateRow_FreshTokensAndOldHeartbeat_PreservesMaximum` |
| G08 | `UpdateRow_HeartbeatOnlyRowEtagConflict_AllowsOneDocumentedRereadRetry` |
| G09 | `Handles_IndependentlyConstructed_ShareCommittedBackingState` |
| G10 | `InsertRow_StaleTableToken_ReturnsFalseWithoutSideEffects` |
| G11 | `UpdateRow_StaleTableTokenWithCurrentRowToken_ReturnsFalseWithoutSideEffects` |
| G12 | `UpdateRow_StaleRowTokenWithFreshTableToken_ReturnsFalseWithoutSideEffects` |
| G13 | `InsertRow_DuplicateIdentity_ReturnsFalseWithoutSideEffects` |
| G14 | `UpdateRow_MissingIdentityWithRealToken_ReturnsFalseWithoutSideEffects` |
| G15 | `ReadRow_AndReadAll_AgreeForPresentAndAbsentIdentities` |
| G16 | `Reads_RetainedObjectsRemainUnchangedAfterLaterWrites` |
| G17 | `InsertRow_MutatingInputAndSuspectList_DoesNotMutateStoredState` |
| G18 | `UpdateRow_MutatingInputAndSuspectList_DoesNotMutateStoredState` |
| G19 | `Reads_MutatingReturnedEntryAndSuspectList_DoesNotMutateStoredState` |
| G20 | `ConcurrentCrossRowUpdates_SharedTableVersion_HaveExactlyOneWinner` |
| G21 | `ConcurrentReadAll_ReturnsOnlyAtomicCommittedViews` |
| G22 | `ConcurrentReadRow_ReturnsOnlyAtomicCommittedViews` |
| G23 | `InitializeMembershipTable_RepeatedWithData_PreservesCommittedState` |
| G24 | `Clusters_SharedBackendWithOverlappingSiloAddresses_AreIsolated` |
| G25 | `CleanupDefunctSiloEntries_RemovesOnlyStrictlyOldDeadRows` |
| G26 | `DeleteMembershipTableEntries_DeletesOwnClusterAndPreservesOtherCluster` |
| G27 | `DeleteMembershipTableEntries_DifferentClusterId_NeverDeletesConfiguredCluster` |

The generated fact is conventionally named
`MembershipTable_ModelBased_GeneratedConformance`; it calls
`MembershipTableModelBasedTestRunner.RunGeneratedConformanceTests`.

## Comparison and protocol rules

The immutable observation captures all public persisted entry fields, nested
suspect identities/times, full endpoint/generation identity, row ETags, and
integer/table ETag. Rejections, aliasing, initialization, and isolation compare
the complete observation. At equal versions the history requires every non-Dead
row and every retained canonical field, while allowing previously Dead rows to
be pruned. IAmAliveTime advances independently and row ETags are opaque.
Null/empty suspect lists and suspect/row enumeration order are
canonicalized, as are null/empty representations of an absent optional role name.
Optional Azure deployment metadata is explicitly initialized to
empty role/zero zones, but all these fields remain in every comparison.

Every successful protocol write supplies the real current `TableVersion.Next()`
and must advance exactly once. ETags are opaque. The membership protocol supplies
valid next-version integers and forward status transitions.
Successful commits can refresh row ETags for unchanged entries, including
providers which derive row tokens from the table version. Stale-table tests use a
freshly read row token after the competing commit; stale-row tests pair a saved
row token with a fresh table candidate. Every unchanged entry field and heartbeat
is preserved.
Different reads can skip committed versions. A newer full view can omit a
previously live identity, establishing its terminal death even when the observer
missed its explicit Dead transition. The history retains terminal identities
and maximum observed heartbeats across compaction.

Dead-only cleanup can preserve the version and table ETag or use atomic +1
commits with fresh table ETags. With several eligible rows, the suite accepts
batches and bounds the total version increase between zero and the number of
removed rows. Every retained canonical field and heartbeat remains unchanged.
Empty cleanup preserves the complete observation.
The cleanup scenario retains a Dead row exactly at the cutoff, repeats that
cutoff as a no-op, then advances it by one tick and requires that row's deletion.
Restarts use a strictly newer generation at the same endpoint. The generated
model chooses legal forward lifecycle operations.

Own-cluster deletion removes that cluster's data and preserves other clusters.
Deletion ends the stored history. G26 verifies native deletion and the other
cluster's complete view. G27 checks unused and foreign cluster IDs, verifying
the configured cluster's complete view throughout.
For a foreign cluster ID, a scoped provider can leave that scope unchanged or
reject it with `ArgumentException` naming `clusterId`. The suite verifies complete
state preservation after rejection; a backend-wide provider can instead delete
the requested foreign scope. If the foreign scope is retained, G27 checks its
complete view and then exercises its own-scope deletion. Native probes establish
which scopes remain readable; retired handles receive only owner disposal.

Heartbeat tests use fixed whole-second UTC t0 < t1 < t2. Newer-then-older and
repeated heartbeat writes, and fresh-token status writes carrying t0, must
retain exactly t2. G08 permits **one** retry only if the controlled t2 heartbeat
changed the target row ETag, with unchanged table integer/token and complete
versioned fields. A false write must have no other side effects. Refresh only
the expected row ETag; retain the original table candidate and old-heartbeat
payload. Unchanged row ETag, stale table, infrastructure exceptions, unrelated
false, or changed view never permit retry. Both heartbeat row-ETag strategies
are exercised by the independent local oracle.

Successful forward updates also advance a supplied newer heartbeat, refresh an
existing suspect's timestamp, and clear a populated suspect list using either an
empty list or null. The stale-heartbeat trace progresses through Dead and verifies
cleanup retains the tombstone until its effective update time passes the cutoff.
Missing-row checks include delayed updates after Dead-row
compaction, checking both stale tokens and fresh table candidates. A delayed heartbeat to
the compacted identity preserves the complete remaining table and its version.
Storage exceptions propagate to the caller.

Concurrency uses materialized ready/start/completion gates, exact winner counts,
and immediate per-observation checks against known before/after histories.
ReadAll and ReadRow scenarios race readers against both forward updates and
Dead-row cleanup, including the transition from a present point row to absence.
The cleanup scenario also races two cleaners over one eligible Dead row and
accepts either an unchanged version or one atomic increment.
`concurrencyRowCount` (default 128,
range 3–10000) bounds the multi-row workload without changing any assertion.
An adapter must select a safe count which crosses its backend's actual paging
or streaming boundary. Retain provider-specific pagination tests which force
those boundaries explicitly.

Accordant generates and executes operation sequences using transition coverage.
Required constrained prefixes reach independent stale row/table modes,
old-heartbeat status writes, and single- and multi-row Dead compaction followed
by successor creation. Every generated case acquires fresh scopes;
the execution context retains real token history separately from the abstract
model. `DeleteCluster` is terminal: its result carries verified deletion evidence
and ends all operations for that history. The next case constructs a new fixture
and its owners. Cleanup outcomes are range-checked and their complete surviving rows
validated before the model records a version delta. Final execution checks
require all eighteen operation kinds. Self-tests exercise both cleanup
strategies with an independent oracle and deliberate contract-violating mutants.
