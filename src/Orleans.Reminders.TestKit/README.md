# Microsoft.Orleans.Reminders.TestKit

Repository-internal test infrastructure for validating Orleans `IReminderTable` implementations.

Every provider executes one contract. Provider suites supply construction and external-service configuration only;
they do not disable guarantees or tune retries. The TestKit owns deterministic data generation, assertions, serialized
cardinality setup and cleanup, bounded retries, diagnostics, and failure messages.

## Contents

| Type | Purpose |
| --- | --- |
| `ReminderTableTestRunner` | Direct conformance facts for every documented table guarantee. |
| `ReminderServiceTestRunner` | Cluster-level registration, replacement, lookup, enumeration, and removal conformance. |
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

The suite deliberately does not define conditional upsert from `ReminderEntry.ETag`, start-cancellation behavior,
cross-table isolation, paging, or continuation behavior because `IReminderTable` does not specify those contracts
uniformly.

## Adopting the suite

Provider tests inherit the facts from `ReminderTableTestsBase`, or external suites can derive directly:

```csharp
public sealed class MyReminderTableTests : ReminderTableTestRunner
{
    public MyReminderTableTests(MyReminderTableFixture fixture)
        : base(fixture.ReminderTable, "MyProvider")
    {
    }

    [Fact]
    public override Task ReminderTable_UpsertRow_ReturnsNewNonEmptyETag()
        => base.ReminderTable_UpsertRow_ReturnsNewNonEmptyETag();
}
```

The model runner accepts either a provider name or `ReminderTableModelBasedConformanceOptions`. Its generated sequences
always include full unsigned range behavior, ETag rotation, conditional removal, and table clearing.

Provider-specific tests remain appropriate for schema, configuration, serialization, migration, emulator, and
external-service behavior. They supplement rather than weaken the shared contract.

## Cluster-level testing with the oracle

```csharp
var builder = new InProcessTestClusterBuilder();
var oracle = builder.UseIdealizedReminderTable();
var cluster = builder.Build();
await cluster.DeployAsync();

var records = oracle.Snapshot();
var operations = oracle.Operations;
```

The oracle supports operation gates, injected failures, frozen reads, outage simulation, and invariant checks.

## Availability

Publication is intentionally deferred, matching `Orleans.Persistence.TestKit`. Provider test projects consume the
project from source with a private project reference.
