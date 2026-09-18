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
        .UpdateRow_TokensCapturedBeforeHeartbeat_CommitStatusChange(ct),
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
Concurrent initialization callers share the same three initialized handles;
each queued caller can cancel its own wait.
The second cluster ID extends the first with a suffix, exercising isolation of
storage keys whose cluster names share a prefix.
`CreateAdditionalHandleAsync` accepts only those two owned, live cluster scopes.
Handle registration and teardown's ownership snapshot share one lifecycle lock.
Admitted factory calls, provider initialization, terminal deletion, native probes,
and late-owner disposal remain
owned until their actual tasks finish. Caller cancellation ends that caller's
wait; the factory and tokenless-compatible initialization keep their original
owners alive. An acquisition which finishes after disposal or history retirement
disposes its returned owner before reporting the lifetime error. Teardown waits
for these admitted operations before deleting scopes or disposing shared owners.
Initialization of an additional handle is explicit. `RunAsync` initializes,
executes, and tears down while preserving the primary failure. Cleanup deletes
only the fixture's cluster partitions and attempts every owner after the actual
delete operations complete. Cleanup dispatch runs off the caller thread so
synchronously blocking callbacks also respect the caller's 30-second wait bound. A timeout
retains the owners and shared cleanup task until the operations finish; later
`DisposeAsync` calls await that same task. The timeout's exception data contains
`ClusteringTestKit.CleanupCompletion`, and a late cleanup failure is attached as
`ClusteringTestKit.CleanupFailure`. An operation which never completes retains
its owner; the bound applies to the caller's wait, while resource release waits
for operation completion. Deleted
scopes are retired; teardown disposes their owners directly. A deletion request
which fails after invocation also retires its handles, since it may have
committed. Subsequent histories use a new fixture with fresh owners.
Ordinary membership operations receive their scenario cancellation token. The
fixture's ownership tracking covers its lifecycle callbacks, rather than hidden
work behind a provider's canceled compatibility adapter. Handle disposers release
their declared resources according to the real SDK close, abort, or drain
contract; any resulting infrastructure failures remain observable.
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
| G06 | `UpdateIAmAlive_OwnerWrites_PreserveCanonicalFieldsAndTableVersion` |
| G07 | `UpdateRow_TokensCapturedBeforeHeartbeat_CommitStatusChange` |
| G08 | `UpdateRow_TokensCapturedBeforeHeartbeat_CommitVoteChange` |
| G09 | `Handles_IndependentlyConstructed_ShareCommittedBackingState` |
| G10 | `InsertRow_StaleTableToken_ReturnsFalseWithoutSideEffects` |
| G11 | `UpdateRow_StaleTableTokenWithCurrentRowToken_ReturnsFalseWithoutSideEffects` |
| G12 | `UpdateRow_StaleSnapshotAfterSameRowCommit_ReturnsFalseWithoutSideEffects` |
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
integer/table ETag. Provider observations compare canonical fields and the
table version/ETag independently of raw IAmAliveTime and provider-defined row
ETags. Retained-object detachment also compares the captured object's liveness
and row metadata. At equal versions the history requires every non-Dead
row and every retained canonical field, while allowing previously Dead rows to
be pruned. IAmAliveTime is independently and inconsistently updated; row ETags
are opaque provider metadata and can change with a heartbeat or physical rewrite.
Null/empty suspect lists and suspect/row enumeration order are
canonicalized, as are null/empty representations of an absent optional role name.
Optional Azure deployment metadata is explicitly initialized to
empty role/zero zones, but all these fields remain in every comparison.

Every successful protocol write supplies the real current `TableVersion.Next()`
and must advance exactly once. ETags are opaque. The membership protocol supplies
valid next-version integers and forward status transitions.
Canonical updates atomically validate the table ETag and replace an existing
row. A provider can additionally validate a canonical row token, or ignore the
supplied row ETag when table-version CAS protects the update. Any additional
row guard remains satisfied across heartbeat-only activity. Stale-table tests
use freshly read row metadata after a competing cross-row commit and original
snapshot inputs after a same-row commit. Every unchanged canonical entry field
is preserved. Full-row writes can overwrite a heartbeat with an earlier value.
Different reads can skip committed versions. A newer full view can omit a
previously live identity, establishing its terminal death even when the observer
missed its explicit Dead transition. The history retains terminal identities
across compaction.

Dead-only cleanup can preserve the version and table ETag or use atomic +1
commits with fresh table ETags. With several eligible rows, the suite accepts
batches and bounds the total version increase between zero and the number of
removed rows. Every retained canonical field remains unchanged.
Empty cleanup preserves the canonical observation and table version/ETag.
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
canonical-state preservation after rejection; a backend-wide provider can instead delete
the requested foreign scope. If the foreign scope is retained, G27 checks its
canonical view and then exercises its own-scope deletion. Native probes establish
which scopes remain readable; retired handles receive only owner disposal.

Each owning silo publishes its own liveness using a single blind column write.
Heartbeat inputs carry the identity and timestamp. The kit publishes fixed
whole-second UTC t1, t2, t2 in sequence through one owner while its row is live.
Each call preserves canonical membership fields and the table version/ETag.
Returned row metadata can vary. Reads can lag or expose older liveness values.
Generated heartbeat operations
use a fixed owner handle per identity and stay within that row's live lifetime.

Provider-native tests verify the storage-operation budget for each periodic
heartbeat: **zero prerequisite reads, zero compare-and-swap operations, and one
blind liveness write**. The kit's observation reads bracket the provider call to
check resulting fields; native SDK instrumentation establishes the operation
count. Runtime snapshot merging retains maximum observed liveness.
Provider-native tests also verify that an original-input canonical update after
heartbeat-only activity takes one mutation attempt, with no heartbeat-driven
reread or retry hidden behind the provider API. The generic scenarios establish
the outcome at that API boundary; SDK instrumentation establishes the attempt count.

G07 and G08 capture the row ETag and table version, perform an owner heartbeat,
then commit a status or suspect-vote change using the original inputs. Each update
succeeds once with an exact +1 version advance. These cases make heartbeat
noninterference observable at the canonical write boundary. Full-row payloads
may clobber a newer stored heartbeat; the model's owner-published timestamps
describe generated inputs, independently of the raw liveness values returned.

Successful forward updates refresh an existing suspect's timestamp and clear
a populated suspect list using either an empty list or null.
Missing-row checks include delayed updates after Dead-row
compaction, checking both stale tokens and fresh table candidates. Periodic
heartbeats finish within the owner's live-row lifetime; native missing-row
errors and infrastructure exceptions propagate.

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
Setup uses one initial point read, one insert and verifying point read per row,
then two full views checked against independently constructed canonical entries.
Each insertion verifies an exact +1 commit, a fresh table token, and expected
canonical row fields. For N rows this is
N+1 point reads, N inserts, two full reads, and 3N membership-row observations.
These count provider API calls; provider-specific instrumentation measures their
native request costs.

Accordant generates and executes operation sequences using transition coverage.
Required constrained prefixes reach stale table snapshots after cross-row and same-row commits,
original-token updates after heartbeats, and single- and multi-row Dead compaction followed
by successor creation. Every generated case acquires fresh scopes;
the execution context retains real token history separately from the abstract
model. `DeleteCluster` is terminal: its result carries verified deletion evidence
and ends all operations for that history. The next case constructs a new fixture
and its owners. Generated cleanup uses a cutoff later than every generated
timestamp, making the expected Dead-row removals independent of heartbeat lag
or overwrite. Cleanup outcomes are range-checked and their surviving canonical rows
validated before the model records a version delta. Final execution checks
require all eighteen operation kinds. Self-tests exercise both cleanup
strategies with independent oracles for separate canonical row checks,
table-version-derived row tokens, and physical row metadata with table-only CAS.
They also cover alternating liveness lag, full-row heartbeat overwrite, cheap
heartbeat preservation, and deliberate canonical/table-token violations.
Any fixture teardown failure stops the generated run before another case or
replay can acquire owners. The original case failure and teardown diagnostics
remain available together.
