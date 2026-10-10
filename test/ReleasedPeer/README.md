# Released-peer retirement qualification

`Orleans.ReleasedPeer.Tests` runs current source-runtime hosts against isolated
processes using **published Orleans 10.3.1 and 10.4.0 binaries** over localhost
TCP. Each helper references only the released server package, never a source
runtime project. The tests check the loaded Core/Runtime informational versions,
assembly locations and MVIDs against the current host, preventing accidental
qualification of two copies of the current runtime.

The shared `Contracts` assembly and generated invocation/serialization code are
built with the 10.3.1 SDK. Both released helpers and current hosts use that same
assembly. Version overrides are intentional, bounded released-version test
matrix exceptions. `NuGet.Config` applies the public package source only under
this helper subtree; repository-wide feed configuration is unchanged.

## Run (Windows, repository root)

```powershell
dotnet test --project test\Orleans.ReleasedPeer.Tests\Orleans.ReleasedPeer.Tests.csproj --framework net10.0 --filter-query "/[(Provider=None)&(Suite=Functional)&(Area!=CodeGen)]" --minimum-expected-tests 8 --max-parallel-test-modules 1
```

This single narrow command restores and builds current source, the contract and
both released helpers before running the eight cases. The query matches the
repository's provider-free Functional CI partition; the tests declare its Suite,
Provider, Area and compatibility Category traits explicitly.

The modern project and all three helper/contract projects are registered exactly
once in `Orleans.slnx` for solution and project-inventory discovery. Helper projects
are non-test, non-packable applications/libraries, with released dependencies
kept package-only; documentation project policy applies only to the docs/samples
trees and needs no exception for these test projects. The modern project references build
both helpers without importing their runtime assemblies into the test process;
an after-build target copies each complete, separate dependency directory.
The current-source test host targets both net8.0 and net10.0, matching CI's
framework partitions. Released helper processes run on net10.0 and the portable
contract targets net8.0.

## Scenarios

Each scenario runs against both released versions:

- `NewCaller_OldSilo_CompletesOriginalRequestOnce`
- `OldCaller_NewSilo_CompletesOriginalRequestOnce`
- `OldClient_ThroughStableGateway_NewRetiringSilo_ReceiverForwardsOriginalRequest`
- `NewCaller_OldRetiringSilo_NewReplacement_PreservesOriginalInvocationWithoutRouteHints`

Assertions cover the transformed result, operation ID, request context, caller,
target grain, wire correlation identity, forwarding count and exactly one
application entry across all participants. Receivers own forwarding of the
original request. Typed route statuses provide advisory hints to upgraded callers;
released peers retain their existing diagnostic fields. The mixed
chain holds replacement activation below invocation admission until the released
receiver has actually stopped, then verifies that the original task completes.

The queued scenarios first hold a running method, observe the queued request
after runtime admission, and synchronously request activation deactivation with
`ShuttingDown` before releasing the running method and executing the real host
shutdown. This avoids racing lifecycle scheduling against release of the blocker.
No sleeps, timing-dependent success assumptions, external services or retries of
the application invocation are used. Timeouts only bound missing barriers and
cleanup.

`RuntimeProbe` uses test-only reflection to attach observers to each runtime's
own internal Message type while preserving existing runtime sniff/statistics
delegates.
The process protocol uses standard input/output, with serialized writes, explicit
ready/running/deactivation events and bounded process-tree cleanup.

## Upgrade order for hosted callers

These releases contain the original membership-triggered callback failure:
an old **hosted** caller can throw `SiloUnavailableException` after the retiring
receiver becomes unavailable even though the forwarded invocation executes once.
This behavior was reproduced with both actual package versions while implementing
these tests.

Upgrade hosted callers to the current runtime before relying on callback
retention across physical receiver departure. Receiver upgrades preserve wire
compatibility and receiver-owned forwarding. Upgraded callers retain the original
callback while the forwarded invocation completes within its original deadline.

The successful legacy queued-forwarding test therefore uses an actual released
**external client through a stable current gateway**. Its physical callback route
remains on the stable gateway while the activation host departs. This qualifies
receiver-owned forwarding with released outside clients. Ordinary
old-hosted-caller/new-silo calls are separately qualified.
