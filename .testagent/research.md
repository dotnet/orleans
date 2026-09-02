# Test Generation Research

## Project Overview
- **Path**: `C:\dev\copilot-worktrees\orleans\rb-issue-10865-test-hosting-close-long-tail-package-cov-c9e442`
- **Language**: C# (`LangVersion=preview`, nullable enabled in the source project)
- **Framework**: .NET, targeting `net8.0;net10.0`
- **Test Framework**: xUnit v3 `3.2.2` on Microsoft Testing Platform `2.3.3`
- **Project system**: SDK-style
- **Dependency format and versions**: `PackageReference` with central package management in `Directory.Packages.props`; `xunit.v3.mtp-v2` 3.2.2 and MTP extensions 2.3.3 are inherited from `test/Directory.Build.props`. NSubstitute 5.3.0 is centrally versioned but is not directly referenced by this test project, so new tests should prefer small in-memory fakes and xUnit assertions.
- **New-file registration**: Implicit SDK `Compile` glob; no explicit `<Compile Include>` is required.
- **Measured baseline (user-supplied, canonical)**: 417/794 lines = 52.5%; 44/130 branches = 33.8%; 377 uncovered lines; 86 uncovered branches; 126 methods; 5 methods over CRAP 30.
- **CRAP-42 hotspots (user-supplied)**: `CertificateLoader.LoadFromStoreCert`, `CertificateLoader.DisposeCertificates`, `DuplexPipeStream.TaskToApm.TaskAsyncResult..ctor`, client `UseTls(IClientBuilder, X509Certificate2, Action<TlsOptions>)`, and `TlsOptions.HandshakeTimeout` setter.

## Dependency Graph
- **Leaf types** (no in-scope dependencies): `RemoteCertificateMode`, `OrleansApplicationProtocol`, `MemoryPoolExtensions`, `DuplexPipeStream` (including `TaskToApm`), `TlsOptions`, `CertificateLoader`, `TlsClientAuthenticationOptions`, `TlsServerAuthenticationOptions`, `TlsConnectionFeature`, and the three public TLS feature interfaces.
- **Mid-layer types** (depend on leaves): `DuplexPipeStreamAdapter<TStream>` depends on `DuplexPipeStream`; `TlsDuplexPipe` depends on the adapter; client/server TLS middleware depend on `TlsOptions`, `TlsDuplexPipe`, `TlsConnectionFeature`, `CertificateLoader`, and memory-pool helpers.
- **Top-layer types** (depend on mid-layer): `TlsConnectionBuilderExtensions` creates middleware; the client and silo `OrleansConnectionSecurityHostingExtensions.UseTls` overloads validate/configure `TlsOptions`, load certificates where requested, and register connection-builder callbacks.
- **Testing order**: Exercise public leaf behavior first (`TlsOptions` and authentication-option wrappers), then public hosting registration with observable options, then internal stream/adapter and middleware behavior using public surfaces or deliberately isolated reflection only if unavoidable.

## Build & Test Commands
- **Build (both TFMs)**: `dotnet build test/Orleans.Connections.Security.Tests/Orleans.Connections.Security.Tests.csproj`
- **Test (scoped — fix cycles)**: `dotnet test --project test/Orleans.Connections.Security.Tests/Orleans.Connections.Security.Tests.csproj --framework net10.0 --minimum-expected-tests 1 --max-parallel-test-modules 1`
- **Test (second supported TFM)**: `dotnet test --project test/Orleans.Connections.Security.Tests/Orleans.Connections.Security.Tests.csproj --framework net8.0 --minimum-expected-tests 1 --max-parallel-test-modules 1`
- **Test (harness-equivalent — discovery check)**: `dotnet test --solution Orleans.slnx --framework net10.0 --list-tests --minimum-expected-tests 1 --max-parallel-test-modules 1`
- **Lint**: No separate command; the scoped build enforces style and warnings as errors through `EnforceCodeStyleInBuild=true` and `TreatWarningsAsErrors=true`.

## Scope
- **Boundary**: Production only under `src/Orleans.Connections.Security`; all generated tests must be under `test/Orleans.Connections.Security.Tests`. Do not modify production, other test projects, or sibling source trees.
- **Targets**:
  - `Hosting/HostingExtensions.cs`
  - `Hosting/HostingExtensions.IClientBuilder.cs`
  - `Hosting/HostingExtensions.ISiloBuilder.cs`
  - `Security/CertificateLoader.cs`
  - `Security/DuplexPipeStream.cs`
  - `Security/DuplexPipeStreamAdapter.cs`
  - `Security/ITlsApplicationProtocolFeature.cs`
  - `Security/ITlsConnectionFeature.cs`
  - `Security/ITlsHandshakeFeature.cs`
  - `Security/MemoryPoolExtensions.cs`
  - `Security/OrleansApplicationProtocol.cs`
  - `Security/RemoteCertificateMode.cs`
  - `Security/TlsClientAuthenticationOptions.cs`
  - `Security/TlsClientConnectionMiddleware.cs`
  - `Security/TlsConnectionFeature.cs`
  - `Security/TlsDuplexPipe.cs`
  - `Security/TlsOptions.cs`
  - `Security/TlsServerAuthenticationOptions.cs`
  - `Security/TlsServerConnectionMiddleware.cs`
- **Representative existing tests**: `test/Orleans.Connections.Security.Tests/TlsClientAuthenticationOptionsTests.cs`; `test/Orleans.Connections.Security.Tests/TlsConnectionTests.cs`
- **Authority**: The working tree was clean when researched and its current files were treated as authoritative. No restore, reset, clean, or revert was performed.

## Files to Test

### High Priority
| File | Classes/Functions | Testability | Estimated Coverage | Notes |
|------|-------------------|-------------|-------------------|-------|
| `Security/TlsOptions.cs` | `TlsOptions`, `RemoteCertificateValidator` | High | Static-unpaired | Pure defaults, timeout boundaries, callback replacement/configuration; includes a CRAP-42 hotspot. |
| `Hosting/HostingExtensions.IClientBuilder.cs` | Client `UseTls` overloads | High | Static-unpaired | Public validation, private-key failure, configuration order, and observable connection registration; includes a CRAP-42 hotspot. |
| `Hosting/HostingExtensions.ISiloBuilder.cs` | Silo `UseTls` overloads | High | Static-unpaired | Symmetric validation plus three inbound/outbound registrations. |
| `Security/DuplexPipeStream.cs` | `DuplexPipeStream`, `TaskToApm`, `TaskAsyncResult` | High behavior / Medium access | Static-unpaired | Broad deterministic pipe behavior and one CRAP-42 hotspot; type is internal. |
| `Security/CertificateLoader.cs` | Store loader, EKU/private-key checks, disposal | Medium | Static-unpaired | Two CRAP-42 hotspots; prefer generated in-memory certificates and avoid OS stores. Internal/private helpers constrain direct access. |

### Medium Priority
| File | Classes/Functions | Testability | Estimated Coverage | Notes |
|------|-------------------|-------------|-------------------|-------|
| `Security/DuplexPipeStreamAdapter.cs` | Adapter constructors/properties/disposal | Medium | Static-unpaired | Deterministic with `Pipe`; internal type. |
| `Security/TlsDuplexPipe.cs` | Two constructors | Medium | Static-unpaired | Verify factory wiring and idempotent disposal; internal type. |
| `Security/TlsClientConnectionMiddleware.cs` | Constructor validation and handshake paths | Medium | Static-unpaired | Use paired in-memory pipes and certificates only; cancellation needs explicit barriers, never elapsed-time sleeps. |
| `Security/TlsServerConnectionMiddleware.cs` | Constructor validation, selection, handshake paths | Medium | Static-unpaired | Same deterministic in-memory constraint; internal type. |
| `Hosting/HostingExtensions.cs` | `UseClientTls`, `UseServerTls` | Medium | Static-unpaired | Null validation and middleware registration; middleware is internal. |
| `Security/TlsServerAuthenticationOptions.cs` | Wrapper properties/callback conversion | High | Static-unpaired | Public object escape hatch exposes the underlying `SslServerAuthenticationOptions`. |
| `Security/TlsClientAuthenticationOptions.cs` | Wrapper properties/callback conversion | High | Partial static pair | Existing test covers only a null-returning local-certificate callback. |
| `Security/MemoryPoolExtensions.cs` | Minimum sizes | High behavior / Low access | Static-unpaired | Small pure internal helper. |
| `Security/TlsConnectionFeature.cs` | Feature properties and certificate task | High behavior / Low access | Static-unpaired | Trivial internal state carrier. |

### Low Priority / Skip
| File | Reason |
|------|--------|
| `Security/RemoteCertificateMode.cs` | Enum only; already exercised incidentally by TLS integration theory. |
| `Security/OrleansApplicationProtocol.cs` | One internal protocol constant, best observed through authentication options/handshake tests. |
| `Security/ITlsApplicationProtocolFeature.cs` | Contract only; exercise through middleware feature publication. |
| `Security/ITlsConnectionFeature.cs` | Contract only; exercise through `TlsConnectionFeature`/middleware. |
| `Security/ITlsHandshakeFeature.cs` | Contract/default members only; exercise through middleware. |

## Existing Tests & Coverage Classification
- The required analyzer was already run once by the parent and was not rerun. Its output is a **static source-to-test naming/pairing heuristic, not line or branch coverage**.
- `Security/RemoteCertificateMode.cs` → `TlsConnectionTests.cs`: **partial/incidental**. The enum drives end-to-end theory data and options, but there are no enum-specific behaviors.
- `Security/TlsClientAuthenticationOptions.cs` → `TlsClientAuthenticationOptionsTests.cs`: **partial**. One fact verifies that the adapted local-certificate callback can return null.
- Static-unpaired: `DuplexPipeStream.cs`, `TlsOptions.cs`, `DuplexPipeStreamAdapter.cs`, `TlsDuplexPipe.cs`, all three `HostingExtensions` files, `CertificateLoader.cs`, both middleware files, `TlsServerAuthenticationOptions.cs`, `TlsConnectionFeature.cs`, all three feature interfaces, `MemoryPoolExtensions.cs`, and `OrleansApplicationProtocol.cs`.
- Static-unpaired does not imply zero runtime coverage: `TlsConnectionTests.TlsEndToEnd` indirectly traverses hosting, options, middleware, certificate, and pipe code. Use the canonical module baseline above for measured coverage; do not infer per-file percentages.

## Existing Test Projects
- **Project file**: `test/Orleans.Connections.Security.Tests/Orleans.Connections.Security.Tests.csproj`
- **Target source project**: Directly references `src/Orleans.Connections.Security/Orleans.Connections.Security.csproj`; also references `src/Orleans.TestingHost` and `test/TestInfrastructure/TestExtensions`.
- **Test files**: `TlsConnectionTests.cs`, `TlsClientAuthenticationOptionsTests.cs`
- **Helper file**: `CertificateCreator.cs` supplies `TestCertificateHelper`, including deterministic self-signed certificate creation, client/server EKU OIDs, and PFX round-tripping.
- **Internal visibility**: No `InternalsVisibleTo` for `Orleans.Connections.Security.Tests` was found in the bounded source project. `DuplexPipeStream`, `TaskToApm`, adapters, middleware, feature implementation, and key certificate helpers therefore cannot be referenced directly from normal test code. Prefer public behavioral entry points; if a later slice uses reflection, isolate type/member lookup in one test helper and assert that lookup explicitly.

## Testing Patterns
- Use xUnit v3 `[Fact]` and `[Theory]`/`[InlineData]` with exact `Assert` checks. Existing lightweight unit tests do not add category attributes; the end-to-end class uses Orleans `TestCategory`, `Trait`, `TestSuite`, `TestProvider`, and `TestArea`.
- For async work, use `TestContext.Current.CancellationToken` where appropriate. Follow `test/AGENTS.md`: no sleeps or polling; arm explicit pipe/task barriers before triggering completion; keep fixtures isolated; run both target frameworks for runtime-sensitive behavior.
- Use `TestCertificateHelper` and in-memory `Pipe`/`IDuplexPipe` fakes. Do not depend on machine certificate stores, network endpoints, environment variables, timing ranges, or installed certificates.
- Authentication wrapper tests can inspect `SslClientAuthenticationOptions`/`SslServerAuthenticationOptions` through the public object properties. Hosting tests should observe configured `ClientConnectionOptions`/`SiloConnectionOptions` and connection-builder effects rather than implementation fields.

## Recommended Slice for This Implementation Turn
Implement **public, deterministic options and hosting tests**:
1. Add `TlsOptionsTests` for defaults, positive/infinite timeout handling, zero/negative rejection, `AllowAnyRemoteCertificate`, and client/server configuration callbacks.
2. Add focused client and silo hosting-extension tests for null arguments, missing certificate configuration, certificates without private keys, configure-action ordering, and observable client/silo connection registrations.
3. Reuse generated in-memory certificates and simple builder/service fakes; add no mock package.

This slice is the strongest feasible next step because it requires no production visibility change or platform resource, covers two CRAP-42 hotspots directly (`TlsOptions.HandshakeTimeout` and client certificate `UseTls`), covers the analogous silo surface, and establishes registration assertions needed before lower-level handshake tests.

## Acceptance Checklist
- [ ] **DuplexPipeStream paths/errors/disposal**: Cover sync and async reads/writes/flush, partial and multi-segment reads, EOF, copy, cancellation, the unexpected zero-byte reader error, argument validation, unsupported seek/length/position operations, and sync/async completion/disposal.
- [ ] **TaskToApm**: Cover callback invocation for already-complete and later-completing tasks, state identity, `CompletedSynchronously`, `IsCompleted`, `AsyncWaitHandle`, `GetTask`, successful generic/non-generic `End`, fault/cancellation propagation, null/foreign result rejection, and wrong generic result rejection.
- [ ] **Adapter and TlsDuplexPipe**: Cover both constructor forms, factory input/property wiring, reader/writer options where observable, and idempotent mixed sync/async disposal.
- [ ] **TlsOptions**: Cover all defaults, positive/infinite/zero/negative timeout boundaries, remote-validator replacement by `AllowAnyRemoteCertificate`, and client/server callback/configuration propagation.
- [ ] **Client UseTls**: Separately cover null builder inputs where extension dispatch permits, null certificate/configure action, missing required local certificate, no-private-key failures (including parameter names), configure-action order, registration, and observable client connection options.
- [ ] **Silo UseTls**: Separately cover null certificate/configure action, missing certificate and selector, no-private-key failures, selector acceptance, configure-action order, all inbound/outbound registrations, and observable silo connection options.
- [ ] **CertificateLoader**: Without opening certificate stores, cover EKU absent/server/client/mismatched cases, accessible-private-key true/false, null/empty disposal, disposal of all except the selected certificate, and pure argument/failure behavior that does not vary by machine.
- [ ] **Middleware**: Use only paired in-memory transports to cover successful client/server handshake, feature publication, application protocol/certificates, transport swap/restore, next-delegate success/failure, certificate validation/selection, explicit cancellation, and authentication failure cleanup. Synchronize by tasks/pipes, never wall-clock delay.
- [ ] **Determinism**: No sleeps, polling loops, real network, OS certificate-store success assumptions, environment-sensitive assertions, or uncontrolled timing.
- [ ] **Location**: Every new test/helper remains under `test/Orleans.Connections.Security.Tests`; no production or other test-project changes.
- [ ] **Verification**: Run scoped net10.0 tests during iteration, then net8.0, the both-TFM build, and the solution-level net10.0 `--list-tests` discovery command.
- [ ] **Intentional deferrals for this turn**: Defer direct internal `DuplexPipeStream`/`TaskToApm`, adapter, `TlsDuplexPipe`, private `CertificateLoader.DisposeCertificates`, and full middleware handshake slices until after the public options/hosting slice. They lack friend-assembly access and should not be reached through scattered brittle reflection. Also defer all store-backed `LoadFromStoreCert` success tests and additional Orleans cluster/socket tests because they violate the in-memory/environment-independent requirement; the existing end-to-end test remains the integration coverage.

## Recommendations
- Generate the recommended options/hosting slice first, keeping each client and silo validation/registration requirement independently asserted.
- Treat the supplied measured baseline as the only coverage metric and the analyzer map only as prioritization evidence.
- For later internal slices, prefer public behavior through registered connection builders. If reflection is necessary because production changes remain forbidden, centralize it in one helper and do not weaken assertions.
- Do not broaden to sibling security, runtime, or test projects.
