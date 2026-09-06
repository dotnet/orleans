# Test Implementation Plan

## Overview

Add concise, representative coverage for the shared generic DI/`ActivatorUtilities` constructor-preservation invariant in the ten bounded `Orleans.Core` and `Orleans.Runtime` candidates. Implement leaf-style direct DI activation tests first, then Runtime factory/deferred-activation tests, then a separate self-contained linked smoke executable. The smoke proves only that the selected generic DI constructor flows survive trimming; it does not claim full Orleans Runtime, NativeAOT, or general reflection-path safety.

Use the existing xUnit v3/Microsoft Testing Platform conventions, constructor-injected marker probes, `Assert.Same` identity assertions, disposable service providers, and small fake builders/recording lifecycle objects. Do not start a cluster, mock DI, instantiate the full default silo graph, inspect attributes as behavioral proof, or test every production caller.

### Strict Change Boundary

Implementation changes are limited to:

- `test/Orleans.Core.Tests/DependencyInjection/GenericRegistrationTests.cs`
- `test/Orleans.Runtime.Internal.Tests/DependencyInjection/GenericRegistrationTests.cs`
- `test/Orleans.DependencyInjection.TrimmedSmoke/Orleans.DependencyInjection.TrimmedSmoke.csproj`
- `test/Orleans.DependencyInjection.TrimmedSmoke/Program.cs`
- `Orleans.slnx`
- `.github/workflows/analyzer-audit.yml`

The `.testagent` research/plan/status artifacts are coordination and quality-report artifacts, not product implementation. Do not modify production files, existing smoke files, or root analyzer configuration. Before implementation and again before final reporting, compare `git diff --name-only` with this allowlist and report any pre-existing/concurrent differences without restoring, resetting, cleaning, reverting, or deleting them.

## Commands

Run from the repository root in PowerShell.

- **Build — Orleans.Core compatibility analyzer**:
  ```powershell
  dotnet build src/Orleans.Core/Orleans.Core.csproj --framework net10.0 --configuration Release --no-incremental -p:EnableCompatibilityAnalyzerAudit=true -p:CustomBeforeMicrosoftCommonTargets="$PWD/.github/scripts/analyzer-audit.targets" -p:WarningsAsErrors="IL2087;IL2091"
  ```
- **Build — Orleans.Runtime compatibility analyzer**:
  ```powershell
  dotnet build src/Orleans.Runtime/Orleans.Runtime.csproj --framework net10.0 --configuration Release --no-incremental -p:EnableCompatibilityAnalyzerAudit=true -p:CustomBeforeMicrosoftCommonTargets="$PWD/.github/scripts/analyzer-audit.targets" -p:WarningsAsErrors="IL2087;IL2091"
  ```
- **Test — Orleans.Core scoped project**:
  ```powershell
  dotnet test --project test/Orleans.Core.Tests/Orleans.Core.Tests.csproj --framework net10.0 --configuration Release --minimum-expected-tests 1
  ```
- **Test — Orleans.Runtime scoped project**:
  ```powershell
  dotnet test --project test/Orleans.Runtime.Internal.Tests/Orleans.Runtime.Internal.Tests.csproj --framework net10.0 --configuration Release --minimum-expected-tests 1
  ```
- **Discovery — solution harness**:
  ```powershell
  dotnet test --solution Orleans.slnx --configuration Release --framework net10.0 --list-tests
  ```
- **Publish/run — clean Windows self-contained trimmed smoke**:
  ```powershell
  dotnet clean test/Orleans.DependencyInjection.TrimmedSmoke/Orleans.DependencyInjection.TrimmedSmoke.csproj --framework net10.0 --configuration Release --runtime win-x64
  dotnet publish test/Orleans.DependencyInjection.TrimmedSmoke/Orleans.DependencyInjection.TrimmedSmoke.csproj --framework net10.0 --configuration Release --runtime win-x64 --self-contained true --output Artifacts/Orleans.DependencyInjection.TrimmedSmoke/win-x64
  & .\Artifacts\Orleans.DependencyInjection.TrimmedSmoke\win-x64\Orleans.DependencyInjection.TrimmedSmoke.exe
  ```
- **Publish/run — CI-equivalent Linux self-contained trimmed smoke**:
  ```powershell
  $output = Join-Path $env:RUNNER_TEMP 'orleans-dependency-injection-trimmed-smoke'
  dotnet publish test/Orleans.DependencyInjection.TrimmedSmoke/Orleans.DependencyInjection.TrimmedSmoke.csproj --framework net10.0 --configuration Release --runtime linux-x64 --self-contained true --output $output
  & (Join-Path $output 'Orleans.DependencyInjection.TrimmedSmoke')
  ```
- **Lint**: No separate scoped lint command exists. The builds enforce repository analyzers and code style via `EnforceCodeStyleInBuild=true` and `TreatWarningsAsErrors=true`.

## Phase Summary

| Phase | Focus | Bounded Source Files | Implementation Files | Est. Checks |
|---|---|---:|---:|---:|
| 1 | Orleans.Core direct generic DI activation | 4 | 1 test file | 4 facts |
| 2 | Orleans.Runtime DI, factory, alias, and deferred activation | 6 | 1 test file | 6 facts |
| 3 | Unrooted self-contained trimmed smoke | 0 | 2 smoke files | 3 self-checks |
| 4 | Solution/CI wiring and final quality gate | 0 | 2 wiring files | discovery, 2 analyzer builds, publish/run, 3 reviews |

Each of the ten bounded production source files is assigned to exactly one implementation phase. Existing substantially covered behavior is not duplicated beyond the constructor-preservation assertion needed for this request.

---

## Phase 1: Orleans.Core Direct Generic DI Activation

### Overview

Establish the repository assertion pattern using cheap `ServiceCollection`-based tests. Every implementation probe has a public constructor accepting the same marker instance registered in DI. Resolve the registered abstraction (and keyed strategy/director where applicable) and use exact type plus `Assert.Same` assertions so descriptor-only registration cannot satisfy the test.

### Test File

- **Test File**: `test/Orleans.Core.Tests/DependencyInjection/GenericRegistrationTests.cs`
- **Test Class**: `UnitTests.DependencyInjection.CoreGenericRegistrationTests`
- **Style**: xUnit v3 `[Fact]` with the repository's BVT metadata; `using var` service providers; a minimal fake `IClientBuilder` backed by `ServiceCollection` and empty configuration.

### Files to Test

#### 1. `PlacementFilterExtensions.cs`

- **Source**: `src/Orleans.Core/Placement/PlacementFilterExtensions.cs`
- **Method**: `AddPlacementFilter<TFilter, TDirector>`
- **Exact Test**: `UnitTests.DependencyInjection.CoreGenericRegistrationTests.PlacementFilter_ActivatesConstructorInjectedDirector`
- **Scenarios/assertions**:
  - Register a leaf probe placement-filter strategy and constructor-injected director.
  - Resolve the keyed strategy and assert its exact probe type.
  - Resolve the keyed `IPlacementFilterDirector`, assert its exact probe type, and `Assert.Same` the registered marker with the director's constructor-captured dependency.
  - A missing key, removed constructor, or descriptor-only registration must fail during resolution or identity assertion.

#### 2. `OptionFormatterExtensionMethods.cs`

- **Source**: `src/Orleans.Core/Configuration/OptionLogger/OptionFormatterExtensionMethods.cs`
- **Methods**: generic formatter registration/`TryConfigureFormatter` forwarding and generic formatter-resolver registration/its `Try` forwarding path
- **Exact Test**: `UnitTests.DependencyInjection.CoreGenericRegistrationTests.OptionFormatter_ActivatesConstructorInjectedFormatterAndResolver`
- **Scenarios/assertions**:
  - Register constructor-injected probes for `IOptionFormatter<TOptions>` and `IOptionFormatterResolver<TOptions>` through the public generic extension paths.
  - Resolve both abstractions, assert exact probe types, and assert that both captured the registered marker instance.
  - Keep the case focused on activation; existing formatter output behavior is already substantially covered.

#### 3. `ClientBuilderExtensions.cs`

- **Source**: `src/Orleans.Core/Core/ClientBuilderExtensions.cs`
- **Methods**: generic retry-filter and cluster-connection-status-observer registration overloads
- **Exact Test**: `UnitTests.DependencyInjection.CoreGenericRegistrationTests.ClientBuilder_ActivatesConstructorInjectedRetryFilterAndObserver`
- **Scenarios/assertions**:
  - Use a small fake `IClientBuilder`; register constructor-injected `IClientConnectionRetryFilter` and `IClusterConnectionStatusObserver` probes through the generic builder APIs.
  - Build the provider and resolve both abstractions.
  - Assert exact types and `Assert.Same` for each constructor-captured marker; do not substitute delegate overload coverage.

#### 4. `GrainCallFilterServiceCollectionExtensions.cs`

- **Source**: `src/Orleans.Core/Core/GrainCallFilterServiceCollectionExtensions.cs`
- **Methods**: generic incoming and outgoing grain-call-filter registrations
- **Exact Test**: `UnitTests.DependencyInjection.CoreGenericRegistrationTests.GrainCallFilters_ActivateConstructorInjectedImplementations`
- **Scenarios/assertions**:
  - Register constructor-injected incoming and outgoing filter probes.
  - Resolve `IIncomingGrainCallFilter` and `IOutgoingGrainCallFilter`.
  - Assert exact implementations and marker identity for both directions in one representative fact.

### Phase Validation

Run the scoped Orleans.Core test command. Record its exit status and total discovered/passed count, and confirm all four fully-qualified Phase 1 names appear in solution `--list-tests` output.

### Success Criteria

- [ ] The single Core test file contains exactly the four planned facts.
- [ ] All four facts resolve real constructor-injected instances and make strong type/identity assertions.
- [ ] The keyed placement strategy and keyed constructor-injected director are both asserted.
- [ ] The scoped Orleans.Core project test command exits successfully.

---

## Phase 2: Orleans.Runtime DI, Factory, Alias, and Deferred Activation

### Overview

Cover the untested `FactoryUtility` overload family first within the file, then the representative keyed, alias, extension, and deferred lifecycle paths. Use caller-supplied probes and fake builders instead of resolving heavy default Runtime graphs. Every explicit factory argument and DI-provided marker must be asserted.

### Test File

- **Test File**: `test/Orleans.Runtime.Internal.Tests/DependencyInjection/GenericRegistrationTests.cs`
- **Test Class**: `UnitTests.DependencyInjection.RuntimeGenericRegistrationTests`
- **Style**: xUnit v3 `[Fact]` with repository BVT metadata; real `ServiceCollection`; minimal fake `ISiloBuilder`; recording `ISiloLifecycle` for startup only.

### Files to Test

#### 1. `FactoryUtility.cs`

- **Source**: `src/Orleans.Runtime/Utilities/FactoryUtility.cs`
- **Methods**: all four `FactoryUtility.Create` overloads for zero, one, two, and three explicit constructor arguments
- **Exact Test**: `UnitTests.DependencyInjection.RuntimeGenericRegistrationTests.FactoryUtility_Create_ActivatesConstructorsForAllSupportedArities`
- **Scenarios/assertions**:
  - Invoke every independently generated factory overload using a dedicated probe shape.
  - For arity zero, assert the instance exact type and DI marker identity.
  - For arities one through three, assert exact type, DI marker identity, and identity/value of every explicit argument in constructor order.
  - This one fact must contain distinct assertions for all four arities; exercising only currently closed production callers is insufficient.

#### 2. `PlacementStrategyExtensions.cs`

- **Source**: `src/Orleans.Runtime/Hosting/PlacementStrategyExtensions.cs`
- **Method**: `AddPlacementDirector<TStrategy, TDirector>`
- **Exact Test**: `UnitTests.DependencyInjection.RuntimeGenericRegistrationTests.PlacementDirector_ActivatesConstructorInjectedDirector`
- **Scenarios/assertions**:
  - Register a leaf strategy and constructor-injected director.
  - Resolve both keyed strategy and keyed `IPlacementDirector`.
  - Assert exact types and director marker identity.

#### 3. `ActivationRebalancerExtensions.cs`

- **Source**: `src/Orleans.Runtime/Hosting/ActivationRebalancerExtensions.cs`
- **Method**: generic activation-rebalancer/backoff-provider registration overload
- **Exact Test**: `UnitTests.DependencyInjection.RuntimeGenericRegistrationTests.ActivationRebalancer_ActivatesConstructorInjectedBackoffProvider`
- **Scenarios/assertions**:
  - Register a constructor-injected custom `IFailedSessionBackoffProvider`.
  - Resolve the custom concrete type and interface alias without resolving the monitor graph.
  - Assert both resolutions are the same singleton and its captured marker is the registered marker.

#### 4. `ActivationRepartitioningExtensions.cs`

- **Source**: `src/Orleans.Runtime/Hosting/ActivationRepartitioningExtensions.cs`
- **Method**: generic activation-repartitioning/tolerance-rule registration overload
- **Exact Test**: `UnitTests.DependencyInjection.RuntimeGenericRegistrationTests.ActivationRepartitioner_ActivatesConstructorInjectedToleranceRule`
- **Scenarios/assertions**:
  - Register a constructor-injected custom `IImbalanceToleranceRule`.
  - Resolve the custom concrete type and interface alias without resolving the repartitioner graph.
  - Assert singleton alias identity and constructor marker identity.

#### 5. `HostingGrainExtensions.cs`

- **Source**: `src/Orleans.Runtime/Hosting/HostingGrainExtensions.cs`
- **Method**: `AddGrainExtension<TExtensionInterface, TExtension>`
- **Exact Test**: `UnitTests.DependencyInjection.RuntimeGenericRegistrationTests.GrainExtension_ActivatesConstructorInjectedImplementation`
- **Scenarios/assertions**:
  - Register a keyed constructor-injected grain-extension implementation through the generic extension.
  - Resolve the keyed `IGrainExtension`, assert exact implementation type, and assert marker identity.

#### 6. `SiloBuilderStartupExtensions.cs`

- **Source**: `src/Orleans.Runtime/Hosting/SiloBuilderStartupExtensions.cs`
- **Method**: `AddStartupTask<TStartup>`
- **Exact Test**: `UnitTests.DependencyInjection.RuntimeGenericRegistrationTests.StartupTask_ActivatesConstructorInjectedTask`
- **Scenarios/assertions**:
  - Register a constructor-injected `IStartupTask` through a fake `ISiloBuilder`.
  - Resolve its `ILifecycleParticipant<ISiloLifecycle>` and attach it to a recording lifecycle.
  - Assert construction is deferred until the captured start observer is invoked.
  - Invoke the start callback, then assert the task was constructed/executed and received the exact registered marker.

### Phase Validation

Run the scoped Orleans.Runtime test command. Record its exit status and total discovered/passed count, and confirm all six fully-qualified Phase 2 names appear in solution `--list-tests` output.

### Success Criteria

- [ ] The single Runtime test file contains exactly the six planned facts.
- [ ] All four `FactoryUtility.Create` arities assert DI and explicit constructor arguments.
- [ ] Rebalancer and repartitioner concrete/interface resolutions are asserted as the same instances.
- [ ] Startup-task construction is proven deferred and its captured callback is executed.
- [ ] The scoped Orleans.Runtime project test command exits successfully.

---

## Phase 3: Self-Contained Trimmed Smoke for Selected Flows

### Overview

After both direct suites pass, add a deliberately small executable which lets the linker process `Orleans.Core` and `Orleans.Runtime` instead of rooting either assembly. Publish and execute locally before adding solution/workflow wiring. A zero exit means only that the three selected generic constructor flows survived this self-contained linked publish.

### Files to Create

#### 1. Smoke project

- **File**: `test/Orleans.DependencyInjection.TrimmedSmoke/Orleans.DependencyInjection.TrimmedSmoke.csproj`
- **Project**: `Orleans.DependencyInjection.TrimmedSmoke`
- **Configuration**:
  - SDK console executable with `OutputType=Exe`.
  - `TargetFrameworks=$(TestTargetFrameworks)`.
  - `IsOrleansFrameworkPart=false`.
  - `PublishTrimmed=true` and `TrimMode=link`.
  - Project references to `Orleans.Core` and `Orleans.Runtime`; add DI only if it is not transitive.
  - Do **not** add `TrimmerRootAssembly` for either Orleans assembly and do not copy the serialization smoke's rooted assembly graph.

#### 2. Self-checking smoke program

- **File**: `test/Orleans.DependencyInjection.TrimmedSmoke/Program.cs`
- **Module**: `Orleans.DependencyInjection.TrimmedSmoke`
- **Exact Checks**:
  1. `ValidateCoreRegistration`
     - Register a constructor-injected formatter or placement-filter director through a public Core generic extension.
     - Resolve it from DI and fail the executable unless its constructor-captured dependency is the exact registered marker.
  2. `ValidateRuntimeRegistration`
     - Call `AddPlacementDirector<TStrategy, TDirector>`.
     - Resolve the keyed strategy and keyed director; fail unless types are correct and the director captured the exact marker.
  3. `ValidateStartupTaskActivation`
     - Call `AddStartupTask<TStartup>` on a fake `ISiloBuilder`.
     - Resolve/participate the lifecycle participant, capture and invoke the start observer, and fail unless the constructor-injected task executes with the exact marker.
- **Failure behavior**: each check throws or otherwise causes a nonzero process exit on missing construction, wrong keyed registration, wrong dependency identity, or skipped startup execution.
- **Claim wording**: successful output may state that “selected generic DI constructor flows survived the self-contained trimmed smoke.” It must not say Orleans/Runtime is trim-safe, AOT-safe, NativeAOT-safe, or comprehensively linker-safe.

### Phase Validation

Run the three Windows clean/publish/execute commands exactly as listed under **Commands**. The executable must exit zero from the published output, not from an untrimmed build output. Record publish and process outcomes separately.

### Success Criteria

- [ ] The project follows the existing trimmed-smoke project shape without rooting `Orleans.Core` or `Orleans.Runtime`.
- [ ] All three exact self-check methods execute from the linked binary and enforce constructor dependency identity.
- [ ] The clean self-contained `win-x64` publish succeeds and the published executable exits zero.
- [ ] Output and comments use limited “selected generic DI constructor flows” wording.

---

## Phase 4: Solution/Workflow Wiring and Final Quality Gate

### Overview

Only after the local smoke passes, add the minimum solution and CI wiring, then run discovery, scoped tests, analyzer builds, and smoke validation. Finish with dedicated test-gap and assertion-quality reviews plus an explicit prompt-to-scenario mapping.

### Files to Modify

#### 1. Solution registration

- **File**: `Orleans.slnx`
- Add only `test/Orleans.DependencyInjection.TrimmedSmoke/Orleans.DependencyInjection.TrimmedSmoke.csproj`.
- Verify the solution discovery command still exits successfully and lists all ten exact unit-test names.

#### 2. Analyzer-audit workflow

- **File**: `.github/workflows/analyzer-audit.yml`
- Add a dedicated job/step with an exact proposed identifier such as `dependency-injection-trimmed-smoke` and display wording “selected generic DI constructor flows.”
- Reuse the existing serialization trimmed-smoke setup and self-contained publish/execute command pattern, changing only project/output/binary details for `Orleans.DependencyInjection.TrimmedSmoke`.
- Keep the existing serialization smoke unchanged.
- Do not add assembly roots and do not label the job as full trim/AOT safety.
- Use the exact CI-equivalent Linux publish/execute commands from **Commands**.

### Ordered Validation

1. Run both scoped project test commands and record pass/fail plus discovered/passed totals.
2. Run the solution `--list-tests` command and capture evidence that these exact names are discoverable:
   - `UnitTests.DependencyInjection.CoreGenericRegistrationTests.PlacementFilter_ActivatesConstructorInjectedDirector`
   - `UnitTests.DependencyInjection.CoreGenericRegistrationTests.OptionFormatter_ActivatesConstructorInjectedFormatterAndResolver`
   - `UnitTests.DependencyInjection.CoreGenericRegistrationTests.ClientBuilder_ActivatesConstructorInjectedRetryFilterAndObserver`
   - `UnitTests.DependencyInjection.CoreGenericRegistrationTests.GrainCallFilters_ActivateConstructorInjectedImplementations`
   - `UnitTests.DependencyInjection.RuntimeGenericRegistrationTests.PlacementDirector_ActivatesConstructorInjectedDirector`
   - `UnitTests.DependencyInjection.RuntimeGenericRegistrationTests.FactoryUtility_Create_ActivatesConstructorsForAllSupportedArities`
   - `UnitTests.DependencyInjection.RuntimeGenericRegistrationTests.ActivationRebalancer_ActivatesConstructorInjectedBackoffProvider`
   - `UnitTests.DependencyInjection.RuntimeGenericRegistrationTests.ActivationRepartitioner_ActivatesConstructorInjectedToleranceRule`
   - `UnitTests.DependencyInjection.RuntimeGenericRegistrationTests.GrainExtension_ActivatesConstructorInjectedImplementation`
   - `UnitTests.DependencyInjection.RuntimeGenericRegistrationTests.StartupTask_ActivatesConstructorInjectedTask`
3. Run both narrow compatibility-analyzer builds and require successful exits with no `IL2087` or `IL2091` diagnostics for the bounded projects.
4. Repeat the clean Windows self-contained publish and published-executable run after wiring.
5. Validate the Linux command shape through the workflow; run it locally only where a Linux SDK/runtime environment is available. Any skipped local Linux execution must be reported explicitly rather than inferred from Windows success.
6. Compare the exact changed-file list with the strict allowlist. Do not “fix” concurrent or pre-existing changes by restoring/resetting/cleaning.

### Final Quality Gate

1. Invoke **`test-gap-analysis`** over the two generated test files and smoke checks. Review specifically for:
   - all ten bounded source files assigned and represented once;
   - every `FactoryUtility.Create` arity;
   - keyed strategy/director resolution;
   - concrete/interface alias identity;
   - startup construction deferral and execution;
   - all three linked-binary smoke behaviors.
   Fix in-scope gaps only, then record findings/fixes in `.testagent/status.md`.
2. Invoke **`assertion-quality`** over the two generated test files and smoke checks. Reject descriptor-only, non-null-only, self-referential, or assertion-free cases. Require exact type, marker identity, explicit-argument, alias-identity, deferred-state, and execution-state assertions as applicable. Record findings/fixes in `.testagent/status.md`.
3. Perform a **prompt-scenario mapping review** using the acceptance traceability table below. Re-open the generated tests/smoke and map each behavioral requirement to exact test/check names and each nonbehavioral requirement to a file, command, diff, or report. No row may rely on generic coverage claims.
4. Re-run every validation affected by a quality-gate fix. Only clean successful exits are final evidence.

### Success Criteria

- [ ] The smoke project is in `Orleans.slnx` and has a dedicated CI publish/execute path.
- [ ] Existing serialization smoke wiring remains unchanged.
- [ ] Both scoped suites, both analyzer builds, solution discovery, and clean Windows publish/execute pass.
- [ ] `test-gap-analysis`, `assertion-quality`, and prompt-scenario mapping have no unresolved in-scope findings.
- [ ] The exact changed-file list respects the boundary; any unrelated concurrent files are identified, not altered.
- [ ] Final reporting includes command-by-command outcomes/counts, exact test names, smoke checks, skipped checks, and either explicit blockers or `Blockers: none.`

---

## Acceptance Traceability

| # | Acceptance Item | Planned Behavioral Test/Check or Nonbehavioral Evidence |
|---:|---|---|
| 1 | **Representative Orleans.Core DI activation tests** | The four exact `UnitTests.DependencyInjection.CoreGenericRegistrationTests.*` facts in Phase 1; each resolves a constructor-injected implementation and asserts the registered marker by identity. |
| 2 | **Representative Orleans.Runtime DI activation tests** | The six exact `UnitTests.DependencyInjection.RuntimeGenericRegistrationTests.*` facts in Phase 2. `FactoryUtility_Create_ActivatesConstructorsForAllSupportedArities` covers arities 0–3; `StartupTask_ActivatesConstructorInjectedTask` proves deferred construction and callback execution. |
| 3 | **Representative self-contained trimmed smoke** | Linked-binary checks `ValidateCoreRegistration`, `ValidateRuntimeRegistration`, and `ValidateStartupTaskActivation`, evidenced by the exact clean/publish/execute commands and a zero process exit. |
| 4 | **No overclaim** | Wording review of `Program.cs`, smoke project/workflow names, `.testagent/status.md`, and the final report. Allowed claim: selected generic DI constructor flows survived this smoke. Explicit exclusions: full silo/client, all Runtime, NativeAOT, and every reflection path. |
| 5 | **Reuse smoke infrastructure** | Nonbehavioral diff evidence from `Orleans.DependencyInjection.TrimmedSmoke.csproj` and `.github/workflows/analyzer-audit.yml`: same suitable project/workflow pattern as serialization smoke, while the existing smoke remains unchanged and neither Orleans assembly is rooted. |
| 6 | **Change boundary** | Exact `git diff --name-only` evidence compared with the six-file implementation allowlist; production and root analyzer files must be absent. `.testagent` files are reported separately as pipeline artifacts. |
| 7 | **Exact commands** | Command-by-command results for both scoped tests, both narrow analyzer builds, solution discovery, Windows clean/publish/run, and CI Linux publish/run or an explicit environment-based skip. |
| 8 | **Final reporting** | Final requirement/evidence table and `.testagent/status.md` list exact changed files, all ten fully-qualified test names, three smoke checks, pass/fail and discovered-test counts per command, skipped checks, and `Blockers: none.` when applicable. |

## Final Reporting Format

The implementation report must contain:

1. Exact implementation changed-file list, with `.testagent` artifacts listed separately.
2. All ten exact fully-qualified xUnit names and the three exact smoke-check names.
3. A command table containing command, exit status, discovered/passed test count where applicable, and smoke executable exit status.
4. A concise `Requirement | Evidence` table whose behavioral evidence cites exact tests/checks and whose nonbehavioral evidence cites exact files/commands/reports.
5. Quality-gate outcomes for `test-gap-analysis`, `assertion-quality`, and prompt-scenario mapping, including fixes and reruns.
6. Explicit skipped checks and blockers; if neither exists, state `Blockers: none.`
