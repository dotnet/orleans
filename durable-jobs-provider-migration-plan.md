# Durable Jobs named journal providers and migration

## Outcome

Static source audit of `dotnet/orleans` at `7193f06b53` on 2026-09-16.
This document records the current implementation and a proposed implementation plan.
Durable Jobs is new and undeployed. The design establishes its initial handle and
storage-routing contract.

| Requirement | Current implementation | Remaining work |
| --- | --- | --- |
| Journaling is the Durable Jobs storage backend | Implemented for supported hosting configuration. Startup requires the journaled shard manager and journaling services. In-memory, Azure Blob, and Azure Table helpers all use journal storage. | Preserve this invariant and centralize provider-independent registration. |
| Multiple named journal providers, explicitly selected by Durable Jobs | The underlying provider implementations support independent construction, but hosting resolves one unkeyed provider, catalog, and state-manager factory. | Add named registrations and explicit Durable Jobs selection. |
| Drain old providers while creating shards on a new provider | Replay, ownership transfer, closed shards, retry, and empty-shard deletion already exist for a single provider. | Add provider-bound shards, multi-provider discovery, a write-provider policy, and a cutover/retirement procedure. |

**Recommended model:** one write provider plus a set of draining providers. Discovery
and execution cover all selected providers. New jobs enter shards belonging to the write
provider. Existing jobs retain their original provider through completion, cancellation,
retry, and rescheduling. Shard IDs and serialized job handles retain their current shape.
Optimize for one selected provider in steady state; migration enables dual reads to locate
existing shards across the write and draining providers.

## Evidence and current guarantees

### 1. Journaling-only Durable Jobs is already implemented

- [`DurableJobsExtensions`](src/Orleans.DurableJobs/Hosting/DurableJobsExtensions.cs)
  registers `DurableJobsJournalingConfigurationValidator`.
  `UseInMemoryDurableJobs` selects `VolatileJournalStorageProvider` and
  `JournaledJobShardManager`.
- [`DurableJobsJournalingConfigurationValidator`](src/Orleans.DurableJobs/Hosting/DurableJobsOptions.cs#L293)
  requires `IJournalStorageProvider`, `IJournalStorageCatalog`,
  `IJournaledStateManagerFactory`, and `JobShardManager`, then checks that the selected
  manager is `JournaledJobShardManager`.
- [`AzureStorageDurableJobsExtensions`](src/Azure/Orleans.DurableJobs.AzureStorage/Hosting/AzureStorageDurableJobsExtensions.cs)
  wires both Blob and Table helpers to the same journaled manager and Durable Jobs JSON metadata.
- [`JournaledJobShardState`](src/Orleans.DurableJobs/JournaledJobShardState.cs)
  persists schedule, remove, retry, and snapshot records through Journaling.
  [`JournaledJobShard`](src/Orleans.DurableJobs/JournaledJobShard.cs#L383)
  completes applied mutations after the state-manager write completes.

`JobShard` and `JobShardManager` remain public abstractions. The former contains
older persistence hooks, but supported startup selects the journaled implementation.
Their API cleanup can be considered separately from this migration work.

### 2. Provider naming is the principal missing foundation

The Blob, Table, Redis, and S3 hosting extensions register a singleton concrete provider
and unkeyed aliases for storage and catalog. Their configuration uses unnamed options.
Repeated registrations of the same backend accumulate configuration onto one options
instance and retain one provider registration. Registering different backends makes
ordinary unkeyed DI resolution select the last registered service.

Sources:
[`Blob`](src/Azure/Orleans.Journaling.AzureStorage/AzureBlobStorageHostingExtensions.cs#L27),
[`Table`](src/Azure/Orleans.Journaling.AzureStorage/AzureTableStorageHostingExtensions.cs#L27),
[`Redis`](src/Redis/Orleans.Journaling.Redis/RedisJournalStorageHostingExtensions.cs),
[`S3`](src/AWS/Orleans.Journaling.S3/S3JournalStorageHostingExtensions.cs), and
[`AddFromExisting`](src/Orleans.Core/Configuration/ServiceCollectionExtensions.cs#L29).

The configuration-based Blob, Table, and Redis `GrainJournaling` builders receive a
`name` argument but configure unnamed provider options. Their `ServiceKey` selects a
keyed Azure/Redis client; storage-provider selection remains unkeyed.
See the [Blob builder](src/Azure/Orleans.Journaling.AzureStorage/AzureBlobStorageGrainJournalingProviderBuilder.cs#L12)
and [Redis builder](src/Redis/Orleans.Journaling.Redis/RedisGrainJournalingProviderBuilder.cs#L16).

Journaling's existing keyed services select format families and named durable states.
[`KeyedJournalingRegistrationTests`](test/Orleans.Journaling.Tests/KeyedJournalingRegistrationTests.cs)
exercise those keys. Storage provider names need their own registration and resolution path.

The binding must cover both metadata and journal data:

- [`JournaledJobShardManager`](src/Orleans.DurableJobs/JournaledJobShardManager.cs#L29)
  captures one storage provider, catalog, and state-manager factory.
- [`JournaledStateManagerFactory`](src/Orleans.Journaling/JournaledStateManagerFactory.cs)
  also captures one provider; `Create(JournalId)` selects only the journal identity.
- Shard creation and ownership updates use the manager's storage provider, while replay
  uses its factory. These services must resolve to the same named storage binding.
- `AddDurableJobs` currently registers runtime services; the in-memory and Azure helpers
  supply the journaled shard manager. A provider-independent selection API would also
  make catalog-capable Redis, S3, and custom journal providers usable through public hosting configuration.

### 3. Draining primitives are already present

[`JournaledJobShardManager`](src/Orleans.DurableJobs/JournaledJobShardManager.cs)
already provides:

- Fresh catalog sweeps over `jobs/shards/`, bounded by shard start time, with deduplication
  and oldest-first ordering.
- Conditional ownership claims using metadata ETags and cluster membership.
- Closed status on adopted or gracefully released populated shards.
- Replay of existing shard state and deletion of empty shards during unregister.
- A claim budget which continues returning locally owned shards after new claims exhaust the budget.

[`LocalDurableJobManager`](src/Orleans.DurableJobs/LocalDurableJobManager.cs#L80)
adds newly created shards to its writable time-bucket/stripe cache.
Discovery activates recovered shards through a separate path.
[`JournaledJobShard`](src/Orleans.DurableJobs/JournaledJobShard.cs#L187)
and [`InMemoryJobQueue`](src/Orleans.DurableJobs/InMemoryJobQueue.cs#L181)
keep retries and successful reschedules in the original shard, including due times beyond
its original time window.

Existing source tests demonstrate useful single-provider scenarios:
[`JournaledJobShardManagerTests`](test/Orleans.DurableJobs.Tests/DurableJobs/JournaledJobShardManagerTests.cs)
covers release/replay, closed-shard mutation, empty deletion, adoption, poisoning, and claim budgets;
[`JournaledJobShardDiscoveryTests`](test/Orleans.DurableJobs.Tests/DurableJobs/JournaledJobShardDiscoveryTests.cs)
covers stale metadata, ordering, horizons, cancellation, and fresh-sweep recovery;
[`JournaledJobShardStateTests`](test/Orleans.DurableJobs.Tests/DurableJobs/JournaledJobShardStateTests.cs#L71)
preserves same-shard retry beyond the original window.

### 4. Provider binding, routing, and retirement need explicit treatment

[`JobShardId`](src/Orleans.DurableJobs/JobShardId.cs) generates a timestamp/GUID identifier
and currently maps it to `jobs/shards/<id>`.
[`DurableJob.ShardId`](src/Orleans.DurableJobs/DurableJob.cs#L39), manager caches, ownership
lookups, and [remote cancellation routing](src/Orleans.DurableJobs/LocalDurableJobManager.cs#L322)
carry that identifier without a provider.

Every opened shard must retain its originating provider binding for metadata and journal
operations. Each generated shard ID identifies one shard in one authoritative provider
for its lifetime, so the existing shard-ID cache keys remain sufficient.

Discovery supplies the provider binding for execution. Cancellation on a silo with an
uncached shard uses the single selected provider directly in steady state. During
migration, direct metadata reads locate that shard among the selected providers.
The runtime retains the resolved binding separately from the serialized job handle.

Draining requires mutation access to old storage: claims, attempt-related durable updates,
retry/reschedule records, cancellation/removal, compaction, and deletion all operate there.
Far-future jobs, repeated rescheduling, active owners, poisoned shards, and failed cleanup
can extend the draining period. The normal discovery lookahead covers eligible work;
provider retirement needs a complete shard-namespace inventory.

## Proposed contracts

Names below are proposed API names.

### Named Journaling registrations

Add named overloads to the existing provider hosting APIs, for example
`AddAzureBlobJournalStorage("jobs-current", configure)`.
Use named backend options and keyed storage, catalog, and factory services.
One resolved binding supplies a coherent provider/catalog/factory set.

Keep existing unnamed overloads as the default binding. Explicit named registrations
leave that default binding unchanged, so an application's grain journaling provider can
remain independent of its Durable Jobs providers. Preserve the existing shared journal-format
policy for this change; account migration uses the existing format readers and codecs.

A provider name identifies a configured physical journal namespace. During migration,
register the new account, container, table, bucket, or key prefix under another name and
keep the original namespace selected until its shards drain. Each physical namespace
has one selected binding; operators configure consistent write and discovery bindings
on all participating silos. Names are resolved when constructing runtime bindings.

### Durable Jobs provider selection

Add a provider-independent `UseJournaledDurableJobs` entry point which installs the
journaled manager, Durable Jobs JSON metadata, and selection validation.
Configure:

- `WriteProviderName`: the binding used for all new shard creation.
- `DrainingProviderNames`: additional bindings whose existing shards are discovered and processed.

The discovery set is the write provider plus the draining providers.
Validate all selected names and their required services at startup. Treat missing or ambiguous
bindings as configuration errors with the provider name in the diagnostic.
Catalog-capable providers used by Durable Jobs satisfy the existing metadata/ETag contract;
provider contract tests establish those semantics.
Choose the single-provider fast path from this selected set, regardless of other
journal providers registered for grain state or other consumers.

Illustrative post-cutover configuration:

```csharp
siloBuilder
    .AddAzureBlobJournalStorage("jobs-original", ConfigureOriginalAccount)
    .AddAzureBlobJournalStorage("jobs-current", ConfigureCurrentAccount)
    .UseJournaledDurableJobs(options =>
    {
        options.WriteProviderName = "jobs-current";
        options.DrainingProviderNames.Add("jobs-original");
    });
```

Keep `UseInMemoryDurableJobs`, `UseAzureBlobDurableJobs`, and `UseAzureTableDurableJobs`
as convenience compositions of the common registration path.

### Shard identity and provider routing

Keep the existing timestamp/GUID shard ID, journal path, and serialized `DurableJob` shape:

```text
Shard ID:   20260916T1600000000000Z-<guid>
Journal ID: jobs/shards/20260916T1600000000000Z-<guid>
```

Creation selects the write binding, discovery supplies the catalog's binding, and each
opened shard retains that binding for metadata, replay, mutation, and deletion.
Both managers continue tracking shards by the existing ID. Retry, reschedule, snapshots,
and remote calls continue carrying the same job handle.

With one selected provider, resolve uncached shard metadata directly through the already
resolved binding. This path requires one provider metadata read and retains the current
storage access pattern. Provider selection adds neither fan-out nor a location-cache lookup
to the single-provider path.

With multiple selected providers, resolve an uncached shard by reading metadata for its
exact journal ID. Check the write provider first, then the draining providers as needed.
The one-authoritative-location invariant permits stopping when the shard is found.
This is a known-ID lookup, including for future-dated shards outside discovery lookahead.

Reuse the provider binding held by an already tracked shard. Successful migration-mode
lookups can also use a bounded location cache if repeated remote-owner lookups warrant it;
provider location remains stable while ownership can change. Keep any such cache separate
from ownership freshness, and release entries with shard lifecycle or bounded eviction.

A successful lookup binds all following operations to the provider containing the shard.
An absent-shard result requires successful absence checks from every selected provider.
Surface lookup failures explicitly; an unavailable provider represents an incomplete search.
Where a later provider supplies the shard, the single-location invariant permits using
that binding while reporting the earlier provider failure.

Dual reads apply to discovery and unresolved shard-location lookups during migration.
Scheduling and execution use provider bindings they already know.

### Creation, discovery, and execution

Bind every created/opened shard to its provider for its entire lifetime.
Create shards only through `WriteProviderName`; expose only that provider's newly created
shards to normal scheduling. Draining shards remain closed to new schedules while existing
jobs continue to mutate their original journals.

Discover all selected catalogs, retain provider identity with each entry, and apply a
single oldest-first ordering and aggregate claim budget. Deduplicate by shard ID under
the one-authoritative-location invariant. Preserve timestamp-first journal names and the
existing inclusive discovery bounds.
Reuse the existing ETag ownership protocol, membership checks, replay, and shared execution
concurrency limit.

Keep provider failures observable and isolated: report a named provider's failed sweep,
process successfully enumerated providers, and retry failed providers on the next fresh sweep.
A failed or incomplete inventory remains an unknown retirement state. Preserve yielded
assignments and cancellation/disposal behavior.

## Implementation sequence

| Phase | Changes | Acceptance criteria |
| --- | --- | --- |
| 1. Named journal bindings | Named hosting/options for Blob, Table, Redis, S3, and volatile storage; keyed storage/catalog/factories; lifecycle initialization/disposal for every binding; honor configuration-builder names. | Two instances of the same backend have independent configuration/data; each initializes once; each catalog and factory use the same storage; named registration order leaves default selection stable. |
| 2. Durable Jobs selection | Common `UseJournaledDurableJobs` registration, validated write/draining options, convenience-helper composition. | Durable Jobs selects an explicit provider independently of grain journaling; supported startup always uses journaled shards; missing names/catalog/factory fail clearly; default configuration retains existing behavior. |
| 3. Provider-bound shards and dual-read lookup | Retain IDs, handles, and cache keys; bind opened shards to their storage services; add a direct single-provider lookup path and migration-only location resolution across selected providers. | Single-provider cold lookup performs one metadata read regardless of unrelated registrations; migration locates shards in either provider; jobs scheduled in A still cancel through A after creation switches to B; future-dated shards are located without catalog discovery; failed lookups remain distinct from absence. |
| 4. Multi-provider draining | Discover all selected catalogs, global ordering/budget, provider-bound claims/replay/writes/deletion, write-provider scheduling, observable per-provider failures. | Pending jobs in A execute while new jobs create shards only in B; both survive owner loss/restart; a healthy provider progresses when another discovery fails; old storage receives completion updates and is emptied when its workload finishes. |
| 5. Cutover and retirement | Document staged deployment and provider-scoped progress; add full-namespace drain inspection and retirement criteria; update sample/playground. | Retirement inspection includes future/poisoned/owned shards and reports failures; removal requires a successful complete inventory after all writers have cut over and remaining work is resolved. |

Phases 1 and 2 establish storage selection. Phases 3 and 4 establish migration correctness.
Phase 5 makes provider retirement operationally verifiable.

### Focused verification

Extend the existing registration, shard-manager, discovery, state-replay, and local-manager
fixtures rather than creating another storage implementation.
Use separate volatile provider instances for deterministic multi-provider scenarios, then
run equivalent Blob scenarios against independent Azure namespaces.

Include schedule/cancel racing with shutdown, distinct shards across two providers, two-silo
ownership contention, stale catalog metadata, old-provider outage, future jobs beyond lookahead,
long-lived reschedules, poisoned shards, and cleanup failure.
Verify factory replay and metadata operations hit the same named provider.
Count storage calls: the single-provider cold path performs one metadata lookup, a
migration lookup stops once its shard is found, and subsequent operations use the resolved
binding. Verify unrelated registered providers receive no shard-location queries.
Cover write-provider absence followed by a draining-provider hit, absence from all selected
providers, and lookup errors that prevent establishing absence. Preserve serialization
round-trips, inclusive discovery bounds, and oldest-first ordering across both catalogs.

Run focused tests and affected package/API checks in GitHub CI. Regenerate `src/api`
through the repository's GenAPI workflow. Any approved public API break follows the normal
Release pack/suppression workflow, with narrowly scoped generated suppressions.

## Deployment and retirement

Start with startup-bound configuration and an explicit deployment cutover:

1. Configure both bindings on all silos, with A as the write provider. All participants
   can route A and B before B receives newly scheduled work.
2. Deploy B as the write provider and A as draining. Cutover is complete when all
   scheduling silos have adopted this configuration and prior scheduling requests have
   finished. For a strict time boundary, pause application scheduling during this step
   and resume after that condition is verified. During an ordinary rolling change,
   still-running A-configured silos can continue creating A shards until cutover completes.
3. Keep A available for ownership updates, execution, retries, cancellation, and cleanup.
   Monitor remaining shards/jobs, oldest outstanding work, poisoned shards, and storage
   failures by configured provider name.
4. Inspect A's complete `jobs/shards/` namespace after all writers have cut over.
   Require a successful inventory and resolution of every remaining shard, including
   future-dated and poisoned work. Repeat according to the backend's live-listing semantics.
5. After verified drain, remove A from `DrainingProviderNames` and retire its storage
   binding when other consumers have also finished with it. Durable Jobs returns to the
   direct single-provider path. Retained handles for completed jobs continue using the
   existing cancellation outcome when their shards are absent.

Rollback during migration uses provider-aware code, keeps both providers selected, and
may switch new shard creation back to A. Existing B shards continue to drain from B.
Advance automatic/hot cluster-wide switching as a separate capability if a coordinated,
zero-pause instantaneous cutover is required.

## Documentation and sample updates

- Update the core and Azure Durable Jobs package READMEs with explicit provider selection,
  the single-provider fast path, migration dual reads, draining writes, and cutover/retirement outcomes.
- Update `docs/site/src/content/docs/grains/journaling/configuration.md`, which currently
  instructs users to configure one provider, and the Azure/Redis provider configuration guidance.
- Add Durable Jobs migration guidance and provider-scoped monitoring guidance to `/docs`.
- Extend `playground/DurableJobsJournaling` with two storage bindings and an A-to-B migration
  scenario. Add a focused runnable migration sample under `/samples`.
- Keep documentation affirmative: explain where new jobs are stored, how existing jobs
  progress, when cutover completes, and how operators establish that a provider is drained.
