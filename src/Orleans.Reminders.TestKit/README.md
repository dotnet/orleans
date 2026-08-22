# Microsoft.Orleans.Reminders.TestKit

Repository-internal test infrastructure for validating Orleans `IReminderTable` implementations.

The TestKit owns provider-neutral data generation, assertions, cleanup, diagnostics, and failure messages.
Provider suites supply construction and configuration only, and keep their own tests for provider-specific
schema, serialization, configuration, migration, and external-service behavior.

## Contents

| Type | Purpose |
| --- | --- |
| `ReminderTableTestRunner` | Direct conformance suite: one `ReminderTable_*` method per documented guarantee. |
| `ReminderServiceTestRunner` | Cluster-level registration, update, lookup, enumeration, and unregister conformance suite. |
| `ReminderTableModelBasedTestRunner` | Microsoft.Accordant model-based sequential conformance suite. |
| `IdealizedReminderTable` | Deterministic, strongly consistent in-memory reference implementation (the oracle). |
| `ReminderTableTestFixture` | `InProcessTestCluster` fixture which deploys a provider and resolves its `IReminderTable`. |
| `ReminderTestKitClusterBuilderExtensions` | Installs a shared reminder table (or the oracle) into every silo. |
| `ReminderTableCapabilities` | Explicit, documented provider capability switches. |
| `ReminderConformanceException` / `ReminderFailureReport` | Provider-neutral failure diagnostics. |

## The reminder table contract

Each guarantee below is documented on, and executed by, the identically named `ReminderTableTestRunner` method.

| Test method | Guarantee |
| --- | --- |
| `ReminderTable_StartAsync_IsIdempotent` | Repeated initialization leaves the table usable. |
| `ReminderTable_StopAsync_ThenRestart_ResumesService` | A stopped table can restart without losing durable rows. |
| `ReminderTable_UpsertRow_ReturnsNewNonEmptyETag` | Every successful write returns a fresh non-empty ETag. |
| `ReminderTable_UpsertRow_PersistsScheduleForPointRead` | Identity, whole-second UTC `StartAt`, `Period`, and ETag round-trip. |
| `ReminderTable_ReadRow_MissingReminder_ReturnsNull` | A missing point read returns `null`. |
| `ReminderTable_ReadRows_ForGrain_ReturnsOnlyThatGrainsReminders` | A grain read returns all and only the requested grain's reminders. |
| `ReminderTable_ReadRows_ForUnknownGrain_ReturnsEmpty` | An unknown grain returns an empty non-null result. |
| `ReminderTable_Identity_IsGrainIdAndReminderName` | Identity is the `(GrainId, ReminderName)` pair. |
| `ReminderTable_Identity_WithSpecialCharacters_RoundTrips` | Reserved characters in reminder names remain individually addressable. |
| `ReminderTable_UpsertRow_ReplacesETagOnEachWrite` | Replacement rotates the ETag and the latest read observes it. |
| `ReminderTable_RemoveRow_WithCurrentETag_RemovesRow` | Conditional removal with the current ETag succeeds. |
| `ReminderTable_RemoveRow_WithStaleETag_FailsAndRetainsRow` | A stale ETag cannot delete or modify the current row. |
| `ReminderTable_RemoveRow_WithUnknownReminderName_ReturnsFalse` | A mismatched name returns `false` and changes nothing. |
| `ReminderTable_RemoveRow_Repeated_ReturnsFalseAfterFirstSuccess` | A repeated removal reports that the row is absent. |
| `ReminderTable_UpsertRow_WithStaleETag_IsRejected` | Conditional-upsert providers reject stale writers; blind-upsert providers explicitly disable this capability. |
| `ReminderTable_UpsertRow_UpdatesStartAtAndPeriod` | A schedule update replaces one row rather than duplicating it. |
| `ReminderTable_UpsertRow_MovesReminderBetweenLoadingWindows` | Moving `StartAt` across a loading-window boundary is observable without changing identity. |
| `ReminderTable_ReadRows_FullRange_ReturnsAllReminders` | `(0, 0]` covers the full ring; `(0, uint.MaxValue]` excludes only hash zero. |
| `ReminderTable_ReadRows_Range_ExcludesBeginAndIncludesEnd` | A normal range is the half-open interval `(begin, end]`. |
| `ReminderTable_ReadRows_WrapAroundRange_ReturnsWrappedSegment` | `begin >= end` selects both sides of the ring origin. |
| `ReminderTable_ReadRows_OutsideRange_DoesNotDeleteReminder` | Absence from an ownership range is not durable deletion. |
| `ReminderTable_ReadRows_AfterRemoval_OmitsRemovedReminder` | Range enumeration explicitly observes removal while preserving siblings. |
| `ReminderTable_ReadRow_AfterRemoval_ReturnsNull` | A point read, not page absence, confirms durable deletion. |
| `ReminderTable_ConcurrentUpserts_ProduceDistinctETags` | Concurrent writes of one identity return distinct ETags. |
| `ReminderTable_ParallelUpserts_AcrossGrains_RemainIsolated` | Parallel grains retain independent rows and ETag streams. |
| `ReminderTable_TestOnlyClearTable_RemovesAllReminders` | Test cleanup removes every row and point reads confirm absence. |
| `ReminderTable_SeparatelyScopedTables_DoNotShareReminders` | Independently scoped services or clusters are isolated when the provider declares this capability. |
| `ReminderTable_StartAsync_WithCanceledToken_ThrowsOperationCanceled` | Providers which declare initialization cancellation surface `OperationCanceledException`. |

The current `IReminderTable` has no bounded-page or continuation API. This suite deliberately does not fabricate
one. Exact hash ownership, schedule replacement, stable operation ordering, and explicit deletion observations form
the executable baseline for adding due-window paging in a future interface revision.

## Adopting the suite in an external provider

```csharp
public sealed class MyReminderTableFixture : ReminderTableTestFixture, IAsyncLifetime
{
    protected override void ConfigureSilo(ISiloBuilder siloBuilder) => siloBuilder.UseMyReminderProvider();

    protected override void CheckPreconditionsOrThrow() => MyProviderEmulator.EnsureAvailable();
}

[TestCategory("Reminders"), TestCategory("MyProvider")]
public sealed class MyReminderTableTests : ReminderTableTestRunner, IClassFixture<MyReminderTableFixture>
{
    public MyReminderTableTests(MyReminderTableFixture fixture)
        : base(fixture.ReminderTable, ReminderTableCapabilities.Portable("MyProvider"))
    {
    }

    [Fact]
    public override Task ReminderTable_UpsertRow_ReturnsNewNonEmptyETag() => base.ReminderTable_UpsertRow_ReturnsNewNonEmptyETag();

    // ... override the remaining guarantees ...

    [Fact]
    public Task ReminderTable_ModelBasedGeneratedConformance()
        => new ReminderTableModelBasedTestRunner(ReminderTable, "MyProvider").RunGeneratedConformanceTests();
}
```

Capability differences are declared, not omitted. For example a provider which cannot guarantee unique ETags
for simultaneous writers sets `SupportsConcurrentUpserts = false` on its
`ReminderTableCapabilities`, which documents the deviation in code and records the disabled guarantee in
`SkippedGuarantees`.

## Cluster-level testing with the oracle

```csharp
var builder = new InProcessTestClusterBuilder();
var oracle = builder.UseIdealizedReminderTable();
var cluster = builder.Build();
await cluster.DeployAsync();

// Register a reminder through the grain, then inspect exactly what was persisted.
var records = oracle.Snapshot();
var operations = oracle.Operations;
```

The oracle also supports deterministic synchronization barriers (`BlockNext`), injected failures
(`InjectFailure`), stale read snapshots (`FreezeReads`), outage simulation (`SetAvailable`), and per-operation
invariant checks, so service-level tests never need sleeps or timing ranges.

## Failure diagnostics

Every failure is a `ReminderConformanceException` whose report identifies the provider, the violated
guarantee, the failing operation, the reminder identity and its uniform hash code, the hash range including
whether it wrapped, the current/previous/supplied ETags, the schedule, the expected and observed results, and
the full operation sequence which produced the failure.

## Availability

Publication is intentionally deferred, matching `Orleans.Persistence.TestKit`. Orleans provider test projects
consume the project from source using a private project reference:

```xml
<PackageReference Include="Microsoft.Accordant" />
<ProjectReference Include="$(SourceRoot)src\Orleans.Reminders.TestKit\Orleans.Reminders.TestKit.csproj"
                  PrivateAssets="all" />
```

The private project reference keeps the TestKit and Accordant out of downstream project dependency graphs while
copying the runtime dependencies needed by each provider test application.
