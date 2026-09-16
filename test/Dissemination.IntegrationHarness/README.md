# Dissemination compatibility tests

This harness tests actual old/new runtime binaries in separate OS processes. The
controller references shared control types and loads neither Orleans runtime.
The `Dissemination compatibility` workflow builds each silo executable against
its own isolated runtime checkout and verifies the loaded assembly hashes,
versions, and paths.

The original runtime is pinned to
`6739589254b746a8790cf53524e6abe372bb53d4`. Only harness sources are copied into
that checkout; Orleans runtime sources remain unchanged.

## Coverage

- Rolling upgrades, mixed enablement, legacy fallback, and rollback.
- Exact load-state convergence after three partition/heal cycles, at four and
  eight silos, using original, current-disabled, and current-enabled runtimes.
- History-miss and same-version heartbeat repair with tree delivery, direct
  gossip, and membership-table reads independently isolated.
- Cancellation of an entered remote RPC and bounded partitioned shutdown.
- Harness guards for connection draining, exact inventory/value comparison,
  public readonly statistics fields, and outgoing-activity observation.

Commands own the load-publication cadence. Partition healing drains middleware
and transport closure at both endpoints, then establishes acknowledged
bidirectional readiness before one publication round. Recovery elapsed time
includes those probes. The disposed publication timer reference is retained for
the original runtime's shutdown lifecycle.

## Execution and artifacts

CI runs `.github\scripts\dissemination-compatibility.ps1`. Runtime builds are
CI-only. To reproduce locally, download the matching
`dissemination-compatibility-binaries` artifact into
`Artifacts\DisseminationCompatibility\bin`, then run:

```powershell
.\.github\scripts\dissemination-compatibility.ps1 -SkipBuild
```

The runner checks eight harness cases and ten process cases. Results are written
under `Artifacts\DisseminationCompatibility\results`: TRX files, invocation
metadata, scenario snapshots, process logs, and final shutdown records.

Scaling experiments and performance reports are maintained separately from this
correctness suite.
