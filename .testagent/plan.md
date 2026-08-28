# Test Implementation Plan

> Final scope update: handler caching is narrowed to local `IGrainContext` targets. Remote references cache only the shared directory address and continue through the normal connection path; connection and response-route cases below remain deferred.

## Overview

This is a targeted, three-phase plan for the forthcoming shared directory-entry message-target cache. Existing coverage is substantial for locator selection and partial for cache lifetime, placement, local activation, and networking, while the shared route-handle API is currently absent.

All test source additions are gated on the production API landing. Once available, tests will use the repository's xUnit v3/Microsoft Testing Platform conventions, Arrange-Act-Assert structure, existing BVT/category/area attributes, deterministic synchronization, and supported internal test accessors rather than reflection.

## Hard Boundary: Not in This Preparation Turn

- Do not modify production code.
- Do not create or edit any test source file.
- Do not build, discover, or run tests while the production API is absent.
- Do not restore, clean, reset, stash, or delete tracked files.
- This turn creates only `.testagent/plan.md`; do not create `.testagent/status.md`.
- The phases below begin only after the route-handle/cache-entry API and required test seams land.

## Commands

Run from the workspace root only after the production API lands and dependencies are already available.

- **Scoped build (runtime tests)**: `dotnet build test/Orleans.Runtime.Tests/Orleans.Runtime.Tests.csproj --framework net10.0 --no-restore`
- **Scoped build (core tests)**: `dotnet build test/Orleans.Core.Tests/Orleans.Core.Tests.csproj --framework net10.0 --no-restore`
- **Full build**: `dotnet build Orleans.slnx --no-restore --no-incremental -bl`
- **Scoped test (directory cache lifetime)**: `dotnet test --project test/Orleans.Runtime.Tests/Orleans.Runtime.Tests.csproj --framework net10.0 --no-restore --filter-class "*GrainDirectoryCacheFactoryTests*" --minimum-expected-tests 1`
- **Scoped test (stale async binding)**: `dotnet test --project test/Orleans.Core.Tests/Orleans.Core.Tests.csproj --framework net10.0 --no-restore --filter-class "*CachedGrainLocatorTests*" --minimum-expected-tests 1`
- **Scoped test (shared/local/remote routes and growth)**: `dotnet test --project test/Orleans.Core.Tests/Orleans.Core.Tests.csproj --framework net10.0 --no-restore --filter-class "*MessageTargetCacheTests*" --minimum-expected-tests 1`
- **Scoped test (placement and compatibility)**: `dotnet test --project test/Orleans.Core.Tests/Orleans.Core.Tests.csproj --framework net10.0 --no-restore --filter-class "*PlacementServiceTests*" --minimum-expected-tests 1`
- **Scoped regression (locator selection)**: `dotnet test --project test/Orleans.Core.Tests/Orleans.Core.Tests.csproj --framework net10.0 --no-restore --filter-class "*GrainLocatorResolverTests*" --minimum-expected-tests 1`
- **Full test (all target frameworks with a fresh build)**: `dotnet test --solution Orleans.slnx --no-restore --minimum-expected-tests 1 --max-parallel-test-modules 1`
- **Full discovery check (all target frameworks)**: `dotnet test --solution Orleans.slnx --no-restore --list-tests --minimum-expected-tests 1 --max-parallel-test-modules 1`
- **Lint**: No separate command; the build enforces style and treats warnings, including nullable warnings, as errors.

## Phase Summary

| Phase | Focus | Test files | Est. tests |
|---|---|---:|---:|
| 1 | Directory-entry ownership and stale async binds | 2 existing | 8 |
| 2 | Shared tombstones plus local/remote route validation | 1 new | 9 |
| 3 | Header/compatibility regressions and bounded growth | 2 (1 existing, 1 from Phase 2) | 8 |

---

## Phase 1: Directory Entry Lifetime and Race Safety

### Overview

Establish the leaf/mid-layer lifetime contract first. Every directory cache removal path must invalidate the owned shared route handle, and delayed directory lookups must not bind a disposed or superseded generation.

### Production Scope

Assigned to this phase:

- `src/Orleans.Runtime/GrainDirectory/IGrainDirectoryCache.cs`
- `src/Orleans.Runtime/GrainDirectory/LruGrainDirectoryCache.cs`
- `src/Orleans.Runtime/GrainDirectory/GrainDirectoryCacheFactory.cs`
- `src/Orleans.Runtime/Configuration/Options/GrainDirectoryOptions.cs`
- `src/Orleans.Runtime/GrainDirectory/CachedGrainLocator.cs`

### Files to Test

#### 1. GrainDirectoryCacheFactoryTests.cs

- **Project**: `test/Orleans.Runtime.Tests/Orleans.Runtime.Tests.csproj`
- **Test File**: `test/Orleans.Runtime.Tests/Directories/GrainDirectoryCacheFactoryTests.cs`
- **Test Class**: `GrainDirectoryCacheFactoryTests`
- **Methods/contracts**: factory creation; cache `AddOrUpdate`, both `Remove` variants, `Clear`, expiration cleanup, and capacity eviction.
- **Current status**: Cache mechanics are regression-testable now, but route-handle assertions are blocked. Add all tests only after the API lands.
- **Future seam/blocker**: The cache value must be a disposable directory entry which owns an observable route handle/tombstone. Tests need supported validity/generation observations, not private-field reflection.

**Tests**:

1. `CreateGrainDirectoryCache_AddOrUpdateInvalidatesReplacedRouteHandle`
   - Arrange: Create the LRU cache with the existing factory fixture. Add a directory entry for one `GrainId` with address A and handle H1, then prepare address B with H2.
   - Act: `AddOrUpdate` the same `GrainId` with the replacement entry.
   - Assert: H1 is invalid/disposed exactly once; H2 remains valid and lookup returns address B.
   - Secondary observable: A consumer retaining H1 cannot obtain a usable local receiver or remote connection from it.

2. `CreateGrainDirectoryCache_RemoveByGrainIdInvalidatesRouteHandle`
   - Arrange: Add one route-owning entry and retain its handle separately.
   - Act: Remove by `GrainId`.
   - Assert: The retained handle is a tombstone and cache lookup misses.
   - Secondary observable: A second removal is harmless and does not repeat disposal.

3. `CreateGrainDirectoryCache_RemoveByAddressInvalidatesOnlyMatchingRouteHandle`
   - Arrange: Add an entry for address A and retain H1; create a different address B for the same grain.
   - Act: First remove using B, then remove using A.
   - Assert: The mismatched removal leaves H1 valid and the entry present; the matching removal invalidates H1 and removes the entry.
   - Secondary observable: No unrelated route handle is invalidated.

4. `CreateGrainDirectoryCache_ClearInvalidatesAllRouteHandles`
   - Arrange: Add multiple distinct grain entries and retain every handle.
   - Act: Call `Clear`.
   - Assert: Every retained handle is invalid exactly once and every lookup misses.
   - Secondary observable: A subsequent `Clear` is idempotent.

5. `CreateGrainDirectoryCache_ExpirationInvalidatesRouteHandle`
   - Arrange: Use `FakeTimeProvider`, the configured TTL, and `ConcurrentLruCacheExpirationCleanupListener`; add an entry and retain its handle.
   - Act: Advance fake time beyond TTL and deterministically invoke/wait for expiration cleanup without wall-clock delays.
   - Assert: The handle is invalid and lookup misses.
   - Secondary observable: An unexpired control entry remains valid until its own deadline.

6. `CreateGrainDirectoryCache_EvictionInvalidatesRouteHandle`
   - Arrange: Use deterministic capacity `3`; add three entries, establish the intended LRU order, retain all handles, then prepare a fourth.
   - Act: Add the fourth entry.
   - Assert: Only the least-recently-used entry's handle is invalidated and its lookup misses.
   - Secondary observable: The other three entries/handles remain usable.

#### 2. CachedGrainLocatorTests.cs

- **Project**: `test/Orleans.Core.Tests/Orleans.Core.Tests.csproj`
- **Test File**: `test/Orleans.Core.Tests/Directory/CachedGrainLocatorTests.cs`
- **Test Class**: `CachedGrainLocatorTests`
- **Methods/contracts**: asynchronous `Lookup` cache binding and `Unregister` invalidation.
- **Current status**: Lookup/unregister races are regression-testable now; generation/tombstone assertions are blocked. Existing `UnregisterRacesWithLookupSameId` remains the nearest current regression.
- **Future seam/blocker**: Binding must accept/check an atomic generation or token so an await continuation cannot bind a disposed or superseded handle.

**Tests**:

1. `Lookup_WhenInvalidatedBeforeAsyncLookupCompletes_DoesNotBindStaleRoute`
   - Arrange: Substitute the custom directory and gate its lookup with `TaskCompletionSource` instances created using `RunContinuationsAsynchronously`. Start `Lookup`, wait until the fake records entry, retain the pending route handle/generation, then invalidate or supersede that entry.
   - Act: Complete the old directory lookup with a valid-looking address and await the original operation.
   - Assert: The stale generation is not bound to the returned address/target.
   - Secondary observable: A subsequent lookup takes the ordinary directory path (recorded call count) or uses only the newer generation; it never uses the stale receiver/connection.

2. `Unregister_WhenLookupCompletesAfterRemoval_DoesNotResurrectRouteHandle`
   - Arrange: Start a gated `Lookup` for a grain and wait until the directory call is in flight.
   - Act: Call `Unregister`/remove first, then release the old lookup result.
   - Assert: The removed handle remains an invalid tombstone and no live cache entry is resurrected.
   - Secondary observable: The next lookup invokes the directory again and can bind only a newly issued generation.
   - Cleanup: Release all gates in `finally` so failures cannot leave tasks blocked.

### Phase 1 Success Criteria

- [ ] Production API landing gate is satisfied.
- [ ] Six lifetime paths invalidate exactly the intended handles.
- [ ] Both race tests use deterministic gates, not delays.
- [ ] Scoped runtime and core builds pass.
- [ ] Both scoped test classes pass.

---

## Phase 2: Shared Tombstones and Direct Route Validation

### Overview

Verify shared identity/lifetime first, then local and remote fast-path validity. These tests cover top-level route consumption while isolating fallback behavior with fakes and call counters.

### Production Scope

Assigned to this phase:

- `src/Orleans.Core.Abstractions/Runtime/GrainReference.cs`
- `src/Orleans.Runtime/Catalog/ActivationDirectory.cs`
- `src/Orleans.Runtime/Catalog/Catalog.cs`
- `src/Orleans.Runtime/Catalog/ActivationData.cs`
- `src/Orleans.Core.Abstractions/Core/IGrainContext.cs`
- `src/Orleans.Core/Networking/Connection.cs`
- `src/Orleans.Core/Networking/ConnectionManager.cs`
- `src/Orleans.Runtime/Messaging/MessageCenter.cs`

### File to Test

#### MessageTargetCacheTests.cs

- **Project**: `test/Orleans.Core.Tests/Orleans.Core.Tests.csproj`
- **Test File**: `test/Orleans.Core.Tests/Runtime/MessageTargetCacheTests.cs` (new, only after API landing)
- **Test Class**: `MessageTargetCacheTests`
- **Methods/contracts**: future shared route acquisition/invalidation, local receiver probe, remote connection-group probe, and fallback.
- **Fixtures/fakes**: NSubstitute for interfaces; a controllable collectible grain-context fake exposing exact `GrainAddress` and `IsValid`; a controllable connection/group fake exposing address, group identity, and `IsValid`; ordinary lookup/acquisition delegates with invocation counters.
- **Future seam/blocker**: An observable atomic route-handle/tombstone, weak local receiver retention after invalidation, remote group identity/address validation, and ordinary fallback hooks are all required.

**Shared identity and lifetime tests**:

1. `GrainReferences_WithSameGrainIdShareRouteHandle`
   - Arrange: Create two grain references for the same `GrainId` through the normal activator/reference path, including different compatible interface views if supported.
   - Act: Obtain the supported route-handle view and bind/invalidate through one reference.
   - Assert: Both references expose the same handle instance and observe the same state transition.
   - Secondary observable: The supported route-entry count increases once, not once per reference/interface view.

2. `GrainReference_RetainsDisposedRouteHandleTombstone`
   - Arrange: Create a grain reference and its directory entry; retain the reference, dispose/remove the entry, and drop other strong references to the live entry.
   - Act: Force bounded GC only if needed to separate entry lifetime from reference lifetime.
   - Assert: The grain reference still retains the same invalid tombstone, rather than silently acquiring a new live handle.
   - Secondary observable: A route probe through that reference falls back and cannot use the former target.

3. `DisposedRouteHandle_DoesNotRetainLocalReceiver`
   - Arrange: In a `[MethodImpl(MethodImplOptions.NoInlining)]` helper, create a collectible local receiver, bind it to the handle, return a retained grain reference plus `WeakReference` to the receiver, invalidate the handle, and ensure the helper drops all receiver/context strong references.
   - Act: Perform a bounded collect/finalize/collect loop, following the collectible-context test pattern; do not use timing sleeps.
   - Assert: The receiver weak reference is no longer alive while the grain reference and tombstone remain alive.
   - Secondary observable: The retained tombstone is invalid and its local probe takes fallback.

**Local route tests**:

4. `LocalRoute_WhenAddressMatchesAndActivationIsValidUsesReceiver`
   - Arrange: Bind a local receiver whose `IGrainContext.Address` exactly equals the cached `GrainAddress` and whose collectible context reports `IsValid = true`; install an ordinary locator fallback counter.
   - Act: Resolve/send through the local route probe.
   - Assert: The exact receiver is selected.
   - Secondary observable: Ordinary locator/fallback is not called and no different activation receives the message.

5. `LocalRoute_WhenActivationAddressDiffersFallsBack`
   - Arrange: Bind a receiver with the same grain identity but a different activation/address instance; configure fallback to return a known target.
   - Act: Probe the cached local route.
   - Assert: The cached receiver is rejected and the known fallback result is selected.
   - Secondary observable: Fallback is called exactly once and the mismatched receiver is not sent to.

6. `LocalRoute_WhenActivationIsInvalidFallsBack`
   - Arrange: Bind a receiver with an exactly matching address but `IsValid = false`; configure a known fallback.
   - Act: Probe the cached local route.
   - Assert: The invalid receiver is rejected and fallback is selected.
   - Secondary observable: Fallback is called once and no direct send occurs.

**Remote route tests**:

7. `RemoteRoute_WhenConnectionGroupMatchesAddressUsesConnection`
   - Arrange: Bind a valid connection from the exact cached connection group associated with the destination `SiloAddress`; install an ordinary `ConnectionManager` acquisition counter.
   - Act: Resolve/send through the remote route.
   - Assert: The bound connection is selected.
   - Secondary observable: Ordinary connection acquisition is not called and destination address remains exact.

8. `RemoteRoute_WhenConnectionGroupAddressDiffersFallsBack`
   - Arrange: Bind a valid connection/group for silo A while the message destination is silo B; configure ordinary acquisition to return a known connection for B.
   - Act: Probe/send the cached route.
   - Assert: The stale group/connection is rejected and the B fallback connection is selected.
   - Secondary observable: Ordinary acquisition is called once and the A connection receives no send.

9. `RemoteRoute_WhenConnectionIsInvalidFallsBack`
   - Arrange: Bind the exact destination group but make the selected connection report `IsValid = false`; configure a valid ordinary fallback connection.
   - Act: Probe/send the route.
   - Assert: The invalid cached connection is rejected and fallback is selected.
   - Secondary observable: Ordinary acquisition is called once and the invalid connection receives no send.

### Phase 2 Success Criteria

- [ ] Shared references demonstrably use one observable handle.
- [ ] Disposed handles remain tombstones but do not root receivers.
- [ ] Local probes require exact address and valid state.
- [ ] Remote probes require exact group/address and valid connection.
- [ ] Every rejection proves both non-use of the stale target and use of ordinary fallback.
- [ ] Scoped core build and `MessageTargetCacheTests` pass.

---

## Phase 3: Placement Compatibility, Invalidation Headers, and Growth Guard

### Overview

Protect existing routing behavior around the new fast path, then add a durable supported-count guard against accidental per-client-`GrainId` growth.

### Production Scope

Assigned to this phase:

- `src/Orleans.Runtime/Placement/PlacementService.cs`
- `src/Orleans.Runtime/Core/InsideRuntimeClient.cs`
- `src/Orleans.Runtime/GrainDirectory/ClientGrainLocator.cs`
- `src/Orleans.Runtime/GrainDirectory/GrainLocatorResolver.cs`
- `src/Orleans.Runtime/GrainDirectory/GrainLocator.cs`
- `src/Orleans.Core/GrainReferences/GrainReferenceActivator.cs`
- `src/Orleans.Core/Messaging/ClientMessageCenter.cs`
- `src/Orleans.Core/Runtime/OutsideRuntimeClient.cs`

### Files to Test

#### 1. PlacementServiceTests.cs

- **Project**: `test/Orleans.Core.Tests/Orleans.Core.Tests.csproj`
- **Test File**: `test/Orleans.Core.Tests/Runtime/PlacementServiceTests.cs`
- **Test Class/fixture**: Existing `PlacementServiceTests` and `PlacementServiceFixture`
- **Methods/contracts**: `AddressMessage`, invalidation-header handling, and compatibility path selection.
- **Current status**: Header bypass and current locator/factory selection are regression-testable now, but all additions remain gated. Shared-route non-interference assertions require the future API.
- **Fixtures/fakes**: Reuse `PlacementServiceFixture`, `MockClusterMembershipService`, `SiloLifecycleSubject`, and NSubstitute. Explicitly stop lifecycle resources and dispose in `finally`.
- **Future seam/blocker**: Fixture-visible route binding/state and supported interaction points for the ordinary locator/placement path.

**Tests**:

1. `AddressMessage_CacheInvalidationHeaderBypassesSharedRouteFastPath`
   - Arrange: Bind a shared fast route to stale address/receiver A; create a message carrying a matching cache-invalidation header; configure ordinary locator/worker resolution to return B.
   - Act: Call `AddressMessage`.
   - Assert: The message is addressed through B, not A, and the stale handle is invalidated/bypassed.
   - Secondary observable: Ordinary locator/placement is called once and A receives no send.

2. `AddressMessage_CacheInvalidationHeaderWithReplacementUsesReplacementRoute`
   - Arrange: Bind stale route A and create an invalidation header containing valid replacement address B; configure locator cache update observation.
   - Act: Call `AddressMessage`.
   - Assert: The resulting destination/route is B and A is not reused.
   - Secondary observable: The locator/cache records the replacement once and the retained A handle remains invalid.

3. `AddressMessage_CustomDirectoryCacheUsesExistingPlacementPath`
   - Arrange: Configure the custom directory-cache strategy and a substitute custom cache/locator with a known result; capture the supported route-entry count.
   - Act: Address a message for the applicable directory grain.
   - Assert: The established custom-cache/placement result is used.
   - Secondary observable: The custom dependency receives the expected call and no unsupported shared fast-route entry is created.

4. `AddressMessage_DisabledDirectoryCacheUsesExistingPlacementPath`
   - Arrange: Configure caching as disabled/none and a known ordinary locator/placement result.
   - Act: Address the message.
   - Assert: The ordinary existing path supplies the destination.
   - Secondary observable: No shared directory route is consulted or retained; supported count remains unchanged.

5. `AddressMessage_DefaultDirectoryGrainUsesExistingPlacementPath`
   - Arrange: Use a normal default-directory/DHT grain and configure its existing locator/worker result.
   - Act: Address the message.
   - Assert: The existing default-directory placement path produces the expected destination.
   - Secondary observable: The expected locator interaction occurs once; no custom/client locator is called.

6. `AddressMessage_ClientGrainUsesExistingPlacementPath`
   - Arrange: Use a client `GrainId` and configure the existing client locator/routing result.
   - Act: Address the message.
   - Assert: The client-grain path produces the expected destination.
   - Secondary observable: No directory-entry route is allocated and directory locator/cache calls remain absent.

7. `SendMessage_SystemTargetUsesExistingFullyAddressedPath`
   - Arrange: Create a fully addressed system-target message with a known local or remote target and record the supported directory-route count.
   - Act: Send the message through the normal `MessageCenter` path.
   - Assert: The existing system-target send path selects the exact addressed target without consulting or allocating a directory-entry route.
   - Secondary observable: The expected direct local/remote send occurs once, directory locator/cache calls remain absent, and the supported directory-route count is unchanged.

#### 2. MessageTargetCacheTests.cs — Growth Slice

- **Project**: `test/Orleans.Core.Tests/Orleans.Core.Tests.csproj`
- **Test File**: `test/Orleans.Core.Tests/Runtime/MessageTargetCacheTests.cs`
- **Test Class**: `MessageTargetCacheTests`
- **Future seam/blocker**: A supported route-entry count/accessor partitioned by grain kind. Never inspect dictionary/private field names by reflection.

1. `RouteCache_ManyClientGrainIdsDoesNotCreatePerGrainEntries`
   - Arrange: Record the supported client-route and directory-route counts. Create/address a large deterministic set of unique client `GrainId` values through the public activator/client path. Include one supported directory grain as a control proving the accessor can observe an eligible entry.
   - Act: Exercise route acquisition for all client IDs and read the supported counters.
   - Assert: Client-grain route-entry count remains at baseline (normally zero), while the control changes only its expected supported count.
   - Secondary observable: Client routing still succeeds through the existing fixed client sender-bucket/weak-connection path, demonstrating that bounded behavior is functional rather than skipped.
   - Synchronization/GC: No private reflection and no GC-based size inference; use the supported accessor after all deterministic operations complete.

### Phase 3 Success Criteria

- [ ] Matching invalidation headers bypass stale fast routes.
- [ ] Replacement headers select and record the replacement route.
- [ ] Custom and disabled caches plus client and system-target non-directory types retain their existing behavior; the default-directory path remains a compatibility control.
- [ ] Many unique client IDs do not create per-grain route entries.
- [ ] Scoped placement, route-cache, and locator-resolver regressions pass.
- [ ] Full build, discovery, and full test commands pass.

---

## Requirement-to-Test Traceability

| Acceptance behavior | Exact project/file | Exact proposed test name(s) | Preparation status / production seam |
|---|---|---|---|
| Replacement invalidates shared handle | `Orleans.Runtime.Tests` / `Directories/GrainDirectoryCacheFactoryTests.cs` | `CreateGrainDirectoryCache_AddOrUpdateInvalidatesReplacedRouteHandle` | Handle assertion blocked; needs disposable route-owning entry |
| Remove by grain ID invalidates | Same | `CreateGrainDirectoryCache_RemoveByGrainIdInvalidatesRouteHandle` | Handle assertion blocked |
| Remove by address affects only exact match | Same | `CreateGrainDirectoryCache_RemoveByAddressInvalidatesOnlyMatchingRouteHandle` | Handle assertion blocked |
| Clear invalidates all | Same | `CreateGrainDirectoryCache_ClearInvalidatesAllRouteHandles` | Handle assertion blocked |
| TTL invalidates | Same | `CreateGrainDirectoryCache_ExpirationInvalidatesRouteHandle` | Mechanics possible now; handle blocked; use fake time/listener |
| Capacity eviction invalidates | Same | `CreateGrainDirectoryCache_EvictionInvalidatesRouteHandle` | Mechanics possible now; handle blocked; capacity 3 |
| Stale async bind cannot resurrect route | `Orleans.Core.Tests` / `Directory/CachedGrainLocatorTests.cs` | `Lookup_WhenInvalidatedBeforeAsyncLookupCompletes_DoesNotBindStaleRoute`; `Unregister_WhenLookupCompletesAfterRemoval_DoesNotResurrectRouteHandle` | Race regression possible now; generation/tombstone blocked |
| References share route state | `Orleans.Core.Tests` / `Runtime/MessageTargetCacheTests.cs` | `GrainReferences_WithSameGrainIdShareRouteHandle` | Fully blocked on shared handle API |
| Reference retains disposed tombstone | Same | `GrainReference_RetainsDisposedRouteHandleTombstone` | Fully blocked |
| Tombstone does not retain receiver | Same | `DisposedRouteHandle_DoesNotRetainLocalReceiver` | Fully blocked; needs weak retention and observable tombstone |
| Local route requires exact address and valid receiver | Same | `LocalRoute_WhenAddressMatchesAndActivationIsValidUsesReceiver`; `LocalRoute_WhenActivationAddressDiffersFallsBack`; `LocalRoute_WhenActivationIsInvalidFallsBack` | Existing primitives testable; direct route probe blocked |
| Remote route requires matching group/address and valid connection, with fallback | Same | `RemoteRoute_WhenConnectionGroupMatchesAddressUsesConnection`; `RemoteRoute_WhenConnectionGroupAddressDiffersFallsBack`; `RemoteRoute_WhenConnectionIsInvalidFallsBack` | Existing manager behavior testable; cached group route blocked |
| Invalidation header bypasses/replaces fast route | `Orleans.Core.Tests` / `Runtime/PlacementServiceTests.cs` | `AddressMessage_CacheInvalidationHeaderBypassesSharedRouteFastPath`; `AddressMessage_CacheInvalidationHeaderWithReplacementUsesReplacementRoute` | Header regression possible now; shared-route observation blocked |
| Custom/disabled caches and non-directory grain types use the existing path | `Orleans.Core.Tests` / `Runtime/PlacementServiceTests.cs` and `Runtime/MessageTargetCacheTests.cs` | `AddressMessage_CustomDirectoryCacheUsesExistingPlacementPath`; `AddressMessage_DisabledDirectoryCacheUsesExistingPlacementPath`; `AddressMessage_ClientGrainUsesExistingPlacementPath`; `SendMessage_SystemTargetUsesExistingFullyAddressedPath` | Existing selection/send behavior possible now; shared-route non-interference count blocked |
| Default-directory compatibility control retains existing path | `Orleans.Core.Tests` / `Runtime/PlacementServiceTests.cs` | `AddressMessage_DefaultDirectoryGrainUsesExistingPlacementPath` | Existing locator selection possible now; route interaction observation blocked |
| No per-client-`GrainId` route growth | `Orleans.Core.Tests` / `Runtime/MessageTargetCacheTests.cs` | `RouteCache_ManyClientGrainIdsDoesNotCreatePerGrainEntries` | Blocked on supported kind-aware count/accessor |

## Implementation Order and Validation

1. Confirm the landed API supplies all six required seams: observable atomic tombstone, disposable owning entry, generation-aware bind, exact local probe, group-aware remote probe, and kind-aware count/accessor.
2. Implement Phase 1 tests and run only its scoped builds/tests.
3. Implement Phase 2 tests, using the new cohesive test file, and run its scoped core build/test.
4. Implement Phase 3 tests and run placement, message-target-cache, and locator-resolver scopes.
5. Run full build, discovery, and full test only after all scoped checks pass.
6. Run the mandatory final pre-completion gate against the final test set: invoke `test-gap-analysis` for each tested source/test pair, fix every in-scope mutation gap, invoke `assertion-quality`, fix weak/trivial assertions, and manually map every prompt scenario to the exact tests in this plan.
7. If coverage-gap review adds or changes a test, repeat the complete pre-completion gate.
8. Write `.testagent/status.md` only after implementation, quality review, and validation, recording checklist evidence, commands/results, quality findings/fixes, and blockers.
9. Preserve repository xUnit v3/MTP metadata: `[Fact]`/`[Theory]`, class-level `[TestSuite("BVT")]`, `[TestProvider("None")]`, appropriate `[TestCategory]` and `[TestArea]`, async `Task`, and focused assertions.
