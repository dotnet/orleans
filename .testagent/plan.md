# Test Implementation Plan

## Overview

This is a broad, bounded cluster-lifecycle test slice for issue #10864. It covers all five inventoried TestingHost sources, beginning with deterministic leaf helpers and then testing real in-process orchestration and fake-handle out-of-process orchestration. All tests belong in the existing `Orleans.TestingHost.Tests` project.

The plan extends genuinely uncovered lifecycle behavior. It does not duplicate merged diagnostics/logger tests, alter production behavior solely to make tests possible, or include `StorageEmulator.cs` because live references remain. Each test must use exact state, identity, set, call-count, exception, and cleanup assertions—never sleeps, polling, timing ranges, broad catches, weak presence assertions, or skip attributes.

Follow the existing xUnit v3/MTP style: `[Fact]`, `TestContext.Current.CancellationToken`, `[TestArea("TestingHost")]`, `[TestProvider("None")]`, and the neighboring class’s `TestSuite`/`TestCategory` convention. Use functional traits for real-cluster cases and the existing fast-suite convention for direct fake-based contracts.

## Commands

Run from the repository root.

- **Build (narrow net10)**: `dotnet build test/TestInfrastructure/Orleans.TestingHost.Tests/Orleans.TestingHost.Tests.csproj --framework net10.0`
- **Test (all implementation cycles)**: `dotnet test --project test/TestInfrastructure/Orleans.TestingHost.Tests/Orleans.TestingHost.Tests.csproj --framework net10.0 --minimum-expected-tests 1 --max-parallel-test-modules 1`
- **Discovery check**: `dotnet test --project test/TestInfrastructure/Orleans.TestingHost.Tests/Orleans.TestingHost.Tests.csproj --framework net10.0 --list-tests --minimum-expected-tests 1 --max-parallel-test-modules 1`
- **Lint**: no separate command; the narrow build enforces code style and treats warnings, including nullable warnings, as errors.
- **Not part of this child task**: full-solution build and final coverage collection. The parent session owns both.

## Phase Summary

| Phase | Focus | Assigned target files | Est. new tests |
|---|---|---:|---:|
| 1 | Deterministic stabilization-helper contracts | 2 | 10–12 |
| 2 | Directory observation and in-process lifecycle | 2 | 13–17 |
| 3 | `TestCluster` lifecycle with fake handles | 1 | 10–13 |
| 4 | Narrow validation and mandatory reviews | 0 | Review only |

Each target source is assigned to exactly one implementation phase.

| Target | Public/internal lifecycle surface accounted for |
|---|---|
| `ClusterManifestStabilizationHelper.cs` | `WaitForExpectedClusterManifestAsync`: direct Phase 1 success, false, null, empty, and zero-timeout tests |
| `LivenessStabilizationHelper.cs` | Combined, silo, and gateway waits: direct Phase 1 tests plus real gateway convergence in Phase 2 |
| `GrainDirectoryObserver.cs` | `CanObserve`, `WaitForConvergenceAsync`, event handling, and `HasConverged`: supported/unsupported and exact-set Phase 2 tests |
| `InProcTestCluster.cs` | Existing deploy/start coverage retained; Phase 2 adds topology, restart, stop, kill, client, creation failure/cancellation, and sync/async disposal |
| `TestCluster.cs` | Existing deploy/configuration/start coverage retained; Phase 3 adds active selection, topology, restart, stop, kill, client, creation failure/cancellation, and sync/async disposal |

---

## Phase 1: Deterministic Stabilization-Helper Contracts

### Overview

Establish direct, fast contracts for the two untested leaf helpers before using them through cluster orchestration. Add hand-written recording `ITestHooks` fakes in the test file; do not add a mocking package or a production seam.

### Files to Test

#### 1. `ClusterManifestStabilizationHelper.cs`

- **Source**: `src/Orleans.TestingHost/ClusterManifestStabilizationHelper.cs`
- **Test File**: `test/TestInfrastructure/Orleans.TestingHost.Tests/ClusterManifestStabilizationHelperTests.cs`
- **Test Class**: `ClusterManifestStabilizationHelperTests`
- **Method**: `WaitForExpectedClusterManifestAsync`

**Planned tests**:

1. `WaitForExpectedClusterManifestAsync_WhenHooksAreNull_ThrowsArgumentNullException`
   - Assert the exact `ArgumentNullException` parameter name.
   - Assert no fake hook call occurred.

2. `WaitForExpectedClusterManifestAsync_WhenHooksAreEmptyAndTimeoutIsZero_ReturnsTrue`
   - Pass the exact empty expected-silo array and `TimeSpan.Zero`.
   - Assert `true`; no background work or timer advancement is allowed.

3. `WaitForExpectedClusterManifestAsync_WhenAllHooksMatch_ReturnsTrueAndForwardsExactArguments`
   - Use two recording hooks which complete synchronously with `true`.
   - Assert `true`, one call per hook, sequence-equal expected `SiloAddress` values, and the exact forwarded timeout.

4. `WaitForExpectedClusterManifestAsync_WhenOneHookDoesNotMatch_ReturnsFalseAndInvokesEachHookOnce`
   - Return `true` and `false` from distinct hooks.
   - Assert `false`, exact one-call counts, and exact arguments for both hooks.

5. `WaitForExpectedClusterManifestAsync_WhenHookIsIncompleteAndTimeoutIsZero_ReturnsFalse`
   - Materialize an incomplete `TaskCompletionSource<bool>` before invoking the helper.
   - Assert exact `false` without advancing time; complete the source in `finally` so no task leaks.

#### 2. `LivenessStabilizationHelper.cs`

- **Source**: `src/Orleans.TestingHost/LivenessStabilizationHelper.cs`
- **Test File**: `test/TestInfrastructure/Orleans.TestingHost.Tests/LivenessStabilizationHelperTests.cs`
- **Test Class**: `LivenessStabilizationHelperTests`
- **Methods**: combined liveness stabilization, silo liveness stabilization, and gateway stabilization/observation entry points recorded in the source

**Planned tests**:

1. `WaitForSiloLivenessToStabilizeAsync_WhenHooksAreNull_ThrowsArgumentNullException`
   - Assert exact exception type and parameter name; assert zero calls.

2. `WaitForSiloLivenessToStabilizeAsync_WhenAllHooksMatch_ReturnsTrueAndForwardsExactArguments`
   - Assert `true`, one call per hook, sequence-equal active-silo addresses, and exact timeout forwarding.

3. `WaitForSiloLivenessToStabilizeAsync_WhenOneHookDoesNotMatch_ReturnsFalseAndInvokesEachHookOnce`
   - Assert exact `false` and exact per-hook calls/arguments.

4. `WaitForSiloLivenessToStabilizeAsync_WhenHooksAreEmptyAndTimeoutIsZero_ReturnsTrue`
   - Assert exact `true` with no scheduling or polling.

5. `WaitForLivenessToStabilizeAsync_WhenOptionalDirectoryCheckReturnsFalse_ReturnsFalse`
   - Use synchronously completed silo/manifest checks and a recording optional directory delegate.
   - Assert exact `false`, one directory-delegate call, and no calls beyond the helper’s documented short-circuit point.

6. `WaitForLivenessToStabilizeAsync_WhenEveryCheckSucceeds_ReturnsTrue`
   - Assert exact `true`; assert exact silo, gateway, manifest, and optional-directory invocation counts and address sets.

7. `WaitForGatewayLivenessToStabilizeAsync_WhenObservationIsAlreadyComplete_ReturnsTrue`
   - Use the existing gateway observation seam with an already-completed exact expected gateway set.
   - Assert exact `true`, expected gateway identities, and one observer disposal.

**Identifier gate**: research records the behavioral entry points but not every internal overload’s exact identifier/signature. During implementation, bind these tests to the existing internal members through `InternalsVisibleTo`; rename a planned test only to match the actual member terminology and record that rename in `.testagent/status.md`. Do not modify production visibility or behavior.

### Determinism and Cleanup

- Recording hooks store immutable copies of received address arrays and timeout values.
- Incomplete fake tasks are armed before the helper call and always completed in `finally`.
- No test advances fake time from more than one owner; zero-timeout cases require no advancement.
- No test asserts elapsed wall-clock duration.

### Phase Command

`dotnet test --project test/TestInfrastructure/Orleans.TestingHost.Tests/Orleans.TestingHost.Tests.csproj --framework net10.0 --filter-class "*StabilizationHelperTests*" --minimum-expected-tests 1 --max-parallel-test-modules 1`

### Success Criteria

- [ ] Both new helper test files compile.
- [ ] Null, empty, success, false, zero-timeout, and optional-directory paths have exact assertions.
- [ ] All Phase 1 tests pass under net10.

---

## Phase 2: Directory Observation and In-Process Lifecycle

### Overview

Exercise `GrainDirectoryObserver` through supported real directory implementations, then cover the highest-risk reachable `InProcessTestCluster` topology, restart, client, failure-cleanup, and disposal branches. Every test uses a fresh `await using` cluster. Static/global observers, hosted services, allocators, and barriers are unique per test.

### Files to Test

#### 1. `GrainDirectoryObserver.cs`

- **Source**: `src/Orleans.TestingHost/GrainDirectoryObserver.cs`
- **Test File**: `test/TestInfrastructure/Orleans.TestingHost.Tests/InProcessTestClusterDirectoryTests.cs`
- **Test Class**: `InProcessTestClusterDirectoryTests`
- **Methods**: `CanObserve`, `WaitForConvergenceAsync`, lifecycle event handling, and `HasConverged`

**Planned tests**:

1. `GrainDirectoryObserver_CanObserve_DefaultAndDistributedDirectories_ReturnsTrue`
   - Build fresh default-directory and distributed-directory clusters.
   - Assert exact `true` for each supported resolver result and exact observer disposal after each cluster.

2. `GrainDirectoryObserver_CanObserve_CustomDirectory_ReturnsFalse`
   - Configure the existing non-observable custom directory fixture.
   - Assert exact `false`; assert no convergence wait was registered.

3. `GrainDirectoryObserver_WaitForConvergenceAsync_DefaultDirectory_ReturnsExactActiveSiloSet`
   - Arm and materialize the directory transition wait before adding a silo.
   - Assert `true` and set-equality between observed owners and the cluster’s exact active `SiloAddress` set; assert no duplicate owners.

4. `GrainDirectoryObserver_WaitForConvergenceAsync_DistributedDirectory_ReturnsExactActiveSiloSet`
   - Use the same ordered barrier pattern with the distributed directory.
   - Assert `true`, exact owner set, and exact active-silo count after convergence.

5. `GrainDirectoryObserver_WaitForConvergenceAsync_AfterRestart_ReplacesOldOwnerWithReplacement`
   - Arm the observer before restart.
   - Assert the old address is absent, the replacement address is present exactly once, and the final observed set equals active cluster membership.

**Diagnostic barrier gate**: the corrected research does not record the literal `DiagnosticListener` event names or payload type/member names for directory and silo transitions. Do not invent them. Before implementing tests 3–5, identify the already-existing cluster-scoped event contract and use `DiagnosticEventCollector` with an event-name plus cluster/silo payload predicate; create and materialize the wait before the transition, and assert the exact collected event count and payload identity. If no existing event represents the required transition, record that exact absence as a blocker in `.testagent/status.md` and use the existing grain-context or hosted-lifecycle `TaskCompletionSource` barrier instead. Adding a production diagnostic event solely for these tests is prohibited.

#### 2. `InProcTestCluster.cs`

- **Source**: `src/Orleans.TestingHost/InProcTestCluster.cs`
- **Test File**: `test/TestInfrastructure/Orleans.TestingHost.Tests/InProcessTestClusterLifecycleTests.cs`
- **Test Class**: `InProcessTestClusterLifecycleTests`
- **Methods**: deployment/start, `RestartSiloAsync`, graceful stop, kill, client kill/stop, `WaitForTopologyToConvergeAsync`, synchronous/async disposal, and associated cleanup branches

**Planned tests**:

1. `WaitForTopologyToConvergeAsync_AfterAddingSilo_ReportsExactSilosGatewaysDirectoriesAndManifest`
   - Arm all existing diagnostic/context waits before `StartSiloAsync`.
   - Assert exact set equality for active silos, gateways, directory owners, and manifest members.
   - Assert one transition event per expected entity, using the diagnostic barrier gate above.

2. `WaitForTopologyToConvergeAsync_WithNonObservableCustomDirectory_ThrowsContextualInvalidOperationException`
   - Assert the exact `InvalidOperationException` type and the implementation’s full existing message, including the custom directory identity.
   - Assert cluster membership remains unchanged and cleanup disposes all handles once.
   - **Message blocker**: the literal message is absent from research and must be copied from the authoritative implementation during implementation, not guessed or changed.

3. `RestartSiloAsync_ReplacesActiveHandleAndPreservesSiloName`
   - Capture old handle, name, and membership before restart.
   - Assert old handle inactive/disposed once, replacement is a different instance, name is exactly preserved, collection count is unchanged, and the replacement alone occupies the old logical slot.

4. `StopSiloAsync_ActiveSilo_RemovesHandleAndDisposesItOnce`
   - Use a controlled hosted lifecycle service with `StopEntered` and `AllowStop` sources.
   - Materialize `StopEntered`, invoke stop, assert the operation is incomplete at the barrier, release once, then assert exact removal, inactive state, and one disposal.

5. `KillSilo_ActiveSilo_RemovesHandleAndDisposesItOnce`
   - Assert exact pre/post collection membership, inactive state, and disposal count.

6. `StartSiloAsync_WhenCreationIsCancelledAfterEndpointAllocation_ReleasesEndpointAndRetainsNoHandle`
   - Fixed allocator reserves a known endpoint; a controlled creation delegate signals `EndpointAllocated` and awaits `ContinueCreation`.
   - Cancel with the caller’s token after the barrier, release the delegate in `finally`, and assert the thrown cancellation carries that exact token.
   - Assert no retained silo handle, allocator release count exactly one, and a retry can use the same endpoint successfully.

7. `StartSiloAsync_WhenCreationFailsAfterEndpointAllocation_RethrowsOriginalExceptionAndAllowsEndpointReuse`
   - Throw a pre-created exception instance after `EndpointAllocated`.
   - Assert reference identity of the exception, zero retained handles, one endpoint release, and successful retry on the same endpoint.

8. `KillClientAsync_WhenClientIsRunning_ClearsStateAndDisposesHostOnce`
   - Assert exact precondition client identity.
   - After completion, assert `ClientHost` and public client state are cleared and the tracked host was disposed exactly once.

9. `KillClientAsync_WhenClientStopFails_RethrowsOriginalExceptionAndDisposesHostOnce`
   - Controlled stop signals `StopEntered`, then throws a pre-created non-cancellation exception after release.
   - Assert exception reference identity, cleared client state, and exactly one disposal.

10. `KillClientAsync_WhenCallerIsCancelled_ClearsStateAndDisposesHostOnce`
    - Cancel only after the materialized `StopEntered` barrier.
    - Assert exact caller token on cancellation, cleared client state, and one disposal; release all barriers in `finally`.

11. `Dispose_WhenCalledTwice_PerformsCleanupExactlyOnce`
    - Start a cluster with recording allocator, observer, client host, and silo handles.
    - Call synchronous dispose twice; assert each component’s disposal/release count is exactly one and all public collections/state are empty.

12. `DisposeAsync_AfterDispose_PerformsNoAdditionalCleanup`
    - Call synchronous dispose followed by async dispose.
    - Assert exact unchanged disposal/release counts and empty post-disposal state.

Existing startup-failure, stop-failure, leak, client-availability, and merged diagnostics tests remain unchanged unless a shared helper must be reused. Do not duplicate them.

### Barrier and Cleanup Protocol

| Scenario | Barrier armed before action | Exact completion evidence | Cleanup |
|---|---|---|---|
| Add/restart topology | Materialized cluster-scoped diagnostic wait, or existing context TCS under the diagnostic gate | Exact event payload identity and exact topology sets | `await using` cluster; collector disposed in `finally` |
| Graceful stop | `StopEntered.Task` | Stop task is incomplete before `AllowStop`; exact state after release | `AllowStop.TrySetResult()` in `finally` |
| Creation fail/cancel | `EndpointAllocated.Task` | Original exception identity or exact cancellation token; allocator counts | `ContinueCreation.TrySetResult()` and cluster disposal in `finally` |
| Client stop/kill | `StopEntered.Task` | Exact exception/token and one disposal | Release source in `finally`; idempotent host disposal |
| Every cluster test | Fresh fixture/cluster | Empty or exact final collections and exact disposal counters | `await using`; no shared mutable fixture state |

### Phase Command

- Directory tests: `dotnet test --project test/TestInfrastructure/Orleans.TestingHost.Tests/Orleans.TestingHost.Tests.csproj --framework net10.0 --filter-class "*InProcessTestClusterDirectoryTests*" --minimum-expected-tests 1 --max-parallel-test-modules 1`
- Lifecycle tests: `dotnet test --project test/TestInfrastructure/Orleans.TestingHost.Tests/Orleans.TestingHost.Tests.csproj --framework net10.0 --filter-class "*InProcessTestClusterLifecycleTests*" --minimum-expected-tests 1 --max-parallel-test-modules 1`

### Success Criteria

- [ ] Supported observer paths converge to exact sets without sleep or polling.
- [ ] Unsupported observation fails with the exact contextual exception.
- [ ] Restart, stop, kill, creation failure/cancellation, client cleanup, and disposal have exact identity/count/state assertions.
- [ ] All Phase 2 tests pass under net10.

---

## Phase 3: `TestCluster` Lifecycle with Fake Handles

### Overview

Use the existing silo-creation delegate and hand-written recording `SiloHandle` fakes to cover out-of-process-facing orchestration without launching unnecessary external processes. Base/fake handle behavior is established before restart and cleanup assertions. Do not add production seams.

### Files to Test

#### 1. `TestCluster.cs`

- **Source**: `src/Orleans.TestingHost/TestCluster.cs`
- **Test File**: `test/TestInfrastructure/Orleans.TestingHost.Tests/TestClusterTests.cs`
- **Test Class**: `TestClusterTests`
- **Methods**: deploy/start, active-silo selection, primary/secondary restart, graceful stop, kill, client stop/kill, topology stabilization, and disposal

**Planned tests**:

1. `GetActiveSilos_ReturnsOnlyActiveHandlesInClusterOrder`
   - Supply active and inactive recording handles in a known order.
   - Assert exact returned handle sequence, exact count, and exclusion of every inactive identity.

2. `RestartSiloAsync_Primary_ReplacesHandleAndPreservesPrimaryIdentity`
   - Assert old primary inactive/disposed once, replacement reference differs, silo name and primary role are exactly preserved, and only replacement remains in active membership.

3. `RestartSiloAsync_Secondary_ReplacesHandleAndPreservesSiloName`
   - Assert exact name, collection index/membership, active count, old-handle disposal count, and replacement identity.

4. `StopSiloAsync_ActiveHandle_StopsRemovesAndDisposesExactlyOnce`
   - Recording handle exposes `StopEntered`/`AllowStop`.
   - Assert operation is incomplete at the armed barrier; after release assert exact stop count, removal, inactive state, and one disposal.

5. `KillSilo_ActiveHandle_KillsRemovesAndDisposesExactlyOnce`
   - Assert exact kill count, zero graceful-stop calls, exact removal, inactive state, and one disposal.

6. `StartSiloAsync_WhenCreationFails_RethrowsOriginalExceptionAndRetainsNoHandle`
   - Creation delegate signals `CreationEntered`, then throws a pre-created exception.
   - Assert exception reference identity, unchanged active-handle sequence, and no disposal attempt on a handle which was never returned.

7. `StartSiloAsync_WhenCreationIsCancelled_RethrowsCallerCancellationAndRetainsNoHandle`
   - Cancel after the materialized `CreationEntered` barrier.
   - Assert exact cancellation token, unchanged handle sequence, and release the delegate in `finally`.

8. `WaitForTopologyToConvergeAsync_WithRecordingHooks_ForwardsExactMembershipAndTimeout`
   - Use synchronously completing recording hooks.
   - Assert exact active-silo address sequence, exact gateway/directory/manifest sets, one call per hook, and exact configured timeout.

9. `KillClientAsync_WhenClientStopFails_RethrowsOriginalExceptionAndClearsClientState`
   - Assert original exception identity, `ClientHost`/public client state cleared, and one tracked-host disposal.

10. `KillClientAsync_WhenCallerIsCancelled_ClearsClientStateAndDisposesHostOnce`
    - Cancel after `StopEntered`; assert exact token, cleared state, and one disposal.

11. `Dispose_WhenCalledTwice_DisposesClientHandlesAllocatorAndObserverExactlyOnce`
    - Assert exact per-component counts, empty silo collections, cleared client state, and no duplicate work.

12. `DisposeAsync_AfterDispose_PerformsNoAdditionalCleanup`
    - Assert all counts remain exactly one and post-disposal state remains empty.

Existing repeated deploy/configuration, client availability, and startup failure tests provide the baseline public deploy/start coverage and must not be cloned. Shared fake helpers may be factored within `TestClusterTests.cs` only when they improve exact call tracking.

### Determinism and Cleanup

- Every recording handle has exact stop, kill, and dispose counters plus explicit entered/release sources.
- All lazy wait collections are materialized before invoking the action.
- `finally` releases every fake handle or hosted-service barrier.
- Cleanup assertions use exact counts and object identity; there are no elapsed-time assertions.

### Phase Command

`dotnet test --project test/TestInfrastructure/Orleans.TestingHost.Tests/Orleans.TestingHost.Tests.csproj --framework net10.0 --filter-class "*TestClusterTests*" --minimum-expected-tests 1 --max-parallel-test-modules 1`

### Success Criteria

- [ ] Primary and secondary replacement contracts are exact.
- [ ] Active selection, stop/kill, creation failure/cancellation, topology forwarding, client cleanup, and idempotent disposal pass.
- [ ] All Phase 3 tests pass under net10.

---

## Phase 4: Narrow Validation and Mandatory Reviews

### Sequential Validation

1. Run the narrow net10 build command.
2. Run the discovery command and verify every planned test name appears exactly once.
3. Run the full `Orleans.TestingHost.Tests` net10 command with one test module.
4. If fixes are required, rerun the narrowest affected class command, then repeat steps 1–3.
5. Do not run a full-solution build or final coverage collection in this task.

### Mandatory Skill Reviews

1. Invoke **`test-gap-analysis`**, scoped to:
   - the five target source files;
   - `ClusterManifestStabilizationHelperTests.cs`;
   - `LivenessStabilizationHelperTests.cs`;
   - changed tests in `InProcessTestClusterDirectoryTests.cs`, `InProcessTestClusterLifecycleTests.cs`, and `TestClusterTests.cs`.
2. Record every finding and its disposition in `.testagent/status.md`. Fix reachable lifecycle gaps; explicitly document blockers rather than adding production-only seams.
3. Invoke **`assertion-quality`** on every newly added/changed test method.
4. Record findings/fixes in `.testagent/status.md`; replace presence, truthiness, range, self-referential, or cleanup-free assertions with exact values, identities, sets, exceptions/tokens, and counts.
5. Repeat the affected class test and final narrow build/full-project net10 test after review fixes.

### Success Criteria

- [ ] Narrow build succeeds with no warnings.
- [ ] Discovery lists every new test exactly once.
- [ ] All `Orleans.TestingHost.Tests` tests pass on net10.
- [ ] Both mandatory skill reviews are recorded in `.testagent/status.md`, including fixes and explicit blockers.
- [ ] No full solution build or final coverage collection was run by this task.

---

## Acceptance Requirement Traceability

The final result must include a compact `Requirement | Evidence` table quoting these same requirements and replacing planned evidence with completed evidence.

| Requirement (verbatim) | Exact planned evidence |
|---|---|
| **1.** “Measure gaps after merged TestingHost diagnostics work and choose the strongest next coherent slice, prioritizing cluster lifecycle branches, TestKit contracts, or obsolete StorageEmulator/Azurite behavior. The selected slice is cluster lifecycle based on the measured evidence above.” | This plan assigns all five measured lifecycle targets to Phases 1–3. Highest-risk tests include `GrainDirectoryObserver_WaitForConvergenceAsync_AfterRestart_ReplacesOldOwnerWithReplacement`, `GetActiveSilos_ReturnsOnlyActiveHandlesInClusterOrder`, and both helper suites. `StorageEmulator.cs`, logging tests, and diagnostic collector tests are explicitly excluded. |
| **2.** “Generate deterministic tests with explicit cleanup and diagnostic assertions. Follow test\AGENTS.md: isolate mutable state, use explicit barriers/DiagnosticEventCollector rather than sleeps or polling, arm waits before actions, and make timeout failures contextual.” | The Phase 2 barrier table specifies materialized waits before every action, exact event payload/count assertions, fresh clusters, `finally` releases, and `await using`. Applies directly to `WaitForTopologyToConvergeAsync_AfterAddingSilo_ReportsExactSilosGatewaysDirectoriesAndManifest`, the three observer convergence tests, both creation cleanup tests, and all stop/client tests. Literal diagnostic event identifiers are the explicit diagnostic barrier gate because research does not contain them; no event will be invented or added solely for tests. |
| **3.** “Cover meaningful success/failure/cleanup branches across in-process and/or out-of-process cluster lifecycle, with exact assertions. Target the highest-value reachable branches without changing production behavior solely for tests.” | Success: both helper `...ReturnsTrue...` tests and topology convergence. Failure: `...ReturnsFalse...`, unsupported directory, original-exception, and exact caller-cancellation tests. Cleanup: `RestartSiloAsync_ReplacesActiveHandleAndPreservesSiloName`, both `StartSiloAsync_WhenCreation...` tests, client cleanup tests, and sync/async disposal tests in `InProcessTestClusterLifecycleTests.cs` and `TestClusterTests.cs`. |
| **4.** “Keep changes surgical and behavior-preserving. Prefer testing existing internals through InternalsVisibleTo and existing test infrastructure. Do not add broad catches, weak assertions, timing ranges, arbitrary delays, or skip attributes.” | Tests are confined to `test/TestInfrastructure/Orleans.TestingHost.Tests`; helper fakes use existing internal access, creation delegates, controlled hosted services, allocator, and `DiagnosticEventCollector`. The phase criteria prohibit production edits, catches except exact expected cleanup handling, timing assertions, sleeps/polls, and skips. Diagnostic/message gaps become `.testagent/status.md` blockers rather than production changes. |
| **5.** “Run the narrowest net10 test command during implementation. Ensure generated tests compile and pass cleanly.” | Each phase has an exact `--framework net10.0 --filter-class ... --minimum-expected-tests 1 --max-parallel-test-modules 1` command. Phase 4 runs the exact narrow build, discovery, and full test-project net10 commands. |
| **6.** “Perform final test-gap-analysis and assertion-quality skill reviews and record findings/fixes in .testagent/status.md.” | Phase 4 mandates `test-gap-analysis` over the five sources and changed tests, then `assertion-quality` over every changed test method, with all findings, fixes, and blockers recorded in `.testagent/status.md` before final narrow validation. |
| **7.** “Provide a compact Requirement \| Evidence table in your result, quoting the user requirements verbatim and citing exact test names/commands.” | The implementation result must reproduce this seven-row compact table, replace plans with actual test names/files/results, and cite the exact narrow build, discovery, phase-filtered, and full-project net10 commands. |

## Explicit Scope and Blockers

- `StorageEmulator.cs` remains out of scope because live references exist.
- Merged diagnostics/logging tests are not duplicated.
- Exact diagnostic event and payload identifiers, and the unsupported-directory exception’s literal message, are not present in research. They must be read from authoritative existing implementation/contracts during implementation and recorded in status; they must not be guessed.
- If a desired transition has no existing observable event, context, hook, delegate, or controlled hosted-service barrier, record the branch as unreachable in `.testagent/status.md`. Do not introduce production behavior solely to test it.
- No test may use arbitrary sleep/polling, timing ranges, broad catches, weak assertions, or skip attributes to bypass a blocker.
