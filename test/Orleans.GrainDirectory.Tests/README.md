# Directory protocol confidence suites

Run commands from the repository root using the SDK selected by `global.json`.

## Controlled protocol scenarios

The controlled scenarios exercise runtime directory partitions with a controllable transport and membership source. Their registration oracle follows accepted operations independently of the transition coordinator.

```powershell
dotnet test --project test\Orleans.Runtime.Internal.Tests\Orleans.Runtime.Internal.Tests.csproj --framework net10.0 --filter-class "*ControlledGrainDirectoryProtocolTests" --minimum-expected-tests 1
```

Use the scenario's phase and transport history when diagnosing a failure. A timeout identifies missing progress; the history identifies the pending transfer, membership view, or operation.

## Released/current process scenarios

```powershell
dotnet test --project test\Orleans.GrainDirectory.Tests\Orleans.GrainDirectory.Tests.csproj --framework net10.0 --filter-class "*GrainDirectoryProcessCompatibilityTests" --minimum-expected-tests 1
```

The test project's build produces two executable hosts:

- `Orleans.GrainDirectory.Compatibility.ReleaseHost` uses published Orleans **10.3.1** packages.
- `Orleans.GrainDirectory.Compatibility.CurrentHost` uses repository project references.

Both hosts use four directory partitions per silo. This exercises partition-specific system-target addressing and spreads ownership across several ring ranges when constructing owner-targeted workloads.

Each child reports its loaded runtime assembly hash, package provenance, target framework, and .NET runtime. The current child's runtime hash must match the test runner's runtime assembly. The released dependency graph must use Orleans 10.3.1 throughout.

The scenarios keep application activations on a surviving silo while their directory registrations change owners. They compare actual Orleans activation IDs and authoritative owner-local records. Both graceful replacement and paused-owner takeover run in released-to-current and current-to-released directions. The paused-owner scenario resumes the evicted child and requires evidence of its fatal self-death handling.

The process fixture owns the handles/PIDs it starts, resumes suspended children during cleanup, and reaps them before releasing resources. Suspension is implemented for Windows, Linux, and macOS. Cleanup errors are reported as failures.

Full child output is retained under the test assembly output directory in `compatibility-process-logs\<cluster-id>`. Failure messages include bounded output tails and the full log paths.

To exercise .NET 8, use `--framework net8.0`. When the required runtime is installed outside the normal `dotnet` location, set `ORLEANS_COMPAT_DOTNET_HOST` to that installation's `dotnet` executable for child processes. The runner and children must use the same runtime/architecture.

## Elastic chaos and failure attribution

```powershell
dotnet test --project test\Orleans.GrainDirectory.Tests\Orleans.GrainDirectory.Tests.csproj --framework net10.0 --filter-class "*GrainDirectoryResilienceTests" --filter-method "*ElasticChaos*" --minimum-expected-tests 1
```

`ElasticChaos` runs concurrent grain traffic with paced joins, graceful stops, and kills. Topology operations finish before the scenario establishes directory convergence and performs its next integrity probe. A final quiescent probe requires successful traffic and consistent registrations.

The five-minute topology workload runs within a ten-minute scenario deadline, including deployment and final probes. Top-level awaits observe that deadline even when an underlying operation does not complete on cancellation. A deadline failure reports the active phase and recorded topology history. Cleanup retains its independent one-minute bounds per step.

Failure reports distinguish:

- **Directory invariant evidence:** an `IntegrityViolation` diagnostic from a partition in this test's cluster, carrying the grain, silo, partition, view, range, and original exception.
- **Expected disruption:** silo-unavailable and message-rejection failures during the workload. Every fault in an aggregate must qualify.
- **Unclassified runtime/infrastructure failure:** an unexpected workload, topology, probe, or cleanup error. The original exception and phase history are preserved for attribution; this category alone does not establish a directory defect.

Normal scenario shutdown accepts cancellation of its own work. Unexpected failures remain visible, and cleanup runs even after an integrity or worker failure.

## Mutation guardrails

The focused mutation campaign exercised the following changes individually, restoring the source and rerunning the clean cases between injections:

| Deliberate defect | Detecting assertion |
| --- | --- |
| Wait for the membership view but serve before range acquisition completes | `CancellingBlockedRegistration_DoesNotCancelSharedTransition` observes a concrete registration installed before its held snapshot is released. |
| Release an outbound range before its predecessor acquisition completes | `CrashBeforeRetention_RecoversFromSurvivingActivationAndEventuallyOpensRange` observes premature release completion. |
| Install a newer snapshot before an older overlapping acquisition completes | `SamePartitionNewerSnapshot_WaitsForOlderOverlappingAcquisitionBeforeInstallingAndServing` observes prematurely installed state. |
| Omit fatal-error reporting for a failed transition | `GrainDirectoryTransitionTests` observes the missing fatal notification. |
| Treat a default execution disposition as safe to retry | `ClusterServiceOperationResultTests` rejects both default results and serialized results with the disposition field absent. |

These are targeted protocol guardrails, not a whole-repository mutation score. When extending them, use the real runtime path and an observable state or outcome assertion. The controlled fixture's membership histories preserve silo incarnations, and newer recovery state is backed by an actual accepted registration.
