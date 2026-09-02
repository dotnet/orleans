# Test Generation Research

## Project Overview
- **Path**: `C:\dev\copilot-worktrees\orleans\rb-issue-10864-test-testing-raise-testinghost-and-test-009927`
- **Issue**: dotnet/orleans [#10864](https://github.com/dotnet/orleans/issues/10864), “test(testing): raise TestingHost and test-kit coverage above 90%”
- **Language**: C# on .NET
- **Framework**: Orleans TestingHost
- **Test Framework**: xUnit v3 on Microsoft Testing Platform (MTP)
- **Project system**: SDK-style
- **SDK / targets**: `global.json` requests SDK `10.0.400` with major roll-forward and MTP; tests target `net8.0;net10.0`. This request is restricted to `net10.0`.
- **Dependency format and versions**: Central Package Management (`Directory.Packages.props`); `xunit.v3.mtp-v2` 3.2.2, MTP extensions 2.3.3, `Microsoft.Extensions.TimeProvider.Testing` 9.10.0, and `AwesomeAssertions` 9.3.0 through `TestExtensions`. No mocking library is directly referenced by `Orleans.TestingHost.Tests`.
- **New-file registration**: implicit SDK `Compile` glob; no explicit `<Compile Include>` is required.
- **Workspace constraint**: workspace contents are authoritative. Do not restore, reset, clean, or reconstruct.

## Dependency Graph
- **In-scope interfaces**: none are declared by the five targets.
- **External seams**: `ITestHooks`, `ITestClusterPortAllocator`, `IHost`/hosted lifecycle services, `IClusterMembershipService`, `GrainDirectoryResolver`, `GatewayManager`, silo creation delegates, and `SiloHandle`.
- **Leaf types** (no in-scope dependencies): `GrainDirectoryObserver`, `LivenessStabilizationHelper`, `ClusterManifestStabilizationHelper`.
- **Mid-layer types**: none.
- **Top-layer types**: `TestCluster` and `InProcessTestCluster`; both use all three leaf helpers for topology stabilization and own host/client/silo cleanup.
- **Testing order**: test helper contracts directly with hand-written `ITestHooks`/`SiloHandle` fakes where possible, then exercise observer and orchestration behavior through real in-process clusters. Do not expose new production seams solely for tests.

## Build & Test Commands
Run from the repository root:

- **Build (narrow, net10)**: `dotnet build test/TestInfrastructure/Orleans.TestingHost.Tests/Orleans.TestingHost.Tests.csproj --framework net10.0`
- **Test (scoped — fix cycles)**: `dotnet test --project test/TestInfrastructure/Orleans.TestingHost.Tests/Orleans.TestingHost.Tests.csproj --framework net10.0 --minimum-expected-tests 1 --max-parallel-test-modules 1`
- **Test (harness-equivalent — discovery check)**: `dotnet test --project test/TestInfrastructure/Orleans.TestingHost.Tests/Orleans.TestingHost.Tests.csproj --framework net10.0 --list-tests --minimum-expected-tests 1 --max-parallel-test-modules 1`
- **Optional single-class loop**: `dotnet test --project test/TestInfrastructure/Orleans.TestingHost.Tests/Orleans.TestingHost.Tests.csproj --framework net10.0 --filter-class "*InProcessTestClusterLifecycleTests*" --minimum-expected-tests 1`
- **Lint**: no separate command; the build enables code-style enforcement and treats warnings (including nullable warnings) as errors.
- **Command evidence**: `CONTRIBUTING.md` documents the `dotnet test --project`, `--framework net10.0`, filter, minimum-test, and single-module conventions; SDK help confirms `--list-tests`. The prohibition on restoring the workspace concerns version-control restoration, not normal package restore performed by `dotnet build`/`dotnet test`.

## Scope
- **Boundary**: broad test generation only for Orleans.TestingHost cluster lifecycle represented by the five files below. Other TestingHost/TestKit code is not an implementation target.
- **Targets**:
  - `src/Orleans.TestingHost/TestCluster.cs`
  - `src/Orleans.TestingHost/InProcTestCluster.cs`
  - `src/Orleans.TestingHost/GrainDirectoryObserver.cs`
  - `src/Orleans.TestingHost/LivenessStabilizationHelper.cs`
  - `src/Orleans.TestingHost/ClusterManifestStabilizationHelper.cs`
- **Representative existing tests**:
  - `test/TestInfrastructure/Orleans.TestingHost.Tests/InProcessTestClusterLifecycleTests.cs`
  - `test/TestInfrastructure/Orleans.TestingHost.Tests/TestClusterTests.cs`
- **Directly relevant infrastructure**: `SiloHandle.cs`, `InProcessSiloHandle.cs`, `BaseInProcessTestClusterFixture.cs`, `TestTraits.cs`, `testconfig.json`, builders/options, `ITestHooks`, `DiagnosticEventCollector`, and the existing controlled hosted-service/port-allocator test doubles.
- **Out of scope**: logging implementation/tests, TestKit packages, provider suites, standalone-host internals, and `StorageEmulator.cs`. Repository-wide `git grep` found live consumers in `test/TestInfrastructure/TestExtensions/TestUtils.cs` and `test/Extensions/Orleans.AdoNet.Tests/StorageTests/Relational/TestEnvironmentInvariant.cs`, plus the shipped API declaration; this slice therefore does not prove it obsolete and must not remove or test it.

### Previously completed static pairing
Do **not** invoke `find-untested-sources` again. Record of the completed Roslyn run:

- Scanned the repository root once: **2,842 source files**, **1,034 test files**, **1,546 unpaired**, **1,296 paired**.
- `TestCluster.cs` paired to `TestClusterTests.cs` plus integration infrastructure.
- `InProcTestCluster.cs` paired to `InProcessTestClusterLifecycleTests.cs`, `DirectoryTests.cs`, `SiloDisposalLeakTests.cs`, and fixtures. The authoritative workspace has no literal `DirectoryTests.cs`; the current directly relevant file is `InProcessTestClusterDirectoryTests.cs`.
- `GrainDirectoryObserver.cs` paired only to unrelated `ProviderRegistrationResolverTests.cs`.
- The liveness, cluster-manifest, and `StorageEmulator` files appeared in neither `source_to_tests` nor the analyzer’s truncated top-50 unpaired output, so their static classification was inconclusive.
- This is a **static name/reference pairing heuristic, not coverage evidence**.

## Measured Baseline Evidence
Artifacts are dated 2026-09-01 and cover one test project with **62 passed, 0 failed**.

- `TestResults/coverage-analysis/coverage-analysis.md`: overall **52.4% line**, **44.8% branch**, 602 methods, and 25 CRAP hotspots above 30.
- `testinghost-baseline.cobertura.xml`: **2,088/3,987 lines = 52.37%** and **473/1,055 branches = 44.83%**.

| Target | Markdown gap evidence | Raw Cobertura evidence |
|---|---:|---:|
| `TestCluster.cs` | 340 uncovered lines; below 90%/70% | 340/673 lines (50.52%); 75/186 branches (40.32%); 333 unique uncovered line elements |
| `InProcTestCluster.cs` | 279 uncovered lines; below 90%/70% | 398/670 lines (59.40%); 84/174 branches (48.28%); 272 unique uncovered line elements |
| `GrainDirectoryObserver.cs` | 80 uncovered lines; below 90%/70% | 43/121 lines (35.54%); 11/42 branches (26.19%); 78 unique uncovered line elements |
| `LivenessStabilizationHelper.cs` | 84 uncovered lines; below 90%/70% | 0/81 lines and 0/28 branches |
| `ClusterManifestStabilizationHelper.cs` | hotspot listed, but not in the summary’s top-ten file table | 0/16 lines and 0/6 branches |

The markdown gap counts and unique Cobertura line-element counts differ slightly; preserve both rather than presenting one as the other. Highest measured risks are `GrainDirectoryObserver.HasConverged` (complexity 16, 0%, CRAP 272), `TestCluster.GetActiveSilos` (12, 0%, 156), `TestCluster.KillClientAsync` (10, 0%, 110), `InProcessTestCluster.WaitForTopologyToConvergeAsync` (10, 0%, 110), `InProcessTestCluster.Dispose` (8, 0%, 72), and both stabilization waits (complexity 6, 0%, CRAP 42).

## Files to Test

### High Priority
| File | Classes/Functions | Testability | Estimated Coverage | Notes |
|---|---|---|---|---|
| `GrainDirectoryObserver.cs` | `CanObserve`, `WaitForConvergenceAsync`, event handling, `HasConverged` | Medium | Partial/incidental | Top CRAP hotspot. Prefer real default/distributed directory events and exact convergence outcomes; concrete silo handles and internal runtime event payloads make synthetic unit injection unsuitable. |
| `LivenessStabilizationHelper.cs` | combined wait, silo wait, gateway wait/observer | Medium | Untested | Directly test silo success, false, zero-timeout, null, and optional-directory short-circuit paths with fakes; cover gateway success through cluster integration. |
| `ClusterManifestStabilizationHelper.cs` | `WaitForExpectedClusterManifestAsync` | High | Untested | Deterministic direct success, false result, zero-timeout, empty, and null contracts using hand-written fakes. |
| `InProcTestCluster.cs` | deploy/start/restart/stop/kill/client/dispose and stabilization | Medium | Partial | Existing failure cleanup is strong, but restart, kill-client, topology helpers, and several success/cancellation branches remain at 0%. |
| `TestCluster.cs` | deploy/start/restart/stop/kill/client/dispose and stabilization | Medium | Partial | Existing tests emphasize repeated startup/configuration. Silo creation delegates and fake `SiloHandle` instances provide deterministic lifecycle seams. |

### Medium Priority
None within the bounded inventory; the five selected files all have measured lifecycle gaps or hotspots.

### Low Priority / Skip
| File | Reason |
|---|---|
| `src/Orleans.TestingHost/Utils/StorageEmulator.cs` | Explicitly outside this lifecycle slice; live repository references do not prove obsolescence. |
| `InMemoryLoggerProvider*`, `DiagnosticEventCollector*` | Already covered by merged #10883 and outside the requested lifecycle targets. |

## Existing Tests & Coverage Classification
- `TestCluster.cs` → `TestClusterTests.cs`, `ClientLifecycleTests.cs`: **partial**. Sixteen `TestClusterTests` facts mostly repeat happy-path deploy/stop and configurator behavior; client availability/startup failure is covered, while measured restart/kill/topology paths remain uncovered.
- `InProcTestCluster.cs` → `InProcessTestClusterLifecycleTests.cs`, `InProcessTestClusterDirectoryTests.cs`, `SiloDisposalLeakTests.cs`, `ClientLifecycleTests.cs`, `HostShutdownCallbackTests.cs`: **partial**. Existing tests strongly assert one-silo startup failure, client startup failure, stop failure, disposal/leak cleanup, and client availability, but many lifecycle success/restart/kill/stabilization branches remain uncovered.
- `GrainDirectoryObserver.cs` → no directly paired behavioral test: **partial only by incidental runtime execution** (35.54% lines); `HasConverged`, `WaitForConvergenceAsync`, `CanObserve`, error, and target-creation paths were 0% in the baseline. The analyzer’s `ProviderRegistrationResolverTests.cs` pairing is unrelated.
- `LivenessStabilizationHelper.cs` → no existing direct test: **untested** (0/81 lines).
- `ClusterManifestStabilizationHelper.cs` → `test/Orleans.Runtime.Internal.Tests/Manifest/ClusterManifestStabilizationTests.cs` exercises the public cluster API in a different project, but the collected TestingHost baseline shows **untested** (0/16 lines) for this project.

## Existing Test Projects
- **Project file**: `test/TestInfrastructure/Orleans.TestingHost.Tests/Orleans.TestingHost.Tests.csproj`
- **Target source project**: references `src/Orleans.TestingHost/Orleans.TestingHost.csproj` indirectly through `TestExtensions.csproj`; TestingHost grants this test assembly internal visibility.
- **Directly relevant test files**:
  - `TestClusterTests.cs`
  - `InProcessTestClusterLifecycleTests.cs`
  - `InProcessTestClusterDirectoryTests.cs`
  - `SiloDisposalLeakTests.cs`
  - `ClientLifecycleTests.cs`
  - `HostShutdownCallbackTests.cs`
- **Cross-project behavioral reference**: `test/Orleans.Runtime.Internal.Tests/Manifest/ClusterManifestStabilizationTests.cs`.

## Testing Patterns
- Use xUnit v3 `[Fact]`, `TestContext.Current.CancellationToken`, and class/method traits: `[TestSuite("Functional"|"BVT")]`, `[TestProvider("None")]`, `[TestArea("TestingHost")]`, and `[TestCategory(...)]`.
- Use a fresh cluster per test and `await using`; make cleanup idempotence and post-failure state explicit. `testconfig.json` disables collection parallelization, but tests must still isolate static/global observers.
- Follow `test/AGENTS.md`: arm and materialize event/barrier waits before the action; use one orchestrator for time; prefer DiagnosticListener events as phase barriers; assert exact state, identity, counts, diagnostics, and cleanup.
- Reuse the controlled `IHostedLifecycleService`, `TaskCompletionSource` barriers, disposal tracker, and fixed allocator pattern from `InProcessTestClusterLifecycleTests.cs`. Hand-write small `ITestHooks` and `SiloHandle` fakes instead of adding a mocking dependency.
- Do not add arbitrary delays or polling, broad catches, weak/presence-only assertions, or skip attributes. Use `try/finally` solely to release barriers and dispose resources; if an expected cleanup exception must be handled, match its exact instance/type.

## Highest-Value Deterministic Branches
1. **Direct leaf contracts**: helper null checks; all-hooks-true; one-hook-false; already-incomplete task with zero timeout; empty active-silo set with zero timeout; optional grain-directory delegate false/success. Assert exact calls, expected silo address arrays, and propagated timeout values.
2. **Observer/topology success**: on a fresh real in-process cluster, explicitly call stabilization for the default directory and distributed directory, then add/restart a silo and assert exact active silo, gateway, directory, and manifest sets. Arm diagnostics before the transition; no sleeps.
3. **Unsupported observer path**: configure a non-observable custom default directory and assert `WaitForTopologyToConvergeAsync` throws the exact contextual `InvalidOperationException`.
4. **Restart success**: primary versus secondary for `TestCluster`, and active handle replacement for `InProcessTestCluster`; assert old handle inactive/disposed, instance/name preservation, exact collection membership, and functioning replacement.
5. **Silo creation failure/cancellation cleanup**: use the existing creation/lifecycle delegate plus barriers to fail after endpoint allocation, assert the original exception/cancellation and no retained handle, then retry the same deterministic allocation to prove endpoint cleanup.
6. **Client stop/kill cleanup**: exact success, non-cancellation stop failure behavior, and caller-cancellation behavior; in every case assert `ClientHost`/public client state and one-time disposal.
7. **Idempotent sync/async disposal and empty/null branches**: exact allocator/observer/handle disposal counts, no duplicate work, and precise post-disposal state.
8. **Pure timeout computation**: cover kill/non-kill crossed with gossip/table-refresh options for both cluster types and assert exact `TimeSpan` values.

Do not duplicate the already merged work: #10883 added `InProcessTestClusterLifecycleTests`, logger tests, and diagnostic collector tests; #10929 added synchronized concurrent cluster logging and its test; #10945 scoped diagnostic observers to individual test clusters and changed both cluster classes. Extend only genuinely uncovered lifecycle behavior.

## Acceptance Checklist
Each item below preserves the user's request verbatim and remains independently auditable.

- [ ] **1.** “Measure gaps after merged TestingHost diagnostics work and choose the strongest next coherent slice, prioritizing cluster lifecycle branches, TestKit contracts, or obsolete StorageEmulator/Azurite behavior. The selected slice is cluster lifecycle based on the measured evidence above.”
- [ ] **2.** “Generate deterministic tests with explicit cleanup and diagnostic assertions. Follow test\AGENTS.md: isolate mutable state, use explicit barriers/DiagnosticEventCollector rather than sleeps or polling, arm waits before actions, and make timeout failures contextual.”
- [ ] **3.** “Cover meaningful success/failure/cleanup branches across in-process and/or out-of-process cluster lifecycle, with exact assertions. Target the highest-value reachable branches without changing production behavior solely for tests.”
- [ ] **4.** “Keep changes surgical and behavior-preserving. Prefer testing existing internals through InternalsVisibleTo and existing test infrastructure. Do not add broad catches, weak assertions, timing ranges, arbitrary delays, or skip attributes.”
- [ ] **5.** “Run the narrowest net10 test command during implementation. Ensure generated tests compile and pass cleanly.”
- [ ] **6.** “Perform final test-gap-analysis and assertion-quality skill reviews and record findings/fixes in .testagent/status.md.”
- [ ] **7.** “Provide a compact Requirement | Evidence table in your result, quoting the user requirements verbatim and citing exact test names/commands.”

## Recommendations
- Prioritize direct helper contracts and observer/topology convergence first, then restart/client cleanup branches in `InProcessTestCluster`, then use `TestCluster`’s creation delegate and fake handles for equivalent out-of-process-facing orchestration.
- Add tests only under `Orleans.TestingHost.Tests`; production behavior changes are not justified by this request.
- Re-collect coverage after tests are discoverable and compare the five target files and named hotspot methods against the raw baseline; do not infer achievement of the issue-wide 90% gate from this bounded project alone.
- Normal package restore is permitted when required by the scoped build/test commands; version-control restoration and workspace reconstruction remain prohibited.
