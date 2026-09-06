# Test Generation Status

## Scope and Outcome

Implemented focused, representative test coverage for the shared generic DI
public-constructor preservation invariant behind IL2087/IL2091 in the complete
`Orleans.Core` and `Orleans.Runtime` projects.

This work proves selected generic registration and factory flows. It does **not**
claim that the full Orleans runtime is trim-safe, NativeAOT-safe, or generally
reflection-safe.

## Requirement Checklist

| Requirement | Status | Evidence |
|---|---|---|
| Representative Orleans.Core generic DI activation tests | Complete | Four facts in `test/Orleans.Core.Tests/DependencyInjection/GenericRegistrationTests.cs`; all resolve real constructor-injected implementations and assert exact type plus dependency identity. |
| Representative Orleans.Runtime generic DI/factory activation tests | Complete | Six facts in `test/Orleans.Runtime.Internal.Tests/DependencyInjection/GenericRegistrationTests.cs`; includes every `FactoryUtility.Create` arity, keyed placement, singleton aliases, grain extension, and deferred startup activation. |
| Representative self-contained trimmed smoke which publishes and executes | Complete | `test/Orleans.DependencyInjection.TrimmedSmoke`; clean self-contained `win-x64` publish succeeded and the published executable exited 0. |
| Prove selected factory/registration behavior survives trimming | Complete | `ValidateCoreRegistration`, `ValidateRuntimeRegistration`, and `ValidateStartupTaskActivation` fail fast on wrong type, key, constructor dependency, stage, or execution state. |
| Avoid full-runtime trim/AOT-safety claims | Complete | Project, workflow, executable output, and this report consistently say “selected generic DI constructor flows.” |
| Reuse existing smoke infrastructure where suitable | Complete | New smoke follows the existing serialization trimmed-smoke project/workflow shape and pinned setup actions, without rooting `Orleans.Core` or `Orleans.Runtime`. |
| Modify only tests/smoke and required wiring | Complete for this work | Test-generation changes are limited to two unit-test files, two smoke files, `Orleans.slnx`, and `.github/workflows/analyzer-audit.yml`. Concurrent parent production/configuration edits were preserved and not modified by this work. |
| Record exact commands, results, files, tests, and blockers | Complete | Recorded below. |

## Generated Tests

### Orleans.Core

File: `test/Orleans.Core.Tests/DependencyInjection/GenericRegistrationTests.cs`

1. `UnitTests.DependencyInjection.CoreGenericRegistrationTests.PlacementFilter_ActivatesConstructorInjectedDirector`
2. `UnitTests.DependencyInjection.CoreGenericRegistrationTests.OptionFormatter_ActivatesConstructorInjectedFormatterAndResolver`
3. `UnitTests.DependencyInjection.CoreGenericRegistrationTests.ClientBuilder_ActivatesConstructorInjectedRetryFilterAndObserver`
4. `UnitTests.DependencyInjection.CoreGenericRegistrationTests.GrainCallFilters_ActivateConstructorInjectedImplementations`

### Orleans.Runtime

File: `test/Orleans.Runtime.Internal.Tests/DependencyInjection/GenericRegistrationTests.cs`

1. `UnitTests.DependencyInjection.RuntimeGenericRegistrationTests.FactoryUtility_Create_ActivatesConstructorsForAllSupportedArities`
2. `UnitTests.DependencyInjection.RuntimeGenericRegistrationTests.PlacementDirector_ActivatesConstructorInjectedDirector`
3. `UnitTests.DependencyInjection.RuntimeGenericRegistrationTests.ActivationRebalancer_ActivatesConstructorInjectedBackoffProvider`
4. `UnitTests.DependencyInjection.RuntimeGenericRegistrationTests.ActivationRepartitioner_ActivatesConstructorInjectedToleranceRule`
5. `UnitTests.DependencyInjection.RuntimeGenericRegistrationTests.GrainExtension_ActivatesConstructorInjectedImplementation`
6. `UnitTests.DependencyInjection.RuntimeGenericRegistrationTests.StartupTask_ActivatesConstructorInjectedTask`

### Trimmed Smoke Checks

File: `test/Orleans.DependencyInjection.TrimmedSmoke/Program.cs`

1. `ValidateCoreRegistration`
2. `ValidateRuntimeRegistration`
3. `ValidateStartupTaskActivation`

## Validation

| Command | Result |
|---|---|
| `dotnet test --project test/Orleans.Core.Tests/Orleans.Core.Tests.csproj --framework net10.0 --configuration Release --minimum-expected-tests 1` | Exit 0: 1,007 passed, 0 failed, 0 skipped. |
| `dotnet test --project test/Orleans.Runtime.Internal.Tests/Orleans.Runtime.Internal.Tests.csproj --framework net10.0 --configuration Release --minimum-expected-tests 1` | Exit 0: 318 passed, 0 failed, 13 pre-existing skips (331 discovered). |
| `dotnet test --solution Orleans.slnx --configuration Release --framework net10.0 --list-tests` | Exit 0: 12,845 tests in 44 assemblies; all ten generated facts discovered. |
| `dotnet build Orleans.slnx --configuration Release --no-incremental` | Exit 0: 0 warnings, 0 errors; 6m 36.32s. |
| `dotnet build src/Orleans.Core/Orleans.Core.csproj --framework net10.0 --configuration Release --no-incremental --no-restore -p:BuildProjectReferences=false -p:EnableCompatibilityAnalyzerAudit=true -p:CustomBeforeMicrosoftCommonTargets="$PWD/.github/scripts/analyzer-audit.targets" -p:TreatWarningsAsErrors=false -p:WarningsAsErrors=` | Exit 0: 28 existing out-of-scope warnings, 0 errors, and no IL2087/IL2091 diagnostics in `Orleans.Core`. |
| `dotnet build src/Orleans.Runtime/Orleans.Runtime.csproj --framework net10.0 --configuration Release --no-incremental --no-restore -p:BuildProjectReferences=false -p:EnableCompatibilityAnalyzerAudit=true -p:CustomBeforeMicrosoftCommonTargets="$PWD/.github/scripts/analyzer-audit.targets" -p:TreatWarningsAsErrors=false -p:WarningsAsErrors=` | Exit 0: 7 existing out-of-scope warnings, 0 errors, and no IL2087/IL2091 diagnostics in `Orleans.Runtime`. |
| `dotnet clean test/Orleans.DependencyInjection.TrimmedSmoke/Orleans.DependencyInjection.TrimmedSmoke.csproj --framework net10.0 --configuration Release --runtime win-x64` | Exit 0. |
| `dotnet publish test/Orleans.DependencyInjection.TrimmedSmoke/Orleans.DependencyInjection.TrimmedSmoke.csproj --framework net10.0 --configuration Release --runtime win-x64 --self-contained true --output Artifacts/Orleans.DependencyInjection.TrimmedSmoke/win-x64` | Exit 0; linked self-contained publish succeeded. |
| `.\Artifacts\Orleans.DependencyInjection.TrimmedSmoke\win-x64\Orleans.DependencyInjection.TrimmedSmoke.exe` | Exit 0; output: `Selected generic DI constructor flows survived the self-contained trimmed smoke.` |
| `dotnet sln Orleans.slnx list` | Exit 0; the new smoke project is registered. |
| `git diff --check -- test/Orleans.Core.Tests/DependencyInjection/GenericRegistrationTests.cs test/Orleans.Runtime.Internal.Tests/DependencyInjection/GenericRegistrationTests.cs test/Orleans.DependencyInjection.TrimmedSmoke/Orleans.DependencyInjection.TrimmedSmoke.csproj test/Orleans.DependencyInjection.TrimmedSmoke/Program.cs Orleans.slnx .github/workflows/analyzer-audit.yml` | Exit 0. |

### Analyzer Validation Qualification

An initial build which included project references and elevated only IL2087 and
IL2091 failed on the pre-existing, out-of-scope
`src/Orleans.Core.Abstractions/Runtime/GrainReference.cs(420)` IL2091 diagnostic.
No production fix was attempted because `Orleans.Core.Abstractions` is outside
the user's bounded test-generation ownership.

Target-only analyzer builds were therefore run with
`BuildProjectReferences=false`. Both completed successfully and reported no
IL2087/IL2091 diagnostics in the requested complete `Orleans.Core` and
`Orleans.Runtime` target projects. Existing IL3050 and other compatibility
warnings remain outside this request.

## Quality Review

### Static Pseudo-Mutation Review

The `test-gap-analysis` gate was invoked. Because the request prohibits
production-file edits and concurrent production edits were present, no real
mutations were injected; conclusions are static/unverified.

The generated tests should kill mutations to implementation types, keyed
service keys, singleton aliasing, factory generic types and explicit argument
ordering, and startup construction/execution. Every one of the ten bounded
production files maps to at least one exact unit test. The three selected
linked-binary paths are also exercised by the smoke.

Potential additions identified by a deliberately broader review were evaluated
as out of scope:

- Trimming all ten registration families is not required by the request for a
  representative smoke; all ten still have direct activation tests.
- Default-overload behavior does not test the generic constructor-preservation
  invariant.
- The non-generic option-formatter alias does not add evidence about preserving
  the generic implementation constructor.
- A removed call inside the smoke harness would be a mutation of test
  infrastructure, not of the bounded production code. Each invoked check is
  fail-fast and the published executable is run end to end.
- IL2087/IL2091 diagnostics are checked by target-project analyzer builds,
  independently of the runtime smoke.

No in-scope pseudo-mutation gap remains.

### Assertion Quality

The `assertion-quality` gate was invoked. The unavailable
`test-analysis-extensions` helper was noted, so xUnit v3 APIs were classified
directly.

- 10 xUnit facts contain 46 direct assertions (4.6 per test), plus two
  lifecycle guard assertions.
- The smoke contains 12 `Ensure` checks across three validation methods, plus
  two lifecycle guards.
- Zero assertion-free tests, zero trivial-only tests, zero self-referential
  assertions, and zero single-observable shortcomings were found.
- All 19 registration/factory operations assert secondary observables, usually
  exact resolved type plus constructor dependency identity. Factory tests also
  assert every supplied explicit argument.

No assertion changes were required.

## Files Added or Modified by Test Generation

- `.testagent/research.md`
- `.testagent/plan.md`
- `.testagent/status.md`
- `.github/workflows/analyzer-audit.yml`
- `Orleans.slnx`
- `test/Orleans.Core.Tests/DependencyInjection/GenericRegistrationTests.cs`
- `test/Orleans.Runtime.Internal.Tests/DependencyInjection/GenericRegistrationTests.cs`
- `test/Orleans.DependencyInjection.TrimmedSmoke/Orleans.DependencyInjection.TrimmedSmoke.csproj`
- `test/Orleans.DependencyInjection.TrimmedSmoke/Program.cs`

## Blockers

- The only blocker to a project-reference-inclusive strict IL2087/IL2091 build
  is the pre-existing out-of-scope IL2091 in
  `Orleans.Core.Abstractions/Runtime/GrainReference.cs(420)`.
- Local Windows self-contained publish/execute is validated. The added workflow
  provides the Linux self-contained publish/execute path; it was not executed
  locally on this Windows host.
- `code-testing-extensions` and `test-analysis-extensions` were unavailable;
  repository conventions and xUnit APIs were derived directly from checked-in
  files.
