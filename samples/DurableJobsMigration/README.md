# Durable Jobs provider migration

A small console sample moves new Durable Jobs from named Azure Blob provider A
to B while existing A journals drain in place. Aspire runs Azurite with a data
volume and invokes the **same console project in two sequential processes**.
Inventory and verification results appear in local console output.

## Package availability

This sample uses named journal-provider and storage-inspector APIs which require
packages built from sources containing this feature. Until the APIs are
published, build the sample using the repository's local sample package feed.
The sample build replaces the pinned package-family versions with the locally
packed version containing the APIs.

From the repository root, prepare that feed and build the sample projects:

```powershell
pwsh .\samples\Build-Samples.ps1
```

This packs the current Orleans sources and builds the sample projects against
those packages. After publication, the sample folder can be copied out and the
Orleans package versions in its `Directory.Packages.props` updated together to
the release containing the APIs.

## Run from this checkout

Prerequisites are the .NET SDK, Aspire CLI, and its Docker-compatible container
runtime for Azurite. Aspire supplies the local emulator connection.

After the local package build above, run the existing binaries with their
resolved local packages. From the repository root:

```powershell
dotnet run --no-build --no-restore --configuration Release --project samples\DurableJobsMigration\DurableJobsMigration.AppHost
```

Once packages containing these APIs are published and the pins are updated,
the copy-out workflow is `aspire run` from `DurableJobsMigration.AppHost`.

In the Aspire dashboard, watch `prepare` and then `drain` console output.
`prepare` must exit successfully before Aspire starts `drain`. A successful
run prints `VERIFIED` and both resources exit with code zero. The AppHost remains running so its dashboard
and storage can be inspected; stop it after verification.

Each AppHost invocation chooses a fresh run ID and independent A, B, and control
containers. Azurite's disk-backed data volume preserves Blob contents across
process and AppHost restarts. The prepare process exits before the drain
process starts.

## What is verified

1. `prepare` selects A for writes and B for draining, mirroring the first
   deployment stage in which every silo can locate both providers.
2. It schedules an A job due in 30 seconds and another A job due 30 seconds
   after that. Both are beyond the prepare host's five-second discovery lookahead. A complete
   inventory must include both; B must be empty.
3. It saves both unchanged `DurableJob` handles in a separate control Blob using
   Orleans serialization, releases the shards on graceful host shutdown, and
   exits. The next process recovers jobs from their journals and handles from
   the control Blob.
4. `drain` starts a new host with B for writes and A for draining. It reads the
   saved handles, confirms A storage survived, creates a new B job, and verifies
   that B now has a shard.
5. A work recovers in the new process. Its first execution deliberately fails;
   the retry occurs five seconds later, beyond its original five-second shard
   window, with the **same shard ID**. B independently executes its new work.
6. The saved future A handle is canceled after discovery loads and owns its
   shard. This phase deliberately widens discovery and activation to two minutes
   so the future shard is loaded and claimed before cancellation.
7. Blob receipts prove execution occurred in the drain process, retained the
   original shard IDs, and retried A. The sample also verifies the canceled
   future job's receipt is absent. Its executor observes the empty queue at
   the shard's start window and deletes the journal. The host waits for zero
   total shards in **both** namespaces before stopping. These bounded future
   times let the complete storage-draining demonstration finish within three minutes.

Receipts are idempotent writes keyed by job ID. They demonstrate handling
at-least-once execution: repeated attempts overwrite the same external receipt.
The receipt and job-journal mutations commit independently. Persisted Blob
receipts and complete inventories establish the sample's outcome.

Each process has a three-minute timeout and fails on a missing receipt,
unexpected routing, stalled cancellation, or inventory failure.
Inspection errors print `UNKNOWN` and fault the run. If a very slow machine
executes A work before the prepare process shuts down, use a new AppHost run with
`Migration__DueDelaySeconds=90` (accepted range 10–90 seconds).

## Applying the pattern to a deployment

The named storage options, catalog, and state-manager factory share a binding.
The default provider for grain journaling remains independent. Keep every
physical namespace under one selected name and preserve each shard's original
authoritative storage location through its final deletion.

New shards use the write provider. Existing jobs retain their original provider
for execution, retry, rescheduling, ownership, cancellation, compaction, and
deletion. Shard IDs retain their timestamp/GUID form, and serialized handles
retain their existing provider-independent shape. In steady state, one selected
provider gets a direct metadata read for an uncached known ID. Migration lookup
checks the write provider first, then draining providers as needed; failures
remain distinct from absence.

For a multi-silo migration:

- Deploy both providers to **all scheduling silos**, initially with A writing
  and B draining, before enabling B creation.
- Restart with B writing and A draining. For a strict boundary, pause
  application scheduling until every scheduling silo is updated and prior
  scheduling calls finish. A rolling change can otherwise create A shards
  until the last old writer finishes.
- Keep A's listing, read, conditional update, write, and delete permissions.
  Draining is a read/write workload.
- Require successful complete A inventories after all writers cut over.
  Resolve future-dated, owned, poisoned, and unrecognized work and cleanup
  failures. Repeat inventories as appropriate for live storage listings.
- Only then restart with B alone and retire A when other consumers are done.
  To roll back, keep both selected, switch writes to A, and drain B.

## Reading inventory

`IDurableJobsStorageInspector.InspectAsync` reads the selected provider's full
`jobs/shards/` namespace using read-only catalog and metadata operations. Its
local snapshot reports:

- `ProviderName` and the caller's `IsWriteProvider` setting.
- `ShardCount` (including unrecognized entries), `OwnedShardCount`,
  `PoisonedShardCount`, and `UnrecognizedShardCount` as 64-bit counts.
- Nullable `OldestShardStartTime` and `NewestShardStartTime`.

Counts describe shards, each of which can contain many jobs, and owned/poisoned
categories can overlap. Times describe recognized shard windows; retries and
reschedules can place future attempts beyond those windows. Retirement requires
**successful full-zero inventory plus completed cluster-wide writer cutover**.
Establish the cutover independently from deployment evidence. Treat a failed or
incomplete snapshot as unknown.

## Cleanup and safety

The sample retains named run resources and the emulator volume for inspection.
It prints its run-specific control container; A and B use
`migration-a-<run-id>` and `migration-b-<run-id>`. After both phases finish and
the inspection and receipt-retention period ends, remove only those three
run-specific containers with your storage tooling, or remove the sample's
dedicated emulator volume after stopping the AppHost. Preserve volumes shared with other
workloads.

For a real Azure deployment, replace emulator resources with explicitly
approved accounts and workload identities, budget for storage operations, and
protect operator inventory output. Apply the application's authentication and
authorization controls to operator and mutation interfaces.
