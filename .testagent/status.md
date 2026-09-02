# Test Generation Status

## Outcome

The deterministic `Orleans.Connections.Security` slice adds 84 tests covering TLS options and hosting validation, duplex-pipe stream contracts, APM behavior, adapter disposal, and middleware authentication callbacks.

| Metric | Before | After | Delta |
|---|---:|---:|---:|
| Line coverage | 421 / 797 (52.82%) | 647 / 797 (81.18%) | +226 lines, +28.36 points |
| Branch coverage | 48 / 134 (35.82%) | 152 / 182 (83.52%) | +104 covered branches, +47.70 points |
| CRAP > 30 | 5 | 2 | -3 |
| Project tests | 9 | 93 | +84 |

The remaining CRAP-42 methods are:

- `CertificateLoader.LoadFromStoreCert`
- `CertificateLoader.DisposeCertificates`

The store-loading path requires an environment-independent certificate-store fixture. The private disposal helper is the strongest deterministic follow-up within this package.

The boundary review also found that `Timeout.InfiniteTimeSpan` was accepted by `TlsOptions` but converted to `TimeSpan.MaxValue`, which `CancellationTokenSource(TimeSpan)` rejects. `TlsOptions.CreateHandshakeCancellationTokenSource` now preserves the configured infinite runtime behavior by creating a non-canceling token source, and both middleware callback tests exercise that path.

## Validation

| Command | Result |
|---|---|
| `dotnet test --project test\Orleans.Connections.Security.Tests\Orleans.Connections.Security.Tests.csproj --framework net10.0 --minimum-expected-tests 1 --max-parallel-test-modules 1` | 93 passed, 0 failed |
| `dotnet test --project test\Orleans.Connections.Security.Tests\Orleans.Connections.Security.Tests.csproj --framework net8.0 --minimum-expected-tests 1 --max-parallel-test-modules 1` | 93 passed, 0 failed |
| Repository coverage collector, net10.0 | 93 passed, Cobertura generated |
| Repository coverage collector, net8.0 | 93 passed, Cobertura generated |
| `dotnet build test\Orleans.Connections.Security.Tests\Orleans.Connections.Security.Tests.csproj` | Succeeded, 0 warnings, 0 errors |
| `dotnet build Orleans.slnx -bl` | Succeeded |

Coverage artifacts are under `TestResults\focused-coverage\`.

## Gap Review

The review identified cancellation-token forwarding, stream-level APM calls, middleware callback invocation, and resource cleanup as meaningful gaps. The implementation added:

- `DuplexPipeStreamTests.CopyToAsync_ForwardsCancellationTokenToEveryRead`
- `DuplexPipeStreamTests.BeginReadAndEndRead_PreserveStateCallbackAndExactBytes`
- `DuplexPipeStreamTests.BeginWriteAndEndWrite_PreserveStateCallbackAndExactBytes`
- `TaskToApmTests.End_DelayedNonGenericTask_WaitsForCompletion`
- `TaskToApmTests.End_DelayedGenericTask_WaitsAndReturnsExactResult`
- `TlsMiddlewareTests.ClientMiddleware_InvokesAuthenticationCallbackAfterApplyingBaseOptions`
- `TlsMiddlewareTests.ServerMiddleware_InvokesAuthenticationCallbackAfterApplyingBaseOptions`

Six injected mutations were verified one at a time and reverted:

| Mutation | Killing evidence |
|---|---|
| Drop the caller token from `DuplexPipeStream.ReadAsync` | `ReadAsync_DelayedData_ReturnsExactBytes` failed |
| Drop the caller token from `DuplexPipeStream.WriteAsync` | `WriteAsync_WritesExactSelectedBytes` failed |
| Return from non-generic `TaskToApm.End` without observing the task | `End_FaultedTasks_PropagateOriginalException` failed |
| Remove `OnAuthenticateAsClient` middleware invocation | `ClientMiddleware_InvokesAuthenticationCallbackAfterApplyingBaseOptions` failed |
| Remove `OnAuthenticateAsServer` middleware invocation | `ServerMiddleware_InvokesAuthenticationCallbackAfterApplyingBaseOptions` failed |
| Construct a timed cancellation source from the accepted infinite timeout | Both `TlsMiddlewareTests` failed before invoking their callbacks |

Result: 6 of 6 injected mutations were killed. No mutation remains in the workspace.

## Assertion Review

| Metric | Value |
|---|---:|
| New tests | 84 |
| Assertions | 367 |
| Average assertions per test | 4.37 |
| Assertion-free tests | 0 |
| Trivial-only tests | 0 |

The suite uses equality/deep collection checks, boolean checks, null checks, exception and parameter-name checks, type checks, identity checks, negative assertions, and state/side-effect assertions. Two null-builder tests were removed because they encoded incidental `NullReferenceException` behavior instead of an API guarantee.

## Requirement Evidence

| Requirement | Evidence |
|---|---|
| “Invoke `coverage-analysis`, then the broad `code-testing-agent` pipeline with `.testagent` artifacts.” | The measured baseline and ranking are recorded in `.testagent\research.md`; `.testagent\plan.md` and this status file complete the broad pipeline |
| “Re-rank long-tail packages and implement the strongest deterministic next slice, preferring `Orleans.Connections.Security` TLS/duplex-pipe behavior unless current measurements show a higher-risk local target.” | Canonical ranking selected `Orleans.Connections.Security`; `DuplexPipeStreamTests`, `TaskToApmTests`, `DuplexPipeStreamAdapterTests`, and `TlsMiddlewareTests` cover the selected slice |
| “Generate strong boundary/failure tests” | `HandshakeTimeout_Zero_ThrowsArgumentOutOfRangeException`, `ReadAsync_EmptyNonCompletedResult_ThrowsInvalidOperationException`, `WriteAsync_CanceledFlushResult_ThrowsOperationCanceledException`, `End_NullResult_ThrowsArgumentNullException`, and client/silo certificate validation tests |
| “run focused coverage on net8/net10” | Both repository-instrumented coverage commands passed and produced `security-net8.0.cobertura.xml` and `security-net10.0.cobertura.xml` |
| “and the relevant/full build” | Both project and `Orleans.slnx` builds succeeded |
| “then perform gap/assertion review” | Gap review killed 6/6 injected mutations; assertion review found 0 assertion-free and 0 trivial-only tests |

## Next Ranked Packages

After this slice, the next package-level line-coverage targets from the canonical ranking are:

| Rank | Package | Line coverage | Uncovered lines |
|---:|---|---:|---:|
| 1 | `Orleans.Serialization.Abstractions` | 48.31% | 46 |
| 2 | `Orleans.EventSourcing` | 56.74% | 578 |
| 3 | `Dashboard` | 65.48% | 599 |
| 4 | `Redis` | 69.95% | 715 |
| 5 | `Orleans.BroadcastChannel` | 70.40% | 185 |
| 6 | `Orleans.Persistence.Memory` | 72.14% | 78 |
