# Test Implementation Plan

## Overview

Continue the deterministic TLS coverage with a fourth, pipe-focused phase after the completed public options/hosting phases. Phase 4 directly exercises `DuplexPipeStream`, its nested `TaskToApm`/`TaskAsyncResult`, `DuplexPipeStreamAdapter<TStream>`, and `TlsDuplexPipe` using friend-assembly access and in-memory pipes only.

All tests use xUnit v3 and deterministic in-memory collaborators. The pipe-focused phase uses explicit `Pipe`, `TaskCompletionSource`, and small custom `PipeReader`/`PipeWriter`/`Stream` implementations. Production changes are limited to the friend-assembly declaration and the handshake cancellation-source fix exposed by the infinite-timeout boundary test. No certificate stores, sockets, sleeps, polling, reflection, elapsed-time assertions, or environment assumptions are used.

## Files Changed

- **Add**: `test/Orleans.Connections.Security.Tests/TlsOptionsTests.cs`
- **Add**: `test/Orleans.Connections.Security.Tests/ClientHostingExtensionsTests.cs`
- **Add**: `test/Orleans.Connections.Security.Tests/SiloHostingExtensionsTests.cs`
- **Add**: `test/Orleans.Connections.Security.Tests/HostingTestInfrastructure.cs`
- **Extend**: `test/Orleans.Connections.Security.Tests/CertificateCreator.cs` with a framework-safe helper named `CreateCertificateWithoutPrivateKey` which exports only the public certificate from a generated test certificate
- **Add**: `test/Orleans.Connections.Security.Tests/DuplexPipeStreamTests.cs`
- **Add**: `test/Orleans.Connections.Security.Tests/TaskToApmTests.cs`
- **Add**: `test/Orleans.Connections.Security.Tests/DuplexPipeStreamAdapterTests.cs`
- **Add**: `test/Orleans.Connections.Security.Tests/PipeTestInfrastructure.cs`
- **Add**: `test/Orleans.Connections.Security.Tests/TlsMiddlewareTests.cs`
- **Extend**: `src/Orleans.Connections.Security/Orleans.Connections.Security.csproj` with one centralized `InternalsVisibleTo` item for `Orleans.Connections.Security.Tests`
- **Update**: `src/Orleans.Connections.Security/Security/TlsOptions.cs` with handshake cancellation-source creation
- **Update**: client/server TLS middleware to use the handshake cancellation-source helper

No source project outside `src/Orleans.Connections.Security` and no test project outside `test/Orleans.Connections.Security.Tests` changes.

## Commands

- **Build (both target frameworks)**:
  `dotnet build test/Orleans.Connections.Security.Tests/Orleans.Connections.Security.Tests.csproj`
- **Phase 1 test**:
  `dotnet test --project test/Orleans.Connections.Security.Tests/Orleans.Connections.Security.Tests.csproj --framework net10.0 --filter-class "*TlsOptionsTests" --minimum-expected-tests 1 --max-parallel-test-modules 1`
- **Phase 2 test**:
  `dotnet test --project test/Orleans.Connections.Security.Tests/Orleans.Connections.Security.Tests.csproj --framework net10.0 --filter-class "*ClientHostingExtensionsTests" --minimum-expected-tests 1 --max-parallel-test-modules 1`
- **Phase 3 test**:
  `dotnet test --project test/Orleans.Connections.Security.Tests/Orleans.Connections.Security.Tests.csproj --framework net10.0 --filter-class "*SiloHostingExtensionsTests" --minimum-expected-tests 1 --max-parallel-test-modules 1`
- **Final scoped net10.0 test**:
  `dotnet test --project test/Orleans.Connections.Security.Tests/Orleans.Connections.Security.Tests.csproj --framework net10.0 --minimum-expected-tests 1 --max-parallel-test-modules 1`
- **Final scoped net8.0 test**:
  `dotnet test --project test/Orleans.Connections.Security.Tests/Orleans.Connections.Security.Tests.csproj --framework net8.0 --minimum-expected-tests 1 --max-parallel-test-modules 1`
- **Discovery check**:
  `dotnet test --solution Orleans.slnx --framework net10.0 --list-tests --minimum-expected-tests 1 --max-parallel-test-modules 1`
- **Lint**: No separate command. The build enforces code style and warnings as errors.

## Phase Summary

| Phase | Focus | Production files | Est. tests |
|---|---|---:|---:|
| 1 | Public `TlsOptions` defaults and boundaries | 1 | 6 methods / 9 cases |
| 2 | Client `UseTls` validation and registration | 1 | 9 |
| 3 | Silo `UseTls` validation and three registrations | 1 | 11 |
| 4 | `DuplexPipeStream`, `TaskToApm`, adapters, and `TlsDuplexPipe` | 3 | 40 |

---

## Phase 1: Deterministic `TlsOptions` Behavior

### Overview

Establish option defaults and callback conventions first, including complete boundary coverage of the CRAP-42 `HandshakeTimeout` setter. Tests are pure and require no builder or transport setup.

### Files to Test

#### 1. `TlsOptions.cs`

- **Source**: `src/Orleans.Connections.Security/Security/TlsOptions.cs`
- **Test File**: `test/Orleans.Connections.Security.Tests/TlsOptionsTests.cs`
- **Test Class**: `TlsOptionsTests`

**Members to test**:

1. `TlsOptions` construction/public defaults
   - `Constructor_SetsEveryPublicDefaultToItsDocumentedValue`
     - Assert each default independently: handshake timeout, remote-certificate mode, local certificate, server selector, application protocol collection, validator, and client/server configuration callback state.
     - Use literal/identity assertions rather than comparing with another newly constructed `TlsOptions`.

2. `TlsOptions.HandshakeTimeout`
   - `HandshakeTimeout_PositiveValue_IsStoredExactly`
     - Set a non-round positive value and assert exact `TimeSpan` equality.
   - `HandshakeTimeout_InfiniteTimeSpan_IsStoredExactly`
     - Set `Timeout.InfiniteTimeSpan` and assert exact equality.
   - `HandshakeTimeout_Zero_ThrowsArgumentOutOfRangeException`
     - Assert the exact exception type, `ParamName`, and rejected `ActualValue`.
   - `HandshakeTimeout_NegativeFiniteValue_ThrowsArgumentOutOfRangeException`
     - Use a finite negative value other than `Timeout.InfiniteTimeSpan`; assert exact exception type, `ParamName`, and `ActualValue`.

3. `TlsOptions.AllowAnyRemoteCertificate`
   - `AllowAnyRemoteCertificate_ReplacesExistingValidatorAndAcceptsEverySslPolicyError`
     - Install a rejecting validator, call `AllowAnyRemoteCertificate`, assert delegate replacement, and invoke it with generated in-memory certificate data and nonzero `SslPolicyErrors`.

4. Public client/server authentication configuration callbacks on `TlsOptions`
   - `ClientAuthenticationConfiguration_PropagatesMutationsToSslClientAuthenticationOptions`
     - Invoke the configured client callback through the public option surface and assert exact mutated underlying SSL option values and object identity.
   - `ServerAuthenticationConfiguration_PropagatesMutationsToSslServerAuthenticationOptions`
     - Invoke the configured server callback and assert exact mutated underlying SSL option values and object identity.

### Implementation Conventions

- Follow the namespace, `using` ordering, and `[Fact]`/`[Theory]` style of the existing project.
- Use `TestCertificateHelper` only when validator invocation requires certificate objects.
- Dispose every generated certificate in the test which owns it.

### Success Criteria

- [ ] `TlsOptionsTests.cs` contains all exact test names above.
- [ ] Every public default and every timeout boundary has an exact assertion.
- [ ] The phase-specific net10.0 command passes.

---

## Phase 2: Client `UseTls` Validation and Registration

### Overview

Exercise every client `UseTls` overload category through public APIs. This phase directly targets the client certificate/configure-action CRAP-42 method and verifies both service-option state and the outbound connection-builder registration.

### Test Infrastructure

- Add `test/Orleans.Connections.Security.Tests/HostingTestInfrastructure.cs`.
- Define minimal test-only recording implementations for the client builder and connection builder, backed by an in-memory `IServiceCollection`.
- The infrastructure must:
  - execute recorded service-configuration callbacks;
  - build a local service provider and resolve `IOptions<TlsOptions>` and `IOptions<ClientConnectionOptions>`;
  - record connection middleware additions without invoking internal middleware or opening a transport;
  - expose exact registration counts and preserve pre-existing connection callbacks so composition can be asserted.
- Extend `CertificateCreator.cs` with `CreateCertificateWithoutPrivateKey`; use only generated certificate bytes and return an independently disposable certificate.

### Files to Test

#### 1. `HostingExtensions.IClientBuilder.cs`

- **Source**: `src/Orleans.Connections.Security/Hosting/HostingExtensions.IClientBuilder.cs`
- **Test File**: `test/Orleans.Connections.Security.Tests/ClientHostingExtensionsTests.cs`
- **Test Class**: `ClientHostingExtensionsTests`

**Methods to test**: every public client `OrleansConnectionSecurityHostingExtensions.UseTls` overload, including the certificate/configure-action overload.

1. Argument validation
   - `UseTls_NullClientBuilder_ThrowsArgumentNullExceptionForBuilder`
     - Invoke the extension statically so null dispatch is possible; assert exact exception type and `ParamName == "builder"`.
   - `UseTls_NullCertificate_ThrowsArgumentNullExceptionForCertificate`
     - Use a valid recording builder; assert exact exception type and `ParamName == "certificate"`.
   - `UseTls_NullConfigureAction_ThrowsArgumentNullExceptionForConfigureOptions`
     - Exercise the configure-action overload; assert exact exception type and the source-declared configure parameter name.

2. Required certificate validation
   - `UseTls_WithoutLocalCertificate_ThrowsConfigurationFailure`
     - Register TLS with a no-op configure action, force options materialization, and assert the exact exception type/message emitted for the absent local certificate.
   - `UseTls_CertificateWithoutPrivateKey_ThrowsArgumentExceptionForCertificate`
     - Use `CreateCertificateWithoutPrivateKey`; assert `ArgumentException`, exact `ParamName == "certificate"`, and that no TLS registration was recorded.

3. Certificate/configure ordering and option propagation
   - `UseTls_CertificateOverload_AssignsCertificateBeforeInvokingConfigureAction`
     - Capture `TlsOptions.LocalCertificate` inside the action and assert reference identity with the supplied certificate.
   - `UseTls_ConfigureActionMutations_AppearExactlyInResolvedTlsOptions`
     - Set distinctive timeout, remote-certificate mode, and validator values; resolve options and assert each exact value/delegate identity.

4. Observable client registration
   - `UseTls_RegistersExactlyOneOutboundClientTlsConnectionCallback`
     - Resolve `ClientConnectionOptions`, invoke the outbound callback on the recording connection builder, and assert exactly one TLS middleware registration.
   - `UseTls_AppendsOutboundTlsWithoutReplacingExistingClientConnectionConfiguration`
     - Seed an existing callback, apply `UseTls`, invoke the composed callback, and assert both callbacks run once in registration order.

### Success Criteria

- [ ] All client overload categories and exact argument parameter names are covered.
- [ ] The provided certificate is demonstrably assigned before user configuration.
- [ ] Resolved TLS and client connection options contain exact expected values.
- [ ] No-private-key failure leaves no partial registration.
- [ ] The phase-specific net10.0 command passes.

---

## Phase 3: Silo `UseTls` Validation and Registration

### Overview

Cover the symmetric silo surface, including selector-only acceptance and independent verification of gateway inbound, silo inbound, and silo outbound registrations.

### Files to Test

#### 1. `HostingExtensions.ISiloBuilder.cs`

- **Source**: `src/Orleans.Connections.Security/Hosting/HostingExtensions.ISiloBuilder.cs`
- **Test File**: `test/Orleans.Connections.Security.Tests/SiloHostingExtensionsTests.cs`
- **Test Class**: `SiloHostingExtensionsTests`

**Methods to test**: every public silo `OrleansConnectionSecurityHostingExtensions.UseTls` overload, including certificate and certificate/configure-action overloads.

1. Argument and certificate validation
   - `UseTls_NullCertificate_ThrowsArgumentNullExceptionForCertificate`
     - Assert exact exception type and `ParamName == "certificate"`.
   - `UseTls_NullConfigureAction_ThrowsArgumentNullExceptionForConfigureOptions`
     - Assert exact exception type and the source-declared configure parameter name.
   - `UseTls_WithoutLocalCertificateOrSelector_ThrowsConfigurationFailure`
     - Force options materialization and assert the exact exception type/message for both certificate sources being absent.
   - `UseTls_CertificateWithoutPrivateKey_ThrowsArgumentExceptionForCertificate`
     - Assert `ArgumentException`, exact `ParamName == "certificate"`, and no partial connection registration.
   - `UseTls_ServerCertificateSelectorWithoutLocalCertificate_IsAccepted`
     - Supply a deterministic selector returning a generated certificate; assert option resolution succeeds and selector identity is retained.

2. Certificate/configure ordering and propagation
   - `UseTls_CertificateOverload_AssignsCertificateBeforeInvokingConfigureAction`
     - Assert that the configure action observes the exact supplied certificate instance.
   - `UseTls_ConfigureActionMutations_AppearExactlyInResolvedTlsOptions`
     - Set distinctive timeout, remote-certificate mode, and selector/validator values and assert exact resolved values/delegate identities.

3. Observable silo registrations
   - `UseTls_RegistersExactlyOneGatewayInboundServerTlsConnectionCallback`
     - Invoke only the gateway inbound callback and assert one server-TLS middleware registration.
   - `UseTls_RegistersExactlyOneSiloInboundServerTlsConnectionCallback`
     - Invoke only the silo inbound callback and assert one server-TLS middleware registration.
   - `UseTls_RegistersExactlyOneSiloOutboundClientTlsConnectionCallback`
     - Invoke only the silo outbound callback and assert one client-TLS middleware registration.
   - `UseTls_AppendsAllTlsCallbacksWithoutReplacingExistingSiloConnectionConfiguration`
     - Seed all three callbacks, apply `UseTls`, and assert each existing callback and corresponding TLS callback runs exactly once and in registration order.

### Success Criteria

- [ ] Missing certificate/selector, no-private-key, and selector-only paths are independently covered.
- [ ] Certificate assignment/configure ordering and exact resolved options are asserted.
- [ ] All three silo connection directions are independently observed.
- [ ] The phase-specific net10.0 command passes.
- [ ] Final net10.0, net8.0, both-TFM build, and solution discovery commands pass.

---

## Phase 4: Deterministic Pipe, APM, Adapter, and TLS Stream Behavior

### Overview

Add centralized friend-assembly access and test the internal transport contracts directly. All delayed operations are controlled by `Pipe` or `TaskCompletionSource` barriers; custom pipe primitives return exact `ReadResult`/`FlushResult` values and record advancement/completion.

### Files to Test

- `Security/DuplexPipeStream.cs` → `DuplexPipeStreamTests.cs` and `TaskToApmTests.cs`
- `Security/DuplexPipeStreamAdapter.cs` → `DuplexPipeStreamAdapterTests.cs`
- `Security/TlsDuplexPipe.cs` → `DuplexPipeStreamAdapterTests.cs`
- Shared deterministic fakes and multi-segment sequence helpers → `PipeTestInfrastructure.cs`

### Success Criteria

- [ ] Every Phase 4 test named in the traceability table below is implemented.
- [ ] Read tests assert exact bytes and exact consumed positions, including partial and multi-segment sequences.
- [ ] Callback tests assert exact callback result/state identity and synchronous-versus-delayed completion.
- [ ] Cancellation, invalid input/result, EOF, unsupported operation, copy, and completion paths assert exact outcomes.
- [ ] Both adapter constructors and both TLS constructors assert factory input and property wiring.
- [ ] Mixed synchronous/asynchronous disposal does not repeat observable completion/disposal.
- [ ] Filtered Phase 4 net10.0 tests pass, followed by the full project on net10.0 and net8.0.

---

## Acceptance Checklist Traceability

| Research acceptance item | Exact planned coverage for this turn or explicit defer reason |
|---|---|
| DuplexPipeStream paths/errors/disposal | Phase 4: `Properties_ReportReadableWritableNonSeekable`; `Read_ImmediatePartialData_ReturnsExactPrefixAndPreservesRemainder`; `Read_DelayedData_ReturnsExactBytes`; `ReadAsync_ImmediateMultiSegmentData_ConsumesExactlyReturnedBytes`; `ReadAsync_DelayedData_ReturnsExactBytes`; `ReadAsync_CompletedEmptyReader_ReturnsEof`; `ReadAsync_CanceledResult_ThrowsOperationCanceledException`; `ReadAsync_EmptyNonCompletedResult_ThrowsInvalidOperationException`; `Read_InvalidByteArrayArguments_Throw`; `ReadAsync_InvalidByteArrayArguments_Throw`; `Write_InvalidByteArrayArguments_Throw`; `WriteAsync_InvalidByteArrayArguments_Throw`; `Write_WritesExactSelectedBytes`; `WriteAsync_WritesExactSelectedBytes`; `WriteAsync_CanceledFlushResult_ThrowsOperationCanceledException`; `FlushAndFlushAsync_CanceledResult_ThrowOperationCanceledException`; `CopyToAsync_CopiesExactBytes`; `UnsupportedSeekLengthPositionAndSetLength_Throw`; `Dispose_CompletesReaderAndWriterSynchronously`; `DisposeAsync_CompletesReaderAndWriterAsynchronously`. |
| TaskToApm | Phase 4: `Begin_CompletedTask_InvokesCallbackSynchronouslyWithExactState`; `Begin_DelayedTask_InvokesCallbackAfterCompletionWithExactState`; `AsyncWaitHandle_TracksDelayedTaskCompletion`; `GetTask_ReturnsExactWrappedTask`; `End_CompletedNonGenericTask_ReturnsSuccessfully`; `End_CompletedGenericTask_ReturnsExactResult`; `End_FaultedTasks_PropagateOriginalException`; `End_CanceledTasks_PropagateCancellation`; `End_NullResult_ThrowsArgumentNullException`; `End_ForeignResult_ThrowsArgumentException`; `End_GenericWithWrongTaskResultType_ThrowsArgumentException`; `GetTask_NullOrForeignResult_ReturnsNull`. |
| Adapter and TlsDuplexPipe | Phase 4: `Adapter_DefaultConstructor_PassesItselfToFactoryAndWiresProperties`; `Adapter_ExplicitOptions_ApplyReaderWriterBehaviorAndWireProperties`; `Adapter_ReaderLeaveOpenFalse_DisposesDecoratedStream`; `Adapter_WriterLeaveOpenFalse_DisposesDecoratedStream`; `Adapter_MixedSyncAsyncDisposal_IsIdempotent`; `TlsDuplexPipe_DefaultFactory_CreatesSslStreamAndWiresPipe`; `TlsDuplexPipe_CustomFactory_ReceivesAdapterAndPreservesExactStream`; `TlsDuplexPipe_MixedSyncAsyncDisposal_IsIdempotent`. |
| TlsOptions | `Constructor_SetsEveryPublicDefaultToItsDocumentedValue`; `HandshakeTimeout_PositiveValue_IsStoredExactly`; `HandshakeTimeout_InfiniteTimeSpan_IsStoredExactly`; `HandshakeTimeout_Zero_ThrowsArgumentOutOfRangeException`; `HandshakeTimeout_NegativeFiniteValue_ThrowsArgumentOutOfRangeException`; `AllowAnyRemoteCertificate_ReplacesExistingValidatorAndAcceptsEverySslPolicyError`; `ClientAuthenticationConfiguration_PropagatesMutationsToSslClientAuthenticationOptions`; `ServerAuthenticationConfiguration_PropagatesMutationsToSslServerAuthenticationOptions`. |
| Client UseTls | `UseTls_NullClientBuilder_ThrowsArgumentNullExceptionForBuilder`; `UseTls_NullCertificate_ThrowsArgumentNullExceptionForCertificate`; `UseTls_NullConfigureAction_ThrowsArgumentNullExceptionForConfigureOptions`; `UseTls_WithoutLocalCertificate_ThrowsConfigurationFailure`; `UseTls_CertificateWithoutPrivateKey_ThrowsArgumentExceptionForCertificate`; `UseTls_CertificateOverload_AssignsCertificateBeforeInvokingConfigureAction`; `UseTls_ConfigureActionMutations_AppearExactlyInResolvedTlsOptions`; `UseTls_RegistersExactlyOneOutboundClientTlsConnectionCallback`; `UseTls_AppendsOutboundTlsWithoutReplacingExistingClientConnectionConfiguration`. |
| Silo UseTls | `UseTls_NullCertificate_ThrowsArgumentNullExceptionForCertificate`; `UseTls_NullConfigureAction_ThrowsArgumentNullExceptionForConfigureOptions`; `UseTls_WithoutLocalCertificateOrSelector_ThrowsConfigurationFailure`; `UseTls_CertificateWithoutPrivateKey_ThrowsArgumentExceptionForCertificate`; `UseTls_ServerCertificateSelectorWithoutLocalCertificate_IsAccepted`; `UseTls_CertificateOverload_AssignsCertificateBeforeInvokingConfigureAction`; `UseTls_ConfigureActionMutations_AppearExactlyInResolvedTlsOptions`; `UseTls_RegistersExactlyOneGatewayInboundServerTlsConnectionCallback`; `UseTls_RegistersExactlyOneSiloInboundServerTlsConnectionCallback`; `UseTls_RegistersExactlyOneSiloOutboundClientTlsConnectionCallback`; `UseTls_AppendsAllTlsCallbacksWithoutReplacingExistingSiloConnectionConfiguration`. These names are class-qualified as `SiloHostingExtensionsTests.*`, avoiding ambiguity with client names. |
| CertificateLoader | **Partially reached, otherwise deferred intentionally.** Public `UseTls_CertificateWithoutPrivateKey_ThrowsArgumentExceptionForCertificate` tests cover deterministic public rejection on both client and silo surfaces. Direct EKU helpers and `DisposeCertificates` are internal/private; store-backed `LoadFromStoreCert` success is environment-dependent. No store will be opened. A later centralized-access, in-memory-certificate slice must cover EKU, accessible-key, and disposal cases. |
| Middleware | `TlsMiddlewareTests.ClientMiddleware_InvokesAuthenticationCallbackAfterApplyingBaseOptions` and `TlsMiddlewareTests.ServerMiddleware_InvokesAuthenticationCallbackAfterApplyingBaseOptions` exercise the real middleware callback boundary, verify base-option ordering and connection identity, and confirm failed authentication does not invoke the next delegate. Full successful paired handshakes, feature publication, cancellation, transport restoration, and certificate-selection branches remain a later slice. |
| Determinism | Every named test above uses generated in-memory certificates and local service/recording builders. There will be no sleeps, polling, network, certificate-store access, environment assertions, or elapsed-time assertions. |
| Location | The five exact changed files listed above are all under `test/Orleans.Connections.Security.Tests`; production and all other test projects remain untouched. |
| Verification | Run each phase's exact filtered net10.0 command immediately after that phase. Then run the exact final net10.0, net8.0, both-TFM build, and solution-level net10.0 discovery commands listed under **Commands**. |
| Intentional deferrals for this turn | Only direct/private `CertificateLoader`, successful middleware handshakes and feature publication, explicit handshake cancellation, certificate-selection branches, store success, and extra cluster/socket cases remain deferred. Phase 4 covers `DuplexPipeStream`/`TaskToApm`, adapter, and `TlsDuplexPipe`; Phase 5 covers middleware authentication-callback invocation and ordering. |

## Final Completion Criteria

- [ ] Every exact test name assigned to Phases 1-3 is implemented using xUnit v3 conventions.
- [ ] Assertions verify exact values, delegate/certificate identities, counts, order, exception types, and argument parameter names.
- [ ] Generated certificates and service providers are disposed by their owning tests.
- [ ] No mock package or production implementation change is introduced; internal access is limited to one centralized `InternalsVisibleTo` declaration.
- [ ] All verification commands complete successfully on both supported target frameworks.
