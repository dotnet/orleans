# Dissemination performance harness

This manually invoked harness measures the original Orleans runtime and a selected
candidate with dissemination explicitly off and on. Each silo is a separate OS
process using the real runtime serializer, transport, membership and load publisher.
The controller reports raw per-node costs and equal-work off/on comparisons.

## Select runtime revisions

Run the **Dissemination performance** workflow from the tooling branch with an
explicit `candidate_repository` and `candidate_ref`. Prefer a full commit SHA for
reproducibility. The initial comparison uses `ReubenBond/orleans` at
`3fc0a4610cd2eb5feef27e958fcca59658aaae47`; the original baseline is pinned to
`dotnet/orleans` at `6739589254b746a8790cf53524e6abe372bb53d4`.

```powershell
gh workflow run dissemination-performance.yml --repo ReubenBond/orleans `
  --ref <tooling-branch> `
  -f candidate_repository=ReubenBond/orleans `
  -f candidate_ref=3fc0a4610cd2eb5feef27e958fcca59658aaae47 `
  -f sizes=4,8 -f iterations=3 -f repetitions=2 `
  -f runtime_paths=All -f scenarios=stable,churn,partition `
  -f workload=ClosedLoop `
  -f silo_processor_count=0 -f gc_conserve_memory=0
```

The workflow runs only through `workflow_dispatch`. GitHub requires a workflow
to be registered on the repository's default branch before it can be dispatched.
Select trusted public runtime revisions: their build code executes on the runner
with read-only repository permissions and checkout credentials disabled.
The operator who can dispatch this workflow authorizes the exact repository/ref to
execute. Inspect that revision's build inputs and dependencies before dispatch;
selected code has the hosted runner's network access. Results record both the
requested ref and resolved commit for audit.

CI creates separate original and candidate checkouts, verifies their origins and
clean state, and copies only `Silo` and `Shared` support sources into each checkout.
Before creating worker files, the copy step validates every existing destination
parent through the checkout, rejecting symlinks and reparse points within it.
Each worker builds against that checkout's runtime projects and package settings.
The controller builds against the tooling checkout's shared protocol, instrumentation
and reflection-boundary helpers.
The tooling branch can therefore be based on main while the selected candidate
provides the dissemination APIs. `NewRuntime.cs` describes the expected internal API
shape; update this adapter deliberately when testing a candidate with changed APIs.
The shared publisher adapter validates the publication method's signature and the
disposable timer field, reporting the incompatible member and selected assembly.
Aggregation-tree reports use the optional public `Overlay.AggregationFanOutFactor`
property when present, clamped to the candidate's effective range
`1..max(1, memberCount)`. Older candidates such as `3fc0a461` use their membership
fanout formula. `FanoutSource` names the detected capability; an incompatible
property shape raises an adapter diagnostic. Membership routing retains its selector.

Published manifests record DLL hashes, versions and source commits. Every worker
checks the loaded Orleans assemblies and exact process identity. Candidate off/on
runs share one binary directory. `selection.json` preserves the resolved candidate,
original and tooling commits and build SDK information.

## Workload and bounds

Defaults are 4 and 8 processes, three rounds, two paired repetitions, and all three
paths across `stable`, `churn` and `partition`. Sizes accept one to six distinct
counts from 3 through 128; rounds accept 3 through 200; repetitions accept 1 through
5. Successive repetitions reverse the complete execution order, including off/on.

`workload=ClosedLoop` preserves the publication/convergence barrier after each round.
For fixed-rate stable measurement, choose `OpenLoopSynchronized` or
`OpenLoopStaggered`, `scenarios=stable`, 3 through 32 processes, and `iterations`
from 3 through 30 (duration in seconds at one publication per producer per second).
Synchronized producers share each one-second boundary; staggered producers use
evenly spaced phases within that second. Each worker arms a local monotonic schedule
at a common future UTC epoch, then calls the production publisher on its scheduler.
Controller polling observes state independently of these publication deadlines.

```powershell
gh workflow run dissemination-performance.yml --repo ReubenBond/orleans `
  --ref <tooling-branch> -f candidate_repository=ReubenBond/orleans `
  -f candidate_ref=<candidate-commit> -f sizes=4,8,32 -f iterations=10 `
  -f repetitions=2 -f runtime_paths=Current -f scenarios=stable `
  -f workload=OpenLoopStaggered -f silo_processor_count=0 -f gc_conserve_memory=0
```

Arming must finish within the five-second lead. A producer fails on a missed period
or a publication extending past its one-second slot, preserving partial timestamps,
missed-period and overrun counts. Each accepted run offers exactly
`processes * durationSeconds` publications. Producers retain the configured rate
without catch-up bursts. The controller has a separate ten-second completion and
latest-state drain after the scheduled end. It verifies active process identities,
exact latest dictionary inventory and values, and that observed versions and values
were actually published.

Open-loop artifacts preserve planned/actual start and completion timestamps,
actual inter-publication periods, first-observed peer ages, and each producer's
final value reaching every process. Ages and final-latest latency are **polling
upper bounds**, using same-host UTC timestamps and 100 ms controller polling.
Intermediate values may coalesce; observed and unobserved publication/observer pairs
are reported separately. Exact latest-state convergence remains required.

Every round invokes the actual load publisher once per active node and checks exact
state inventory and field values at every node. Churn restarts a process at the same
endpoint. Partition recovery drains connections at both endpoints, including
unfinished handshakes, then confirms bidirectional acknowledged control RPCs before
one publication round. This preserves the #11243 transport-readiness ordering.
Readiness time and RPC costs stay inside recovery and total cost windows.

Automatic load publication is paused by disposing its timer on the publisher
scheduler. The timer field retains its disposed reference for original-runtime
shutdown. Enabled runs explicitly opt into the subsystem and both namespaces while
retaining the candidate's production dissemination tuning defaults. Offered rounds
begin after peer support confirmation; replacement startup and support confirmation
remain inside churn costs.

The Linux/Windows memory guard plans 128 MiB per silo plus 1 GiB controller headroom.
Commands, startup, shutdown and processes have deadlines; the workflow has a
90-minute budget. For an explicitly requested large stable curve, select
`sizes=8,16,32,64,100`, `runtime_paths=Current`, `scenarios=stable`,
`silo_processor_count=1`, `gc_conserve_memory=9`, and two repetitions on a suitably
provisioned host. These controls are equal across each off/on pair.

## Results and interpretation

`Artifacts\DisseminationPerformance\results\<UTC>-<unique-id>` contains raw
`cost-results.json`, normalized `comparisons.json`, `selection.json`, invocation,
methodology, host resources, TRX, and per-cluster logs and snapshots. The workflow
also preserves build binlogs and `bin\{Old,New,Runner}` as separate downloadable
artifacts. Results retain partial samples and explicit incomplete pairs on failure.

Metrics include RPC/message counts, complete serialized bytes, socket and transport
bytes, CPU, allocations, retained memory, publication/convergence/recovery latency,
and per-node topology. Load broadcast requests and logical value transmissions are
separate counters. Reports compare equal binary, environment, process count and
offered work, with costs per publication and median/P95 convergence.
Open-loop comparisons use median/P95 final-latest polling upper bounds instead of
per-round closed-loop latency. Raw `open-loop-{plans,producers,observations,summary}.json`
files preserve cadence and observation evidence. Total costs include arming, the
fixed-rate window, final-state drain, and the common socket-sampling margin.

These are shared-runner, single-host loopback measurements. The workload is
closed-loop by default, with a convergence barrier after each round. The optional
open-loop mode fixes the offered producer rate at 1 Hz. Silo costs include control
and instrumentation work; controller costs, storage disk bytes, cross-machine latency,
TCP/IP wire overhead and application-grain workloads need separate experiments.
The 100 ms polling cadence and one-second socket sampling introduce boundary
uncertainty. Retained memory represents post-GC live bytes on surviving nodes.
Original-to-candidate changes include all intervening commits; same-candidate off/on
pairs isolate enablement. See [methodology.json](methodology.json) for exact windows.

## Local reproduction and focused checks

Download `dissemination-performance-binaries` from a successful dispatch into
`Artifacts\DisseminationPerformance\bin` in the same tooling revision. Install a
matching .NET 10/ASP.NET Core shared framework for Linux or Windows, then replay
the exact repository/ref recorded in `bin\selection.json`:

```powershell
.\.github\scripts\dissemination-performance.ps1 -SkipBuild `
  -CandidateRepository ReubenBond/orleans `
  -CandidateRef 3fc0a4610cd2eb5feef27e958fcca59658aaae47 `
  -Sizes '4,8' -Iterations 3 -Repetitions 2 `
  -RuntimePaths All -Scenarios 'stable,churn,partition'
```

`-ArtifactsDirectory` accepts a single named directory under `Artifacts`; paths
accept either slash spelling and are assembled from platform-native components.
Existing links/reparse points are rejected. Builds require fresh binary output;
each replay creates its own results directory.

Run only the lightweight controller measurement cases locally:

```powershell
.\.github\scripts\test-dissemination-performance.ps1
dotnet test --project test\Dissemination.PerformanceHarness\Tests\Dissemination.PerformanceHarness.Tests.csproj `
  --framework net10.0 --filter-class '*MeasurementTests' --minimum-expected-tests 50
```

Ordinary Linux/Windows CI runs script checks for repository containment, worker-copy
coexistence, baseline-pin consistency and replay paths with worker execution stubbed.
`ProviderTests\FileMembershipTableTests.cs` is linked into `Orleans.Runtime.Tests`
alongside the file-backed provider and its protocol. These BVT cases exercise
persisted membership cleanup: all expired non-active statuses are removed, while
active rows and rows updated at or after the cutoff retain their ETags. Cleanup uses
the selected runtime's `EffectiveUpdateTime`, including startup, heartbeat and
suspect-vote timestamps.
Clearing membership advances the table ETag so writes from an earlier snapshot
are rejected. Heartbeat dirty writes preserve the table version and update only
the liveness timestamp.

```powershell
dotnet test --project test\Orleans.Runtime.Tests\Orleans.Runtime.Tests.csproj `
  --framework net10.0 --filter-class 'Orleans.Dissemination.PerformanceHarness.FileMembershipTableTests' `
  --minimum-expected-tests 12
```

The scaling test runs through the script's explicit manual-run marker and published
binary selection. The measurement controller retains its independent build using
shared protocol and instrumentation sources.
