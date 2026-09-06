# Test Generation Research

## Project Overview
- **Path**: `C:\dev\copilot-worktrees\orleans\rb-refactored-pancake`
- **Branch/status at research time**: `rb-trim-di-constructor-flow`; clean (`git status --short --branch` reported only `## rb-trim-di-constructor-flow`). `HEAD` is `999f145b0`, currently also `origin/main`.
- **Language**: C#/.NET
- **Framework**: Microsoft.Extensions.DependencyInjection-based Orleans registration APIs
- **Test Framework**: xUnit v3 `3.2.2` on Microsoft Testing Platform `2.4.0`; NSubstitute `5.3.0` is available where already referenced
- **Project system**: SDK-style; source projects target `$(DefaultTargetFrameworks)`, tests target `net8.0;net10.0`
- **Dependency format and versions**: Central `PackageReference` in `Directory.Packages.props`; Microsoft.Extensions.DependencyInjection is `8.0.1` normally and `10.0.5` for `net10.0`
- **New-file registration**: implicit SDK `Compile` glob. New unit-test and smoke `.cs` files need no explicit `<Compile Include>`. A new smoke project does need solution/workflow wiring.
- **Analyzer context**: `Orleans.Core.csproj` enables trim analysis for `net10.0`, but normally suppresses `IL2087`/`IL2091`; setting `EnableCompatibilityAnalyzerAudit=true` removes that suppression. `.github/scripts/analyzer-audit.targets` enables trim, AOT, and single-file analyzers for the compatibility audit. Do not change root analyzer configuration.
- **Discovery caveat**: `code-testing-extensions` was attempted before this research and was unavailable. The mandatory `find-untested-sources` Roslyn run was also already completed once at repository root and was not rerun: 2,858 sources, 1,118 tests, 1,462 unpaired, 1,396 paired. Its extension-method path lookup was inconclusive because static pairing does not reliably attribute extension-method instance syntax or DI activation. Those numbers are a **static pairing heuristic, not line/branch coverage**.

## Dependency Graph
- **Leaf types (no in-scope dependencies)**:
  - Placement strategy instances with public parameterless construction: `RandomPlacement`, `PreferLocalPlacement`, `StatelessWorkerPlacement`, `ActivationCountBasedPlacement`, `HashBasedPlacement`, `ClientObserversPlacement`, `SiloRoleBasedPlacement`, `ResourceOptimizedPlacement`, `PreferredMatchSiloMetadataPlacementFilterStrategy`, and `RequiredMatchSiloMetadataPlacementFilterStrategy`.
  - Parameterless directors such as `RandomPlacementDirector`, `PreferLocalPlacementDirector`, `StatelessWorkerDirector`, `HashBasedPlacementDirector`, and `ClientObserversPlacementDirector`.
  - Test-only probe strategy types should be leaves; give implementation probes one small constructor dependency so the tests prove DI constructor activation rather than only descriptor presence.
- **Mid-layer types (DI/reflection-created and dependent on services)**:
  - Core: custom `IOptionFormatter<T>`/`IOptionFormatterResolver<T>`, `IClientConnectionRetryFilter`, `IClusterConnectionStatusObserver`, incoming/outgoing call filters, placement-filter directors, `ActivityPropagationIncomingGrainCallFilter`, `ActivityPropagationOutgoingGrainCallFilter`, and `StaticGatewayListProvider`.
  - Runtime: custom placement directors, `Gateway`, `LocalGrainDirectoryPartition`, `SiloRoleBasedPlacementDirector`, `ActivationCountPlacementDirector`, `ResourceOptimizedPlacementDirector`, `ActivationRebalancerMonitor`, `FailedSessionBackoffProvider`, `ActivationRebalancerOptionsValidator`, `ActivationRepartitioner`, `RepartitionerMessageFilter`, `RebalancerCompatibleRule`, and `ActivationRepartitionerOptionsValidator`.
  - Caller-supplied runtime implementations: `IFailedSessionBackoffProvider`, `IImbalanceToleranceRule`, `IGrainExtension`, and `IStartupTask`.
- **Top-layer types (registration/factory flow under test)**:
  - Core: `PlacementFilterExtensions`, `OptionConfigureExtensionMethods`, `ClientBuilderExtensions`, and `GrainCallFilterServiceCollectionExtensions`.
  - Runtime: `PlacementStrategyExtensions`, `FactoryUtility`, `ActivationRebalancerExtensions`, `ActivationRepartitioningExtensions`, `HostingGrainExtensions`, and `SiloBuilderStartupExtensions`.
  - Closed production callers: `DefaultClientServices`, `DefaultSiloServices`, `CoreHostingExtensions`, and `SiloMetadataHostingExtensions`.
- **Testing direction**: directly activate small constructor-injected probe implementations for the generic APIs. Do not mock DI. For `AddStartupTask<TStartup>`, resolve its `ILifecycleParticipant<ISiloLifecycle>`, attach it to a recording lifecycle, invoke the captured start callback, and assert that the constructor dependency reached `TStartup`. Heavy default silo registrands should not all be instantiated in these focused tests.

## Build & Test Commands
Run from the repository root in PowerShell.

- **Build (narrow compatibility-analyzer checks)**:
  ```powershell
  dotnet build src/Orleans.Core/Orleans.Core.csproj --framework net10.0 --configuration Release --no-incremental -p:EnableCompatibilityAnalyzerAudit=true -p:CustomBeforeMicrosoftCommonTargets="$PWD/.github/scripts/analyzer-audit.targets" -p:WarningsAsErrors="IL2087;IL2091"
  dotnet build src/Orleans.Runtime/Orleans.Runtime.csproj --framework net10.0 --configuration Release --no-incremental -p:EnableCompatibilityAnalyzerAudit=true -p:CustomBeforeMicrosoftCommonTargets="$PWD/.github/scripts/analyzer-audit.targets" -p:WarningsAsErrors="IL2087;IL2091"
  ```
- **Test (scoped — fix cycles)**:
  ```powershell
  dotnet test --project test/Orleans.Core.Tests/Orleans.Core.Tests.csproj --framework net10.0 --configuration Release --minimum-expected-tests 1
  dotnet test --project test/Orleans.Runtime.Internal.Tests/Orleans.Runtime.Internal.Tests.csproj --framework net10.0 --configuration Release --minimum-expected-tests 1
  ```
  These are project-scoped and avoid relying on an unverified runner-specific name-filter syntax. Once the proposed classes exist, their exact names should be checked in discovery output before adding a narrower filter.
- **Test (harness-equivalent — discovery check)**:
  ```powershell
  dotnet test --solution Orleans.slnx --configuration Release --framework net10.0 --list-tests
  ```
  Confirm that all proposed fully-qualified test names below appear. The repository opts into Microsoft Testing Platform in `global.json`; this is not a VSTest command.
- **Trimmed smoke, clean/publish/execute on this Windows workspace**:
  ```powershell
  dotnet clean test/Orleans.DependencyInjection.TrimmedSmoke/Orleans.DependencyInjection.TrimmedSmoke.csproj --framework net10.0 --configuration Release --runtime win-x64
  dotnet publish test/Orleans.DependencyInjection.TrimmedSmoke/Orleans.DependencyInjection.TrimmedSmoke.csproj --framework net10.0 --configuration Release --runtime win-x64 --self-contained true --output Artifacts/Orleans.DependencyInjection.TrimmedSmoke/win-x64
  & .\Artifacts\Orleans.DependencyInjection.TrimmedSmoke\win-x64\Orleans.DependencyInjection.TrimmedSmoke.exe
  ```
- **Trimmed smoke, CI-equivalent Linux publish/execute**:
  ```powershell
  $output = Join-Path $env:RUNNER_TEMP 'orleans-dependency-injection-trimmed-smoke'
  dotnet publish test/Orleans.DependencyInjection.TrimmedSmoke/Orleans.DependencyInjection.TrimmedSmoke.csproj --framework net10.0 --configuration Release --runtime linux-x64 --self-contained true --output $output
  & (Join-Path $output 'Orleans.DependencyInjection.TrimmedSmoke')
  ```
- **Lint**: no separate scoped lint command was found; compilation enforces repository analyzers and code style (`EnforceCodeStyleInBuild=true`, `TreatWarningsAsErrors=true`).

## Scope
- **Boundary**: only the shared generic DI/`ActivatorUtilities` public-constructor preservation invariant behind `IL2087`/`IL2091` in the complete `Orleans.Core` and `Orleans.Runtime` projects, constrained to the ten user-named production candidates. Sibling projects and unrelated trim warnings are out of scope.
- **Targets**:
  1. `src/Orleans.Core/Placement/PlacementFilterExtensions.cs`
  2. `src/Orleans.Core/Configuration/OptionLogger/OptionFormatterExtensionMethods.cs`
  3. `src/Orleans.Core/Core/ClientBuilderExtensions.cs`
  4. `src/Orleans.Core/Core/GrainCallFilterServiceCollectionExtensions.cs`
  5. `src/Orleans.Runtime/Hosting/PlacementStrategyExtensions.cs`
  6. `src/Orleans.Runtime/Utilities/FactoryUtility.cs`
  7. `src/Orleans.Runtime/Hosting/ActivationRebalancerExtensions.cs`
  8. `src/Orleans.Runtime/Hosting/ActivationRepartitioningExtensions.cs`
  9. `src/Orleans.Runtime/Hosting/HostingGrainExtensions.cs`
  10. `src/Orleans.Runtime/Hosting/SiloBuilderStartupExtensions.cs`
- **Suggested new unit-test targets**:
  - `test/Orleans.Core.Tests/DependencyInjection/GenericRegistrationTests.cs`
  - `test/Orleans.Runtime.Internal.Tests/DependencyInjection/GenericRegistrationTests.cs`
- **Suggested new smoke target**:
  - `test/Orleans.DependencyInjection.TrimmedSmoke/Orleans.DependencyInjection.TrimmedSmoke.csproj`
  - `test/Orleans.DependencyInjection.TrimmedSmoke/Program.cs`
- **Representative existing tests read**:
  - `test/Orleans.Runtime.Tests/LogFomatterTests.cs`
  - `test/Orleans.Runtime.Tests/StartupTaskTests.cs`

## Generic Registration and Factory Inventory

| Candidate | Constructor-sensitive flow | Closed production implementation types in the bounded projects |
|---|---|---|
| `PlacementFilterExtensions.cs` | `DescribeKeyed(... typeof(TFilter) ...)`; `AddKeyedSingleton<IPlacementFilterDirector,TDirector>` | `PreferredMatchSiloMetadataPlacementFilterStrategy` / `PreferredMatchSiloMetadataPlacementFilterDirector`; `RequiredMatchSiloMetadataPlacementFilterStrategy` / `RequiredMatchSiloMetadataPlacementFilterDirector` from `SiloMetadataHostingExtensions` |
| `OptionFormatterExtensionMethods.cs` | `AddSingleton<IOptionFormatter<TOptions>,TOptionFormatter>`; generic forwarding from `TryConfigureFormatter`; `AddSingleton<IOptionFormatterResolver<TOptions>,TOptionFormatterResolver>` and its `Try` forwarding path | Call sites in `DefaultClientServices` and `DefaultSiloServices` use the one-type formatter path for `ClusterOptions`, `ClientMessagingOptions`, `ConnectionOptions`, `SiloOptions`, `SchedulingOptions`, `SiloMessagingOptions`, `ClusterMembershipOptions`, `GrainDirectoryOptions`, `ActivationCountBasedPlacementOptions`, `ResourceOptimizedPlacementOptions`, `GrainCollectionOptions`, `StatelessWorkerOptions`, `GrainVersioningOptions`, `ConsistentRingOptions`, `LoadSheddingOptions`, and `EndpointOptions`; implementation/resolver types for the two-type overload are caller supplied |
| `ClientBuilderExtensions.cs` | `AddSingleton<IClientConnectionRetryFilter,TConnectionRetryFilter>`; `AddSingleton<IClusterConnectionStatusObserver,TObserver>`; generic forwarding to call-filter registration | Caller-supplied retry filter/observer; closed call-filter types are `ActivityPropagationOutgoingGrainCallFilter` and `ActivityPropagationIncomingGrainCallFilter`. `StaticGatewayListProvider` is another concrete DI-created type in this file, though it is not itself a generic type parameter. |
| `GrainCallFilterServiceCollectionExtensions.cs` | `AddSingleton<IIncomingGrainCallFilter,TImplementation>` and outgoing equivalent | Caller supplied; internal closed callers include `ActivityPropagationIncomingGrainCallFilter` and `ActivityPropagationOutgoingGrainCallFilter` |
| `PlacementStrategyExtensions.cs` | keyed `DescribeKeyed(... typeof(TStrategy) ...)`; `AddKeyedSingleton<IPlacementDirector,TDirector>`; builder overloads forward generic requirements | Default registrations: `RandomPlacement`/`RandomPlacementDirector`, `PreferLocalPlacement`/`PreferLocalPlacementDirector`, `StatelessWorkerPlacement`/`StatelessWorkerDirector`, `ActivationCountBasedPlacement`/`ActivationCountPlacementDirector`, `HashBasedPlacement`/`HashBasedPlacementDirector`, `ClientObserversPlacement`/`ClientObserversPlacementDirector`, `SiloRoleBasedPlacement`/`SiloRoleBasedPlacementDirector`, `ResourceOptimizedPlacement`/`ResourceOptimizedPlacementDirector` |
| `FactoryUtility.cs` | four `ActivatorUtilities.CreateFactory(typeof(TInstance), ...)` overloads for zero through three explicit parameters | `Gateway` through `Create<MessageCenter, Gateway>` and `LocalGrainDirectoryPartition` through `Create<LocalGrainDirectoryPartition>` in `DefaultSiloServices`; the two- and three-argument overloads have no bounded production caller |
| `ActivationRebalancerExtensions.cs` | `AddSingleton<TProvider>` followed by `AddFromExisting`; optional lifecycle alias | Default `FailedSessionBackoffProvider`; also `ActivationRebalancerMonitor` and `ActivationRebalancerOptionsValidator`; custom provider type is caller supplied |
| `ActivationRepartitioningExtensions.cs` | `AddSingleton<TRule>` followed by `AddFromExisting`; optional lifecycle alias | Default `RebalancerCompatibleRule`; also `ActivationRepartitioner`, `RepartitionerMessageFilter`, nested `ActivationRepartitioner.DeactivatedGrainQueue`, and `ActivationRepartitionerOptionsValidator`; custom rule type is caller supplied |
| `HostingGrainExtensions.cs` | `AddKeyedTransient<IGrainExtension,TExtension>` | Caller supplied |
| `SiloBuilderStartupExtensions.cs` | deferred `ActivatorUtilities.GetServiceOrCreateInstance<TStartup>` | Caller supplied |

The preservation contract belongs on the generic implementation/instance parameters which flow into DI or `ActivatorUtilities`; forwarding methods must propagate the same contract. Tests should assert actual activation and dependency identity, not inspect attributes or service descriptors alone.

## Files to Test

### High Priority
| File | Classes/Functions | Testability | Estimated Coverage | Notes |
|---|---|---|---|---|
| `src/Orleans.Core/Placement/PlacementFilterExtensions.cs` | `AddPlacementFilter<TFilter,TDirector>` | High | Partial | Existing cluster tests use parameterless directors; add direct keyed resolution with a constructor-injected director. |
| `src/Orleans.Core/Configuration/OptionLogger/OptionFormatterExtensionMethods.cs` | formatter and resolver generic overloads | High | Substantial behavior, partial invariant | Existing tests resolve constructor-injected formatters/resolvers, but no trimmed proof ties the generic contract together. |
| `src/Orleans.Core/Core/ClientBuilderExtensions.cs` | generic retry filter and observer; activity filter forwarding | High with a small fake `IClientBuilder` | Partial | Existing retry tests use delegate overloads. Directly resolve constructor-injected generic implementations. |
| `src/Orleans.Core/Core/GrainCallFilterServiceCollectionExtensions.cs` | generic incoming/outgoing registrations | High | Partial | Existing incoming-filter cluster test has a dependency; add cheap direct incoming and outgoing DI resolution. |
| `src/Orleans.Runtime/Hosting/PlacementStrategyExtensions.cs` | generic strategy/director registrations and forwarding | High | Partial | Resolve both keyed strategy and constructor-injected keyed director. |
| `src/Orleans.Runtime/Utilities/FactoryUtility.cs` | `Create` overloads for 0–3 explicit arguments | High | Untested | Internal visibility makes direct leaf-first testing possible; assert injected and explicit constructor arguments for every overload. |
| `src/Orleans.Runtime/Hosting/SiloBuilderStartupExtensions.cs` | `AddStartupTask<TStartup>` | Medium | Substantial behavior, partial invariant | Existing full-cluster test proves constructor injection. Add a focused recording-lifecycle test, and preferably exercise this path in the trimmed smoke. |

### Medium Priority
| File | Classes/Functions | Testability | Estimated Coverage | Notes |
|---|---|---|---|---|
| `src/Orleans.Runtime/Hosting/ActivationRebalancerExtensions.cs` | default and generic provider overloads | Medium | Partial | Existing tests enable the default service. A focused fake-builder test can resolve a constructor-injected custom provider without resolving the heavy monitor graph. |
| `src/Orleans.Runtime/Hosting/ActivationRepartitioningExtensions.cs` | default and generic rule overloads | Medium | Partial | Existing custom-rule coverage uses a parameterless rule. Resolve a constructor-injected rule through `IImbalanceToleranceRule`. |
| `src/Orleans.Runtime/Hosting/HostingGrainExtensions.cs` | `AddGrainExtension<TExtensionInterface,TExtension>` | High | Partial | Existing functional tests use parameterless extensions. Resolve the keyed implementation with a constructor dependency. |

### Low Priority / Skip
| File | Reason |
|---|---|
| Non-candidate source files under `Orleans.Core`/`Orleans.Runtime` | Outside the explicitly bounded production scope; only their closed call sites/types were inventoried. |
| `src/api/**` generated API files | Generated API baselines, not test targets. |
| Full silo startup in the trimmed app | This work proves selected generic constructor flows only and must not imply that the full runtime is trim- or AOT-safe. |

## Existing Tests & Coverage Classification
- `PlacementFilterExtensions.cs` → `test/Orleans.Placement.Tests/PlacementFilterTests/GrainPlacementFilterTests.cs`, `test/Orleans.Runtime.Tests/ActivationTracingTests.cs`: **partial**; registration and behavior are exercised, but the directors shown are parameterless and the tests are not trimmed.
- `OptionFormatterExtensionMethods.cs` → `test/Orleans.Runtime.Tests/LogFomatterTests.cs`: **substantial behavior / partial preservation evidence**; formatter and resolver implementations have `IOptions`/`IOptionsMonitor` constructors and are resolved, but only in an untrimmed test process.
- `ClientBuilderExtensions.cs` → `test/Orleans.Runtime.Tests/ActivityPropagationTests.cs`, `ActivationTracingTests.cs`, and client connection tests: **partial**; activity registrations and delegate retry overloads are exercised, but the generic retry-filter and generic observer overloads have no located focused activation test.
- `GrainCallFilterServiceCollectionExtensions.cs` → `test/Orleans.Runtime.Tests/GrainCallFilterTests.cs`: **partial**; an incoming filter with an `IGrainFactory` dependency is activated through a cluster, but there is no cheap paired incoming/outgoing DI test or trimmed proof.
- `PlacementStrategyExtensions.cs` → `test/Orleans.Runtime.Tests/Placement/CustomPlacementTests.cs`: **partial**; custom keyed placement works, but the located director is parameterless.
- `FactoryUtility.cs` → no paired test located: **untested**.
- `ActivationRebalancerExtensions.cs` → `test/Orleans.Placement.Tests/ActivationRebalancingTests/RebalancerFixture.cs` and `StatePreservationRebalancingTests.cs`: **partial**; default registration is used, not a constructor-injected custom generic provider.
- `ActivationRepartitioningExtensions.cs` → `test/Orleans.Placement.Tests/ActivationRepartitioningTests/CustomToleranceTests.cs` and default/migration tests: **partial**; generic custom rule exists but is parameterless.
- `HostingGrainExtensions.cs` → `test/Orleans.Runtime.Tests/GrainCallFilterTests.cs`, `test/Orleans.DefaultCluster.Tests/ProviderTests.cs`, and grain-service tests: **partial**; keyed extension behavior exists, but no constructor-injected focused registration test was located.
- `SiloBuilderStartupExtensions.cs` → `test/Orleans.Runtime.Tests/StartupTaskTests.cs`: **substantial behavior / partial preservation evidence**; `CallGrainStartupTask(IGrainFactory)` is activated and run in a full cluster, but not after trimming.

## Existing Test Projects
- **Project file**: `test/Orleans.Core.Tests/Orleans.Core.Tests.csproj`
  - **Target source project**: `Orleans.Core` (available through the existing test project graph; `Orleans.Core` grants `InternalsVisibleTo` to this assembly)
  - **Relevant test files**: `Runtime/PlacementServiceTests.cs`; proposed `DependencyInjection/GenericRegistrationTests.cs`
  - **Wiring**: implicit compile glob; no project edit expected.
- **Project file**: `test/Orleans.Runtime.Internal.Tests/Orleans.Runtime.Internal.Tests.csproj`
  - **Target source project**: `Orleans.Runtime` (existing graph; `Orleans.Runtime` grants `InternalsVisibleTo`)
  - **Relevant test files**: proposed `DependencyInjection/GenericRegistrationTests.cs`
  - **Wiring**: implicit compile glob; no project edit expected.
- **Project file**: `test/Orleans.Runtime.Tests/Orleans.Runtime.Tests.csproj`
  - **Target source project**: Runtime behavior/in-process cluster
  - **Relevant test files**: `LogFomatterTests.cs`, `StartupTaskTests.cs`, `GrainCallFilterTests.cs`, `Placement/CustomPlacementTests.cs`, `ActivityPropagationTests.cs`, `ActivationTracingTests.cs`
  - **Wiring**: no change recommended; these tests are evidence and convention references.
- **Project file**: `test/Orleans.Placement.Tests/Orleans.Placement.Tests.csproj`
  - **Target source project**: runtime placement/rebalancing
  - **Relevant test files**: `PlacementFilterTests/GrainPlacementFilterTests.cs`, `ActivationRebalancingTests/RebalancerFixture.cs`, `ActivationRebalancingTests/StatePreservationRebalancingTests.cs`, `ActivationRepartitioningTests/CustomToleranceTests.cs`
  - **Wiring**: no change recommended.
- **Project file**: `test/Orleans.Serialization.TrimmedSmoke/Orleans.Serialization.TrimmedSmoke.csproj`
  - **Target source project**: serialization and `Orleans.Core`
  - **Relevant files**: `Program.cs`
  - **Suitability**: its self-contained publish/execute workflow is the right template, but its `<TrimmerRootAssembly Include="Orleans.Core" />` deliberately roots the full Core assembly. Extending it would not be strong evidence that the selected Core generic constructor flow survived linking, and adding a Runtime root would be worse. Keep it intact and reuse its project/workflow pattern in a separate DI smoke app.

## Testing Patterns
- Tests use xUnit v3 `[Fact]`/`Assert.*` and Microsoft Testing Platform. Repository metadata commonly includes `[TestSuite("BVT")]`, `[TestProvider("None")]`, `[TestArea("Runtime")]`, and `[TestCategory("BVT")]`.
- Existing DI tests create `ServiceCollection`, add dependencies/options, call the extension under test, build a provider, resolve the service, and assert both concrete type and behavior.
- Dispose providers with `using var`.
- Prefer exact type/instance/dependency assertions. Constructor probes should expose the injected marker and assertions should use `Assert.Same`, preventing a descriptor-only test from passing.
- Use a tiny fake `IClientBuilder`/`ISiloBuilder` backed by `ServiceCollection` and an empty `IConfiguration`; do not start a cluster for registration-only tests.
- Where multiple aliases are registered through `AddFromExisting`, assert that interface resolution returns the same instance as concrete resolution.
- Avoid mocks for these leaf registration tests. Use a recording `ISiloLifecycle` only for the deferred startup callback.

## Proposed Exact Tests

### `Orleans.Core.Tests`
Class: `UnitTests.DependencyInjection.CoreGenericRegistrationTests`

1. `PlacementFilter_ActivatesConstructorInjectedDirector`
2. `OptionFormatter_ActivatesConstructorInjectedFormatterAndResolver`
3. `ClientBuilder_ActivatesConstructorInjectedRetryFilterAndObserver`
4. `GrainCallFilters_ActivateConstructorInjectedImplementations`

Together these cover the four named Core files with direct service resolution. Include the keyed placement strategy assertion as well as the director assertion.

### `Orleans.Runtime.Internal.Tests`
Class: `UnitTests.DependencyInjection.RuntimeGenericRegistrationTests`

1. `PlacementDirector_ActivatesConstructorInjectedDirector`
2. `FactoryUtility_Create_ActivatesConstructorsForAllSupportedArities`
3. `ActivationRebalancer_ActivatesConstructorInjectedBackoffProvider`
4. `ActivationRepartitioner_ActivatesConstructorInjectedToleranceRule`
5. `GrainExtension_ActivatesConstructorInjectedImplementation`
6. `StartupTask_ActivatesConstructorInjectedTask`

These should use caller-supplied probes rather than resolving the entire default silo object graph. For the rebalancer/repartitioner tests, resolve the custom concrete type and its interface alias and assert identity.

## Suitable Trimmed Smoke Infrastructure and Wiring
- Create a separate `test/Orleans.DependencyInjection.TrimmedSmoke` SDK console project modeled on `Orleans.Serialization.TrimmedSmoke`.
- Set `OutputType=Exe`, `TargetFrameworks=$(TestTargetFrameworks)`, `IsOrleansFrameworkPart=false`, `PublishTrimmed=true`, and `TrimMode=link`. Reference `Orleans.Core` and `Orleans.Runtime` projects plus DI if it is not already available transitively.
- Do **not** add `TrimmerRootAssembly` for `Orleans.Core` or `Orleans.Runtime`: the point is to let the linker prove that the annotated generic flow preserves the selected public constructors.
- Keep the program intentionally small and self-checking. Recommended checks:
  1. `ValidateCoreRegistration`: register a custom constructor-injected formatter or placement-filter director through the public Core generic extension, build the provider, resolve it, and verify dependency identity.
  2. `ValidateRuntimeRegistration`: register a custom constructor-injected placement director through `AddPlacementDirector<TStrategy,TDirector>`, resolve the keyed strategy/director, and verify dependency identity.
  3. `ValidateStartupTaskActivation`: register `AddStartupTask<TStartup>` on a fake `ISiloBuilder`, resolve the lifecycle participant, capture/start its observer with a recording lifecycle, and verify that the trimmed executable constructed and executed the constructor-injected task.
- Add the project to `Orleans.slnx`.
- Add a dedicated publish-and-run job/step to `.github/workflows/analyzer-audit.yml`, copying the existing `serialization-trimmed-smoke` setup and command shape but using the new project/binary. Keep the existing serialization smoke unchanged.
- A successful smoke means only that these selected registration/factory paths survive a self-contained linked publish. It is **not** evidence that a full Orleans silo/client, all of `Orleans.Runtime`, NativeAOT, or every reflection path is safe.

## Acceptance Checklist
1. **Representative Orleans.Core DI activation tests**: add the four proposed Core facts and assert actual constructor-injected resolutions.
2. **Representative Orleans.Runtime DI activation tests**: add the six proposed Runtime facts, including all `FactoryUtility.Create` arities and deferred startup-task creation.
3. **Representative self-contained trimmed smoke**: publish and execute `Orleans.DependencyInjection.TrimmedSmoke`; it must validate selected Core registration, Runtime keyed registration, and startup factory behavior from the linked binary.
4. **No overclaim**: documentation, test names, workflow names, and final report must say “selected generic DI constructor flows,” not “Orleans is trim/AOT-safe.”
5. **Reuse smoke infrastructure**: copy the existing trimmed-smoke project conventions and workflow publish/execute pattern, but do not reuse its rooted assembly graph as proof.
6. **Change boundary**: modify only the two test files, the new smoke project/files, `Orleans.slnx`, and `.github/workflows/analyzer-audit.yml` as necessary. Do not modify production files or root analyzer configuration as part of test generation.
7. **Exact commands**: run and report the two scoped test commands, both narrow analyzer builds, and the clean self-contained publish/execute commands listed above.
8. **Final reporting**: include (a) the exact changed-file list, (b) every exact fully-qualified test name, (c) command-by-command pass/fail and discovered-test counts, and (d) blockers or skipped checks. If there are none, explicitly state “Blockers: none.”

## Recommendations
1. Add the Core direct-DI tests first; they are fast leaf tests and establish the assertion pattern.
2. Add Runtime direct-DI/factory tests next. Cover each `FactoryUtility` overload because each independently creates reflection metadata.
3. Add the separate unrooted DI trimmed smoke and run it locally with `win-x64`.
4. Run the two compatibility-analyzer builds and ensure `IL2087`/`IL2091` are absent for the bounded candidates.
5. Add solution/workflow wiring only after local publish/execute succeeds; then verify MTP discovery from the root.
6. Do not infer dynamic coverage from the prior source-pairing counts. Existing tests demonstrate behavior, but only the new linked executable can catch constructor removal after trimming.
7. **Current blockers**: no implementation blocker found. The language extension skill was unavailable, so harness guidance was derived from `global.json`, `test/Directory.Build.props`, package versions, and the checked-in workflow. No tests/builds/publishes were run during this research.
