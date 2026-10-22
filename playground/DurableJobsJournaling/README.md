# Durable Jobs journaling playground

The Aspire app runs durable workflow grains, a web load driver, and
Prometheus/Grafana monitoring. Select journal storage using the shared
`Playground:Storage:Provider` setting:

| Value | Journal storage | Clustering |
| --- | --- | --- |
| `Azurite` (default) | Azurite Blob | Azurite Table |
| `AzuriteTable` | Azurite Table | Azurite Table |
| `StandardBlob` | Standard Azure Blob account | Table service on the standard account |
| `PremiumBlob` | Premium LRS BlockBlobStorage account | Separate standard account's Table service |
| `Table` | Azure Table | Table service on the same standard account |
| `Azure` | Alias for `PremiumBlob` | Separate standard account's Table service |

Both Blob tiers use append-blob WALs and block-blob checkpoints. Premium accounts
expose Blob resources; the AppHost removes unsupported queue/table outputs and
supplies clustering through a separate standard account.

From the repository root, with the .NET SDK and a Docker-compatible container
runtime available:

```powershell
dotnet run --project playground\DurableJobsJournaling\DurableJobsJournaling.AppHost

# To select Table journaling on the emulator:
$env:Playground__Storage__Provider = 'AzuriteTable'
dotnet run --project playground\DurableJobsJournaling\DurableJobsJournaling.AppHost
```

The AppHost passes the normalized backend to the silo and references `blobs` or
`journals` for journal data, plus `tables` for clustering. The web client uses
the clustering reference. Each app run generates a GUID-based container
(`durablejobs-...`) or journal table (`durablejobs...`) shared by its silo
replicas. Blob layout remains `wal/{journalId}` and
`checkpoints/{journalId}/{snapshotId}`, enabling catalog discovery with the
provider's default layout.

Each run registers two named journal bindings, `jobs-a` and `jobs-b`.
The names resolve independent containers (`durablejobs-...` and
`durablejobs-b-...`) or tables (`durablejobs...` and `durablejobsb...`).
By default A is the write provider and B is selected for draining, preparing
every silo to discover either namespace before B receives work.

The run-specific resources preserve journals for inspection. Azurite uses a
data volume so storage survives process and AppHost restarts. Record the
container/table name from Aspire's environment configuration and delete that
resource when the run's data is no longer needed. For restart/recovery
experiments, keep the shared resource name and workload identity stable across
the processes being restarted.

## Exercise a provider cutover

For a minimal deterministic console scenario with pass/fail checks, use the
[Durable Jobs migration sample](../../samples/DurableJobsMigration/README.md).
This playground is for interactive load and failure experiments, with
process-local workflow grain state powering its UI. Use the sample's persisted
Blob receipts to verify restart recovery.

Use a stable, lowercase alphanumeric run identifier (up to 40 characters) to
reuse both namespaces across AppHost restarts:

```powershell
$env:Playground__Storage__RunId = 'migration01'
$env:Playground__Migration__ReportInventory = 'true'
$env:Playground__Migration__ActiveProviderName = 'jobs-a'
dotnet run --project playground\DurableJobsJournaling\DurableJobsJournaling.AppHost
```

Schedule work, stop application scheduling using the load controls, and stop
the AppHost gracefully. Keep its Azurite volume and stable run identifier.
Restart with B creating new shards and A still selected for draining:

```powershell
$env:Playground__Migration__ActiveProviderName = 'jobs-b'
dotnet run --project playground\DurableJobsJournaling\DurableJobsJournaling.AppHost
```

`DrainOtherProvider` defaults to `true`. Provider selection is fixed at startup;
restart the silo to apply changes to its bindings.
For multi-silo rolling experiments, first update every scheduling silo with both
providers selected, then switch writes. A-configured silos can continue creating
A shards until their rollout and in-flight scheduling finish. Pause scheduling
for a strict cutover boundary.

With `ReportInventory=true`, silo logs report a full catalog inventory for each
selected provider every 30 seconds with a 20-second per-provider timeout.
This opt-in diagnostic adds catalog traffic. It reports total, owned,
poisoned, and unrecognized shard counts plus oldest/newest recognized shard
start times. The total includes unrecognized entries.
An inspection failure is logged as **UNKNOWN**. The reporter uses read-only
catalog and metadata operations and writes results to local silo logs.

Keep A's read/write/delete permissions until its full inventory is empty,
including future and poisoned work, all A writers have stopped, and cleanup has
succeeded. Retirement requires successful full-zero inventory plus independent
evidence of completed cluster-wide writer cutover.
Once these conditions hold, restart with
`Playground__Migration__DrainOtherProvider=false` to select only B.
Preserve A's namespace until its drain is verified. To roll back,
restart with A as write provider and B draining; existing B jobs stay in B.

Cloud backend selection enables Aspire's Azure resource configuration and can
provision chargeable resources. Use an explicitly approved Azure development
environment and its standard Aspire authentication/deployment procedure.
`PremiumBlob` configures `BlockBlobStorage` with `Premium_LRS`; standard accounts
use `Standard_LRS`.
Storage role defaults match the selected services: Blob and Table Data
Contributor for a shared standard Blob/clustering account, Table Data Contributor
for Table journals, and separate Blob-only journal and Table-only clustering
assignments for premium Blob. Generic manifest publishing retains role modules
with `principalId` and `principalType` inputs for the deployment's intended
identity. An identity-aware Aspire deployment environment can apply these defaults
to its application identities. Configure that environment and identity before
deployment; local Azurite uses its emulator credentials.
Compare backends using the same compute, region, account redundancy, job
distribution, and `Playground:DurableJobs` settings.

Open the web resource from Aspire's dashboard to configure load concurrency/rate,
start and stop load, drain outstanding work, and inspect workflow metrics.
Existing stage-write and end-to-end latency metrics, durable-job retry policy,
slow start, and scheduling tunings apply to every backend. Use
`Playground__DurableJobs__...` settings to adjust those tunings.

For a finite provider-only workload with isolated resource cleanup, operation
percentiles, and JSON/CSV reports, use the
[Azure journal benchmarks](../../test/Benchmarks/Journaling/Azure/README.md).
