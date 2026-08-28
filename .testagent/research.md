# Test Generation Research

> Final scope update: cached targets are limited to local `IGrainContext` instances and remote `ConnectionManager.ConnectionEntry` groups. Remote references retain the shared directory entry for address reuse; response-route caching remains a separate experiment.

## Project Overview
- **Path**: `C:\dev\copilot-worktrees\orleans\rb-scaling-guacamole`
- **Boundary**: Orleans runtime directory caching, silo/client message routing, local activation lookup, connection selection, and grain-reference creation. Samples, providers, streaming, persistence, and unrelated tests are out of scope.
- **Language / framework**: C# on SDK-style .NET; source targets `net8.0;net10.0`, and `global.json` selects SDK `10.0.400` with major roll-forward.
- **Test framework**: xUnit v3 `3.2.2` hosted by Microsoft Testing Platform (MTP) `2.3.3`.
- **Dependencies**: Central `PackageReference` versions in `Directory.Packages.props`; NSubstitute `5.3.0`, NSubstitute analyzer `1.0.17`, Microsoft.Extensions.TimeProvider.Testing `9.10.0`, and AwesomeAssertions `9.3.0`.
- **New-file registration**: SDK implicit `Compile` glob. A future `*.cs` test file needs no project edit.
- **Current state**: The shared directory-entry message-target/route-handle API does not exist. `GrainReference` currently stores only `GrainReferenceShared` and `IdSpan`; `IGrainDirectoryCache` stores `GrainAddress`; `PlacementService` resolves a silo for every not-fully-addressed message.

## Static Pairing Heuristic
- The required Roslyn analyzer was executed **exactly once**, at the repository root. This is the narrowest common root because the bounded production files are under `src/` and their tests are under `test/`.
- Analyzer diagnostics: 3,844 C# files discovered; 2,837 source and 1,007 test files; indexing/scanning completed in 3.192 seconds.
- The file-based SDK emitted a leading `C...` build line into captured stdout, so `ConvertFrom-Json` rejected the combined stream before the relevant `source_to_tests` and `suggested_test_path` nodes could be extracted. It was not rerun, honoring the exactly-once constraint.
- Consequently, the pairings below are direct, bounded source/test references and naming conventions, **not recovered analyzer pairings**. The analyzer is a static identifier-pairing heuristic in any case, not line or branch coverage.

## Dependency Graph and Production Seams
- **Leaf types**:
  - `src/Orleans.Runtime/Catalog/ActivationDirectory.cs` — grain-id-to-context table; exact-instance conditional removal.
  - `src/Orleans.Core.Abstractions/Runtime/GrainReference.cs` — reference identity and invocation surface.
  - `src/Orleans.Runtime/GrainDirectory/IGrainDirectoryCache.cs` — current address-only cache contract.
  - `src/Orleans.Core/Networking/Connection.cs` — exposes `IsValid`.
- **Mid-layer types**:
  - `src/Orleans.Runtime/GrainDirectory/LruGrainDirectoryCache.cs` — wraps `ConcurrentLruCache`; replacement/removal/clear/expiry/eviction dispose cache values only when the value itself implements `IDisposable`.
  - `src/Orleans.Runtime/GrainDirectory/GrainDirectoryCacheFactory.cs` and `Configuration/Options/GrainDirectoryOptions.cs` — LRU/custom/none selection and keyed `TimeProvider`.
  - `src/Orleans.Runtime/GrainDirectory/CachedGrainLocator.cs` — async custom-directory lookup and cache population/invalidation.
  - `src/Orleans.Runtime/GrainDirectory/ClientGrainLocator.cs`, `GrainLocatorResolver.cs`, and `GrainLocator.cs` — client/DHT/custom-directory path selection.
  - `src/Orleans.Core/Networking/ConnectionManager.cs` — per-silo connection groups; `NextConnection()` validates the selected connection and the slow path removes defunct connections.
  - `src/Orleans.Core/GrainReferences/GrainReferenceActivator.cs` — bounded today by `(GrainType, GrainInterfaceType)`, not `GrainId`.
- **Top-layer types**:
  - `src/Orleans.Runtime/Placement/PlacementService.cs` — current cache fast path and cache-invalidation-header validation.
  - `src/Orleans.Runtime/Messaging/MessageCenter.cs` — local loopback, remote send, fallback connection acquisition, and address/send sequencing.
  - `src/Orleans.Runtime/Core/InsideRuntimeClient.cs` — applies invalidation headers before response handling.
  - `src/Orleans.Core/Messaging/ClientMessageCenter.cs` and `Runtime/OutsideRuntimeClient.cs` — client path; routing is a fixed-size array of weak connection references, not a per-`GrainId` dictionary.
  - `src/Orleans.Runtime/Catalog/Catalog.cs`, `ActivationData.cs`, and `src/Orleans.Core.Abstractions/Core/IGrainContext.cs` — local receiver address and validity.
- **Required future seams**:
  1. An observable shared route-handle/tombstone with atomic bind/invalidate state.
  2. A disposable directory-cache entry which owns that handle, so every LRU removal path invalidates it.
  3. Generation/token-aware binding so an await continuation cannot bind a disposed or superseded handle.
  4. A local-route probe which validates both exact `GrainAddress` and receiver validity without strongly retaining the receiver after invalidation.
  5. A remote-route probe which validates destination `SiloAddress`, connection-group identity, and connection validity, then exposes the ordinary fallback path.
  6. A test accessor/counter for route entries by grain kind; do not test private field names by reflection.

## Build & Test Commands
Run from the repository root. `--no-restore` preserves the no-restore constraint once dependencies are already available.

- **Build (scoped)**:
  - `dotnet build test/Orleans.Core.Tests/Orleans.Core.Tests.csproj --framework net10.0 --no-restore`
  - `dotnet build test/Orleans.Runtime.Tests/Orleans.Runtime.Tests.csproj --framework net10.0 --no-restore`
- **Build (full)**: `dotnet build Orleans.slnx --no-restore --no-incremental -bl`
- **Test (scoped — future core route tests)**: `dotnet test --project test/Orleans.Core.Tests/Orleans.Core.Tests.csproj --framework net10.0 --no-restore --filter-class "*MessageTargetCacheTests*" --minimum-expected-tests 1`
- **Test (scoped — existing cache file)**: `dotnet test --project test/Orleans.Runtime.Tests/Orleans.Runtime.Tests.csproj --framework net10.0 --no-restore --filter-class "*GrainDirectoryCacheFactoryTests*" --minimum-expected-tests 1`
- **Test (scoped — current regression files)**: substitute one of `*PlacementServiceTests*`, `*CachedGrainLocatorTests*`, or `*GrainLocatorResolverTests*` for `*MessageTargetCacheTests*` in the core command.
- **Test (full, all target frameworks with a fresh build)**: `dotnet test --solution Orleans.slnx --no-restore --minimum-expected-tests 1 --max-parallel-test-modules 1`
- **Harness-equivalent discovery (all target frameworks)**: `dotnet test --solution Orleans.slnx --no-restore --list-tests --minimum-expected-tests 1 --max-parallel-test-modules 1`
- **Lint**: no separate command; `dotnet build` enforces style and treats warnings/nullable warnings as errors.

## Exact Test Targets

### Projects
1. `test/Orleans.Runtime.Tests/Orleans.Runtime.Tests.csproj`
   - Target: `src/Orleans.Runtime/Orleans.Runtime.csproj`.
   - Canonical file: `test/Orleans.Runtime.Tests/Directories/GrainDirectoryCacheFactoryTests.cs`.
2. `test/Orleans.Core.Tests/Orleans.Core.Tests.csproj`
   - Targets: `src/Orleans.Core/Orleans.Core.csproj`, `src/Orleans.Core.Abstractions/Orleans.Core.Abstractions.csproj`, and `src/Orleans.Runtime/Orleans.Runtime.csproj`.
   - Canonical files: `Directory/CachedGrainLocatorTests.cs`, `Directory/GrainLocatorResolverTests.cs`, and `Runtime/PlacementServiceTests.cs`.
   - One cohesive **future** file, added only after the API exists: `test/Orleans.Core.Tests/Runtime/MessageTargetCacheTests.cs`.

### Exact proposed test names

`test/Orleans.Runtime.Tests/Directories/GrainDirectoryCacheFactoryTests.cs`:
- `CreateGrainDirectoryCache_AddOrUpdateInvalidatesReplacedRouteHandle`
- `CreateGrainDirectoryCache_RemoveByGrainIdInvalidatesRouteHandle`
- `CreateGrainDirectoryCache_RemoveByAddressInvalidatesOnlyMatchingRouteHandle`
- `CreateGrainDirectoryCache_ClearInvalidatesAllRouteHandles`
- `CreateGrainDirectoryCache_ExpirationInvalidatesRouteHandle`
- `CreateGrainDirectoryCache_EvictionInvalidatesRouteHandle`

`test/Orleans.Core.Tests/Directory/CachedGrainLocatorTests.cs`:
- `Lookup_WhenInvalidatedBeforeAsyncLookupCompletes_DoesNotBindStaleRoute`
- `Unregister_WhenLookupCompletesAfterRemoval_DoesNotResurrectRouteHandle`

`test/Orleans.Core.Tests/Runtime/PlacementServiceTests.cs`:
- `AddressMessage_CacheInvalidationHeaderBypassesSharedRouteFastPath`
- `AddressMessage_CacheInvalidationHeaderWithReplacementUsesReplacementRoute`
- `AddressMessage_CustomDirectoryCacheUsesExistingPlacementPath`
- `AddressMessage_DisabledDirectoryCacheUsesExistingPlacementPath`
- `AddressMessage_DefaultDirectoryGrainUsesExistingPlacementPath`
- `AddressMessage_ClientGrainUsesExistingPlacementPath`
- `SendMessage_SystemTargetUsesExistingFullyAddressedPath`

`test/Orleans.Core.Tests/Runtime/MessageTargetCacheTests.cs` (future file):
- `GrainReferences_WithSameGrainIdShareRouteHandle`
- `GrainReference_RetainsDisposedRouteHandleTombstone`
- `DisposedRouteHandle_DoesNotRetainLocalReceiver`
- `LocalRoute_WhenAddressMatchesAndActivationIsValidUsesReceiver`
- `LocalRoute_WhenActivationAddressDiffersFallsBack`
- `LocalRoute_WhenActivationIsInvalidFallsBack`
- `RemoteRoute_WhenConnectionGroupMatchesAddressUsesConnection`
- `RemoteRoute_WhenConnectionGroupAddressDiffersFallsBack`
- `RemoteRoute_WhenConnectionIsInvalidFallsBack`
- `RouteCache_ManyClientGrainIdsDoesNotCreatePerGrainEntries`

## Acceptance Checklist and Blockers
1. **Replacement/removal/clear/TTL/eviction invalidates a shared route handle** — **blocked on future API** for the handle assertion. Cache mechanics are regression-testable now: factory TTL coverage exists, and `ConcurrentLruCache` already disposes `IDisposable` values on update/remove/clear/expiry/eviction. Use `FakeTimeProvider` plus `ConcurrentLruCacheExpirationCleanupListener`; capacity `3` is the minimum deterministic eviction fixture.
2. **Stale asynchronous binding cannot resurrect invalid entries** — **partly regression-testable now** via `CachedGrainLocator` lookup/unregister races; handle generation/tombstone assertions are blocked. Existing `UnregisterRacesWithLookupSameId` is the closest regression.
3. **Grain references retain disposed tombstones without retaining receivers** — **fully blocked**. There is no route field/tombstone today. Use a `[MethodImpl(NoInlining)]` setup helper, `WeakReference`, and bounded collect/finalize/collect attempts, following `ProviderRegistrationResolverTests.GeneratedRegistry_DoesNotRootCollectibleAssemblyLoadContext`.
4. **Local activation exact-address/state validation** — exact-instance removal and validity are regression-testable in `ActivationDirectory`/`LocalActivationStatusChecker`; direct-route validation against `IGrainContext.Address` and `ICollectibleGrainContext.IsValid` is **blocked**.
5. **Remote connection-group route validation and fallback** — current `ConnectionManager` valid/defunct selection and `MessageCenter` fallback are regression-testable; cached route/group identity is **blocked**.
6. **Cache invalidation headers bypass the fast path** — **regression-testable now**. `PlacementService.CachedAddressIsValid` updates/invalidates the locator and rejects a matching invalid address before worker lookup. Add the named tests to the existing `PlacementServiceFixture`.
7. **Custom/disabled caches and non-directory grain types retain the existing path** — **regression-testable now** for factory and locator selection; future shared-route non-interference needs the new API. Treat client grains and system targets as the explicit non-directory cases. Existing coverage includes custom/none factory tests and DHT/client/custom locator resolver tests; add the named system-target send guard after the new API lands.
8. **The original unbounded client `GrainId` mapping does not return** — current code already has no such map: `GrainReferenceActivator` keys by type/interface and `ClientMessageCenter` uses a fixed `ClientSenderBuckets` array of weak references. A durable count-based guard is **blocked** pending a route-cache test accessor; avoid private-field reflection.
9. **No production modifications and no test source additions during preparation** — satisfied. This turn creates only `.testagent/research.md`.
10. **Exact proposed names and target files/projects** — supplied in “Exact Test Targets”.

## Existing Tests & Coverage Classification
- `GrainDirectoryCacheFactory.cs` / `LruGrainDirectoryCache.cs` → `test/Orleans.Runtime.Tests/Directories/GrainDirectoryCacheFactoryTests.cs`: **partial** for the forthcoming scope. It covers TTL, ownership, custom/none selection, and disposal policy, but no shared route exists.
- `CachedGrainLocator.cs` → `test/Orleans.Core.Tests/Directory/CachedGrainLocatorTests.cs`: **substantial current behavior**, including cache population, dead silos, unregister-first, and races; no route binding.
- `GrainLocatorResolver.cs` / client-DHT-custom selection → `test/Orleans.Core.Tests/Directory/GrainLocatorResolverTests.cs`: **substantial for selection**, only three focused tests.
- `PlacementService.cs` → `test/Orleans.Core.Tests/Runtime/PlacementServiceTests.cs`: **partial**. Strong lifecycle/version/placement fixture, but only the stopped-state test currently exercises `AddressMessage`; no invalidation-header fast-path test.
- `ActivationDirectory.cs` / local receiver validity → indirect uses in `CachedGrainLocatorTests.cs` and `Membership/MembershipSystemTargetTests.cs`: **partial**, no dedicated exact-address/state route tests.
- `ConnectionManager.cs` / `MessageCenter.cs`: **partial integration coverage**, no directly paired connection-group route test found in the bounded test set.
- `GrainReference.cs` / `GrainReferenceActivator.cs`: existing default-cluster reference/cast tests cover identity and casting, but forthcoming route retention is **untested/absent**.
- `ClientMessageCenter.cs`: **partial existing behavior**, with no dedicated bounded test file for per-grain route growth.
- These classifications are structural, not numeric coverage measurements.

## Testing Patterns and Helpers
- Representative conventions:
  - `test/Orleans.Core.Tests/Runtime/PlacementServiceTests.cs`
  - `test/Orleans.Runtime.Tests/Directories/GrainDirectoryCacheFactoryTests.cs`
- Use xUnit `[Fact]`/`[Theory]`, `Assert.*`, async `Task`, and class-level `[TestSuite("BVT")]`, `[TestProvider("None")]`, `[TestCategory("BVT"), TestCategory("Directory" or "Placement")]`, plus an appropriate `[TestArea]`.
- Prefer deterministic `TaskCompletionSource(...RunContinuationsAsynchronously)`, `FakeTimeProvider`, explicit lifecycle stop, and `try/finally` disposal.
- Reuse `PlacementServiceFixture`, `MockClusterMembershipService`, `SiloLifecycleSubject`, NSubstitute, and `ConcurrentLruCacheExpirationCleanupListener`.
- Assert both the selected result and the bypass/fallback interaction (for example, no stale receiver send and one ordinary locator/connection lookup). Do not rely on timing delays or inspect private fields.

## Priority
1. After the API lands, implement route-handle ownership/invalidation and stale-bind tests first; they protect lifetime and race correctness.
2. Add local/remote route validation and GC-retention tests.
3. Add header-bypass and compatibility-path regressions.
4. Add the client growth guard using a supported internal test accessor.

## Preparation Constraints
- No build or test was run because the production API is absent and this was planning-only.
- No restore, clean, reset, stash, deletion, production edit, or test-source addition was performed.
