# Microsoft.Orleans.Reminders.TestKit

Test infrastructure for validating Orleans `IReminderTable` implementations and their reminder-service integration.

Every provider executes one contract. Provider suites supply construction and external-service configuration only;
they do not disable guarantees or tune retries. The TestKit owns deterministic data generation, assertions, serialized
cardinality setup and cleanup, bounded retries, diagnostics, and failure messages.

## Contents

| Type | Purpose |
| --- | --- |
| `ReminderTableTestRunner` | Direct conformance facts for every documented table guarantee. |
| `ReminderServiceTestRunner` | Cluster-level registration, replacement, lookup, enumeration, and removal conformance. |
| `ReminderServiceLifecycleTestRunner` | Deterministic startup, ownership, exact-due, reconciliation, churn, and cleanup-isolation conformance. |
| `IReminderServiceLifecycleHarness` | Adapter contract for one cluster, one reminder clock, diagnostics, and explicit topology barriers. |
| `ReminderTableModelBasedTestRunner` | Generated sequential conformance against the same full contract. |
| `IdealizedReminderTable` | Deterministic, strongly consistent reference implementation and fault-injection oracle. |
| `ReminderTableTestFixture` | In-process cluster fixture which deploys and resolves a provider. |
| `ReminderConformanceException` / `ReminderFailureReport` | Uniform, provider-named diagnostics. |

## Contract

The shared suite requires:

- idempotent start and durable restart after stop;
- a non-empty, provider-issued ETag for every successful upsert and a different ETag for every replacement;
- exact point, grain, and range reads, including identity, UTC whole-second `StartAt`, `Period`, ETag, cardinality,
  and duplicate rejection;
- unsigned `(begin, end]` ring semantics, including wrap-around and `(0, 0]` as the full ring;
- current- and stale-ETag conditional removal, exact absence observations, and `TestOnlyClearTable`;
- concurrent same-identity replacements and parallel per-grain writes using one bounded mutation-retry policy; and
- one bounded read-convergence policy (10 seconds with 100 millisecond delays).

Retry time is measured before the first attempt, so a blocked initial operation is bounded. Timeout diagnostics retain
the provider, guarantee, operation, expected result, attempt count, last completed observation, and last exception.
Exact-cardinality setup and cleanup are serialized for every provider; concurrency is exercised by dedicated facts.

Provider suites own additional backend semantics such as conditional upsert from `ReminderEntry.ETag`,
start-cancellation timing, cross-table isolation, paging, and continuation behavior.

## Adopting the suite

Reference the TestKit privately from the provider test project:

```xml
<ProjectReference
    Include="$(SourceRoot)src\Orleans.Reminders.TestKit\Orleans.Reminders.TestKit.csproj"
    PrivateAssets="all" />
```

### Create a provider fixture

Derive from `ReminderTableTestFixture` to configure the provider in an in-process silo. The fixture starts the table,
exposes the deployed `IGrainFactory` and `IReminderTable`, and stops and disposes the cluster after the suite.

```csharp
public sealed class MyReminderTableFixture : ReminderTableTestFixture, IAsyncLifetime
{
    protected override void ConfigureSilo(ISiloBuilder siloBuilder)
        => siloBuilder.UseMyReminderProvider(options =>
        {
            options.ConnectionString = TestEnvironment.ConnectionString;
        });

    protected override void CheckPreconditionsOrThrow()
        => TestEnvironment.EnsureBackendIsAvailable();
}
```

`CheckPreconditionsOrThrow` captures external-service availability failures during fixture initialization.
Call `EnsurePreconditionsMet` from the test constructor so the test framework reports that captured result consistently.

### Run the direct contract

Derive from `ReminderTableTestRunner`, pass the resolved table and a stable provider name, then expose each shared
guarantee using the attributes of the test framework:

```csharp
public sealed class MyReminderTableTests
    : ReminderTableTestRunner, IClassFixture<MyReminderTableFixture>
{
    public MyReminderTableTests(MyReminderTableFixture fixture)
        : base(fixture.ReminderTable, "MyProvider")
    {
        fixture.EnsurePreconditionsMet();
    }

    [Fact]
    public override Task ReminderTable_UpsertRow_ReturnsNewNonEmptyETag()
        => base.ReminderTable_UpsertRow_ReturnsNewNonEmptyETag();

    [Fact]
    public override Task ReminderTable_ReadRows_Range_ExcludesBeginAndIncludesEnd()
        => base.ReminderTable_ReadRows_Range_ExcludesBeginAndIncludesEnd();

    [Fact]
    public override Task ReminderTable_ParallelUpserts_AcrossGrains_RemainIsolated()
        => base.ReminderTable_ParallelUpserts_AcrossGrains_RemainIsolated();
}
```

The built-in provider suites inherit these facts through `ReminderTableTestsBase`. A standalone suite exposes every
public guarantee on `ReminderTableTestRunner` so additions to the shared contract become visible during review.

### Run the model-based contract

The model runner generates deterministic operation sequences covering upsert, point and grain reads, unsigned range
reads, current and stale ETag removal, and table clearing:

```csharp
public sealed class MyReminderTableModelTests : IClassFixture<MyReminderTableFixture>
{
    private readonly MyReminderTableFixture fixture;

    public MyReminderTableModelTests(MyReminderTableFixture fixture)
    {
        this.fixture = fixture;
        fixture.EnsurePreconditionsMet();
    }

    [Fact]
    public Task MyProvider_ModelBasedConformance()
    {
        var options = new ReminderTableModelBasedConformanceOptions
        {
            ProviderName = "MyProvider",
            GrainType = "my-provider-reminder-tests",
            KeyPrefix = "model",
            Seed = 42,
            MaxDepth = 3,
            MaxSequenceLength = 3
        };

        return new ReminderTableModelBasedTestRunner(
            fixture.ReminderTable,
            options,
            output: Console.WriteLine)
            .RunGeneratedConformanceTests();
    }
}
```

`Seed` and `KeyPrefix` make identities and failure traces reproducible. The runner clears the table before and after
each generated case and performs a final empty-table observation before returning.

### Run the grain-facing reminder service contract

`ReminderServiceTestRunner` verifies registration, lookup, enumeration, schedule replacement, ETag replacement, and
unregistration through the public grain API. Pass the same `TimeProvider` that the silo registers for
`ReminderTimeProviderNames.Reminders` so due-time assertions use the subsystem clock:

```csharp
public sealed class MyReminderServiceTests
    : ReminderServiceTestRunner, IClassFixture<MyReminderTableFixture>
{
    public MyReminderServiceTests(MyReminderTableFixture fixture)
        : base(
            fixture.GrainFactory,
            fixture.ReminderTable,
            "MyProvider",
            seed: 42,
            reminderTimeProvider: TimeProvider.System)
    {
        fixture.EnsurePreconditionsMet();
    }

    [Fact]
    public override Task ReminderService_RegisterLookupEnumerateAndUnregister()
        => base.ReminderService_RegisterLookupEnumerateAndUnregister();

    [Fact]
    public override Task ReminderService_UpdateReplacesScheduleAndETagWithoutDuplicate()
        => base.ReminderService_UpdateReplacesScheduleAndETagWithoutDuplicate();
}
```

### Run lifecycle and churn conformance

`ReminderServiceLifecycleTestRunner` adds the shared service-level contract. The same eight scenarios run for every
service provider: startup readiness, single registration ownership, in-place schedule update, removal quiescence,
exact-due recovery, stale-owner registration reconciliation, one-silo join/leave transfer, and cleanup isolation.

Use `ReminderTestClock` as the sole time driver and `ReminderDiagnosticObserver` as the lifecycle/tick source. The
`ReminderServiceLifecycleHarness` adapter for `InProcessTestCluster` supplies explicit membership and reminder-range
reconciliation barriers:

```csharp
var observer = ReminderDiagnosticObserver.Create(); // create before deployment
var clock = builder.AddReminderTestClock();
var cluster = builder.Build();
await cluster.DeployAsync();

var options = cluster.Silos[0].ServiceProvider
    .GetRequiredService<IOptions<ReminderOptions>>().Value;
var harness = new ReminderServiceLifecycleHarness(
    cluster,
    clock,
    observer,
    options.ReminderLoadingWindow);

public sealed class MyLifecycleTests : ReminderServiceLifecycleTestRunner
{
    public MyLifecycleTests(IReminderServiceLifecycleHarness harness)
        : base(harness, "MyProvider", seed: 42)
    {
    }

    [Fact]
    public override Task ReminderService_OneSiloJoinLeaveTransfersOwnership()
        => base.ReminderService_OneSiloJoinLeaveTransfersOwnership();
}
```

Do not replace harness barriers with delays, retry loops, longer timeouts, or provider-specific skips. Scenario cleanup
unregisters only deterministic scenario identities and verifies their absence; it never clears unrelated provider rows.

## Deterministic oracle and cluster testing

`IdealizedReminderTable` supplies a strongly consistent reference implementation for TestKit self-tests and
runtime-integration tests. `UseIdealizedReminderTable` registers one shared oracle across every silo and can install a
dedicated reminder clock and reminder options:

```csharp
var builder = new InProcessTestClusterBuilder();
var reminderClock = TimeProvider.System;
var oracle = builder.UseIdealizedReminderTable(
    reminderTimeProvider: reminderClock,
    configureReminderOptions: options => options.RefreshReminderListPeriod = TimeSpan.FromSeconds(1));
var cluster = builder.Build();
await cluster.DeployAsync();

var records = oracle.Snapshot();
var operations = oracle.Operations;
```

The oracle exposes:

- `Snapshot`, `Find`, `Operations`, and `OperationCount` for deterministic state and operation assertions;
- `BlockNext` for phase barriers around a selected table operation;
- `InjectFailure` and `SetAvailable` for transient failure and outage scenarios;
- `FreezeReads` for stale-read convergence scenarios; and
- lifecycle cancellation and invariant checks.

The TestKit cluster integration suite uses these controls to cover exact-due delivery, exact-due storage recovery,
due times beyond the platform timer limit, stale-refresh suppression after unregister, and single-owner delivery in a
multi-silo cluster.

## Diagnostics and cleanup

Every conformance failure throws `ReminderConformanceException` with a `ReminderFailureReport`. Reports identify the
provider, guarantee, operation, reminder identity, expected and observed state, ETag lineage, range ownership, retry
attempts, and the final observation or exception.

Direct facts use unique deterministic identities and remove their rows. Exact-cardinality setup and cleanup are
serialized. Model-based cases clear the table at each boundary. `ReminderTableTestFixture` owns cluster shutdown and
resource disposal, including cleanup after failed deployment.

Provider-specific tests cover schema, configuration, serialization, migration, emulator, and external-service
behavior alongside the shared contract.

## Consumption

Provider test projects consume the project from source using a private project reference, matching
`Orleans.Persistence.TestKit`.
