# Test Generation Research

## Project Overview
- **Path**: `C:\dev\copilot-worktrees\orleans\rb-issue-10861-test-providers-raise-cloud-provider-cove-c07538`
- **Repository state**: current `upstream/main` descendant; merged PR #10952 is commit `607fe8a5828b4973453a17b7050c7fe9588956de` (`test(aws): cover DynamoDB configuration (#10952)`).
- **Language**: C#/.NET; Orleans with AWSSDK.DynamoDBv2.
- **Test Framework**: xUnit v3 `3.2.2` on Microsoft Testing Platform `2.3.3`.
- **Project system**: SDK-style.
- **Dependency format and versions**: central `PackageReference`; AWSSDK.DynamoDBv2 `4.0.14`. `NSubstitute` `5.3.0` is centrally versioned but is not referenced by either bounded test project.
- **Target frameworks**: `net8.0;net10.0` from `test/Directory.Build.props`.
- **New-file registration**: implicit SDK glob for a normal test file; no `<Compile Include>` is needed. `DynamoDBStorage.cs` itself is deliberately linked using explicit compile items.
- **Guidance read**: `test/AGENTS.md` and the .NET unit-test extension. Tests must isolate state, avoid sleeps/polling, use controlled synchronization, run both TFMs where behavior can differ, and verify harness discovery.

## Scope
- **Boundary**: only `src/AWS/Shared/Storage/DynamoDBStorage.cs`, its linked inclusions, relevant AWS/DynamoDB test projects and seams, manifests, MTP categorization/commands, and coverage tooling. Provider siblings are excluded.
- **Production target**: `src/AWS/Shared/Storage/DynamoDBStorage.cs`.
- **Named methods**: `UpdateTableAsync`, `TableIndexWaitOnStatusAsync`, `PutEntriesAsync`, both `WriteTxAsync` overloads, and `ConvertUpdate`.
- **Adjacent behavior**: `InitializeTable`, `TableWaitOnStatusAsync`, `TableUpdateTtlAsync`, `TableCreateSecondaryIndex`, and `UpsertEntryAsync` only for conditional updates, mutation shape, cancellation, and AWS failures.
- **Representative tests**:
  - `test/Extensions/Orleans.AWS.Tests/StorageTests/DynamoDBStorageCancellationTests.cs`
  - `test/Extensions/Orleans.AWS.Tests/StorageTests/DynamoDBGrainStorageProviderBuilderTests.cs`

## Bounded Target Inventory

| Priority | File/type | Role | Testability |
|---|---|---|---|
| High | `src/AWS/Shared/Storage/DynamoDBStorage.cs` / `DynamoDBStorage` | All requested behavior | High for public mutations; medium for private table-state methods via reflection and fake client |
| Supporting | `test/Extensions/Orleans.AWS.Tests/StorageTests/UnitTestDynamoDBStorage.cs` | Existing emulator-backed subclass | Medium; simulator-dependent and does not expose the client |
| Supporting | `test/Extensions/Orleans.AWS.Tests/StorageTests/DynamoDBStorageTests.cs` | Existing CRUD/conditional functional tests | Emulator-dependent |
| Supporting | `test/Extensions/Orleans.AWS.Tests/StorageTests/DynamoDBStorageCancellationTests.cs` | Credential-free pre-cancellation test | High |
| Supporting | `test/Transactions/Orleans.Transactions.DynamoDB.Test/DynamoDBTransactionalStateStorageTests.cs` | Indirect use of public production copy | Emulator-dependent |

## Linked-Source Inclusion
- The physical source is linked into:
  - `src/AWS/Orleans.Clustering.DynamoDB/Orleans.Clustering.DynamoDB.csproj`
  - `src/AWS/Orleans.Persistence.DynamoDB/Orleans.Persistence.DynamoDB.csproj`
  - `src/AWS/Orleans.Reminders.DynamoDB/Orleans.Reminders.DynamoDB.csproj`
  - `src/AWS/Orleans.Transactions.DynamoDB/Orleans.Transactions.DynamoDB.csproj`
  - `test/Extensions/Orleans.AWS.Tests/Orleans.AWS.Tests.csproj`
- Conditional symbols give each production copy a different namespace. Only `TRANSACTIONS_DYNAMODB` makes `DynamoDBStorage` public; the `AWSUTILS_TESTS` copy is internal and partial.
- The AWS test project could add a test-only partial declaration to expose private members. However, `.github/coverage.config.xml` sets `<IncludeTestAssembly>False</IncludeTestAssembly>`, so hits in `Orleans.AWS.Tests.dll` are not canonical production-coverage evidence.
- Prefer the public production copy in `Orleans.Transactions.DynamoDB`, tested from `Orleans.Transactions.DynamoDB.Tests`. Inject its private concrete client and invoke private methods by reflection. This records production-assembly hits without production changes.

## Dependency Graph
- **Leaf type**: `DynamoDBStorage`; it is the only in-scope production type and has no in-scope dependencies.
- **External dependencies**: `AmazonDynamoDBClient`, AWS models, `ILogger`, `AWSUtils`, and `Task.Delay`.
- **Mid/top layer**: none in scope.
- **Test double feasibility**: a hand-written `AmazonDynamoDBClient` subclass works. In AWSSDK `4.0.14`, the class is not sealed, has a public parameterless constructor, and required `DescribeTableAsync`, `DescribeTimeToLiveAsync`, `UpdateTableAsync`, `UpdateItemAsync`, `BatchWriteItemAsync`, and `TransactWriteItemsAsync` methods are virtual/non-final.

## Existing Seams and Deterministic Feasibility
- **Credential-free client seam**: construct storage with an HTTP service URL (dummy credentials), replace private `_ddbClient` using reflection, and capture complete requests/tokens in an overridden client.
- **Private-method seam**: invoke `UpdateTableAsync` and `TableIndexWaitOnStatusAsync` by reflection; pass `delay: 0` to the latter. Missing-index creation still executes the production method's fixed one-second stabilization delay per index.
- **Public seam**: `InitializeTable` can route through update after a scripted describe response. `ConvertUpdate`, `PutEntriesAsync`, `WriteTxAsync`, and `UpsertEntryAsync` are public on the transaction copy.
- **Simulator seam**: `AWSTestConstants.DynamoDbService` plus `UnitTestDynamoDBStorage`; tests skip if DynamoDB Local is unavailable. CI starts `amazon/dynamodb-local:latest` on `127.0.0.1:8000`.
- **Use emulator only for** server-side expression evaluation, real transaction cancellation reasons, AWS request validation/marshalling, or eventual-consistency behavior. Request construction, branch flow, token propagation, and service failures are deterministic with the fake.
- **Async limitation**: `PutEntriesAsync` and both `WriteTxAsync` overloads return the client task without awaiting it. Their `catch` blocks observe synchronous throws only. Faulted/canceled returned tasks propagate but bypass those catch/log branches. They expose no cancellation-token parameter.

## Baseline Metrics (verbatim)

| Method | Baseline physical coverage | Complexity | CRAP |
|---|---:|---:|---:|
| `UpdateTableAsync` | 56.9% | 54 | 287.52 |
| `TableIndexWaitOnStatusAsync` | 0% | 14 | 210 |
| `PutEntriesAsync` | 0% | 6 | 42 |
| `WriteTxAsync` | 65.5% | 16 | 26.5 |
| `ConvertUpdate` | 69.7% | 16 | 23.12 |

Issue #10861 requests named hotspots to reach at least 80% line and 70% branch coverage with CRAP below 30, and retained provider families to reach 90% line coverage or have a tracked rationale/follow-up. `UpdateTableAsync` has complexity 54, so coverage alone cannot reduce its CRAP score below 54.

## Exact Branch Inventory and Feasible Seams

### `UpdateTableAsync`
1. `_updateIfExists == false` logs and returns without a client call.
2. Status outside `{CREATING, UPDATING, ACTIVE}` throws before the method's `try`.
3. `CREATING` and `UPDATING` enter the initial table wait; `ACTIVE` skips it.
4. Provisioned mode supplies `PROVISIONED`, throughput, and per-existing-GSI update actions; on-demand mode supplies `PAY_PER_REQUEST`, null throughput, and null GSI updates.
5. Capacity condition short-circuits across requested read differs, requested write differs, existing read/write both nonzero while switching to on-demand, or no term matches.
6. Capacity change calls update and waits `UPDATING -> ACTIVE`; no change skips both.
7. TTL branches: non-disabled/wrong attribute warning; empty/already-correct no-op; enable plus wait; swallowed `AmazonDynamoDBException`.
8. Existing GSI collection is null/non-null. Each index is `CREATING`, `UPDATING`, or another status; only the first two wait.
9. Requested indexes are null/empty, already present, or missing; only missing names call `TableCreateSecondaryIndex`, in order.
10. Cancellation from a client/wait/delay is rethrown when the supplied token is canceled. Other AWS/service failures are logged and rethrown.

Use scripted descriptions and captured requests. Invoke privately for isolation; use `InitializeTable` for one routing test. Keep wait delay zero.

### `TableIndexWaitOnStatusAsync`
1. First description has no matching GSI: loop exits without delay.
2. First index already differs from `whileStatus`: loop exits without delay.
3. Index equals `whileStatus`: fetch again; subsequent iterations enter the delay branch.
4. Desired status null: validation is skipped, including for a missing index.
5. Desired status non-null and matching: return.
6. Desired status non-null with missing/wrong index: throw.
7. Describe service failure and cancellation propagate; cancellation during delay also propagates.

Invoke with `delay: 0`; queue deterministic `DescribeTableResponse` values.

### `PutEntriesAsync`
1. Null collection throws `ArgumentNullException`.
2. Empty collection returns `Task.CompletedTask` without a client call.
3. Non-empty input creates one `PutRequest` per item, preserving order/reference identity, under exactly the requested table key.
4. Client success, synchronous throw, faulted task, and canceled task propagate according to the async limitation above.

No emulator is needed; capture `BatchWriteItemRequest`.

### `WriteTxAsync`
1. `puts`, `updates`, `deletes`, and `conditionChecks` each independently branch null/non-null.
2. Non-null empty enumerables add nothing; all-null/all-empty inputs still submit an empty request.
3. Mixed inputs are grouped puts, updates, deletes, condition checks; order within groups is preserved.
4. Deferred-enumeration failures occur synchronously during `AddRange` and enter the catch.
5. Client success, synchronous throw, faulted task, and canceled task propagate.
6. List overload forwards the supplied list as `TransactItems`, including empty/null-at-runtime behavior, with the same failure distinction.

Assert identity/order in the captured request. Emulator validation of an empty transaction is optional, not needed for construction coverage.

### `ConvertUpdate` and conditional update
1. One/multiple fields produce `SET field = :field` entries in dictionary order; no extra expression removes the final comma.
2. Null/empty/whitespace extra expression takes the no-extra branch; nonblank text retains the comma separator and appends text.
3. Extra values independently branch null, empty, or populated.
4. Condition values independently branch null, empty, or populated.
5. Duplicate keys across generated placeholders, extra values, or condition values throw `ArgumentException` from `Dictionary.Add`.
6. Empty fields plus no extra previously returned invalid expression `"SE"`; enforce the non-null, non-empty field invariant with explicit argument exceptions.
7. `UpsertEntryAsync` branches on blank/nonblank condition expression and merges response attributes by replacing existing keys or adding new keys.
8. Conversion, conditional, and AWS failures propagate. Existing emulator test `DynamoDBDataManager_UpsertItemAsync` already verifies real ETag conditional success/failure.

## Existing Tests & Coverage Classification
- Required Roslyn static pairing maps `src/AWS/Shared/Storage/DynamoDBStorage.cs` to:
  - `test/Extensions/Orleans.AWS.Tests/StorageTests/AWSTestConstants.cs`
  - `test/Extensions/Orleans.AWS.Tests/StorageTests/DynamoDBStorageCancellationTests.cs`
  - `test/Extensions/Orleans.AWS.Tests/StorageTests/UnitTestDynamoDBStorage.cs`
  - `test/Transactions/Orleans.Transactions.DynamoDB.Test/DynamoDBTransactionalStateStorageTests.cs`
- This is a parse-only pairing heuristic, not coverage. It misses indirect calls and the functional `DynamoDBStorageTests.cs` namespace relationship.
- **Overall**: partial. Five emulator CRUD tests cover basic put/upsert/delete/read/query and real conditional update. Transactional-state tests exercise transaction writes indirectly. Only pre-work `InitializeTable` cancellation has a credential-free direct test.
- **Named methods**: partial/untested exactly as the baseline table shows. No direct test was found for `TableIndexWaitOnStatusAsync` or `PutEntriesAsync`.

## Existing Test Projects

### AWS shared/provider tests
- **Project file**: `test/Extensions/Orleans.AWS.Tests/Orleans.AWS.Tests.csproj`
- **Target projects**: clustering, persistence, reminders, and SQS; it also links the physical test copy.
- **Relevant files**: `AWSTestConstants.cs`, `UnitTestDynamoDBStorage.cs`, `DynamoDBStorageTestFixture.cs`, `DynamoDBStorageTests.cs`, `DynamoDBStorageCancellationTests.cs`, `DynamoDBStorageProviderTests.cs`, `DynamoDBStorageStressTests.cs`, `PersistenceGrainTests_AWSDynamoDBStore.cs`, and `DynamoDBGrainStorageProviderBuilderTests.cs`.

### Production-copy transactional tests
- **Project file**: `test/Transactions/Orleans.Transactions.DynamoDB.Test/Orleans.Transactions.DynamoDB.Test.csproj`
- **Target project**: `src/AWS/Orleans.Transactions.DynamoDB/Orleans.Transactions.DynamoDB.csproj`.
- **Relevant files**: `DynamoDBTransactionalStateStorageTests.cs` and `TestFixture.cs`; other transaction scenarios are outside this method scope.
- **Recommended location**: a new credential-free test file here, using public `Orleans.Transactions.DynamoDB.DynamoDBStorage`.

## Testing and Categorization Conventions
- xUnit v3 `[Fact]`/`[Theory]`, async `Task`, `Assert.ThrowsAsync`, and exact request/state assertions.
- Follow merged PR #10952: `[TestSuite("BVT")]`, `[TestProvider("DynamoDB")]`, `[TestArea("Transactions")]`, `[TestCategory("BVT")]`, `[TestCategory("AWS")]`, `[TestCategory("DynamoDB")]`, and `[TestCategory("Transactions")]`.
- Add `[TestCategory("DynamoDBStorage")]` for focused MTP selection. Use `DynamoDB` casing; older files inconsistently use `DynamoDb`.
- Name tests `Method_Scenario_ExpectedResult`. Create storage/client per test.
- Do not add NSubstitute; a recording subclass matches the concrete field and avoids a dependency change.

## Build & Test Commands
- **Full workspace build**: `dotnet build Orleans.slnx --no-incremental -bl`
- **Verified narrow AWS BVT discovery, net10.0** — exactly 21 current cases:
  - `dotnet test --project test/Extensions/Orleans.AWS.Tests/Orleans.AWS.Tests.csproj --framework net10.0 --filter-query "/[(Provider=DynamoDB)&(Suite=BVT)&(Area=Persistence)&(Category!=Performance)&(Category!=Stress)]" --list-tests --minimum-expected-tests 21 --max-parallel-test-modules 1`
- **Verified narrow AWS BVT discovery, net8.0** — exactly 21 current cases:
  - `dotnet test --project test/Extensions/Orleans.AWS.Tests/Orleans.AWS.Tests.csproj --framework net8.0 --filter-query "/[(Provider=DynamoDB)&(Suite=BVT)&(Area=Persistence)&(Category!=Performance)&(Category!=Stress)]" --list-tests --minimum-expected-tests 21 --max-parallel-test-modules 1`
- **Scoped fix cycle for recommended production-copy tests**:
  - `dotnet test --project test/Transactions/Orleans.Transactions.DynamoDB.Test/Orleans.Transactions.DynamoDB.Test.csproj --framework net10.0 --filter-query "/[(Provider=DynamoDB)&(Suite=BVT)&(Area=Transactions)&(Category=DynamoDBStorage)]" --minimum-expected-tests 1 --max-parallel-test-modules 1`
  - Repeat with `--framework net8.0`. After generation, replace `1` with the exact discovered new case count on both TFMs.
- **Harness-equivalent discovery**:
  - `dotnet test --solution Orleans.slnx --framework net10.0 --filter-query "/[(Provider=DynamoDB)&((Suite=BVT)|(Suite=SlowBVT)|(Suite=Functional))&(Category!=Performance)&(Category!=Stress)]" --list-tests --minimum-expected-tests 1 --max-parallel-test-modules 1`
  - Repeat with `--framework net8.0`; discovery delta must equal the new xUnit case count.
- **Canonical provider reference**: the AWS project alone currently discovers 145 canonical DynamoDB cases on each TFM.
- **Lint**: no separate command found; build enforces code style, analyzers, nullable checks, and warnings-as-errors.

## Focused Coverage Tooling
- CI pins `dotnet-coverage` `18.5.2`; this machine has global `18.10.0`. Use the CI pin for comparable evidence.
- Dynamic coverage uses `.github/coverage.config.xml` and `.github/scripts/invoke-coverage.ps1`, emits Cobertura, includes `Orleans.*.dll` and source paths under `src`, and excludes test assemblies.
- Exact focused command after adding categorized tests:

```powershell
$env:DOTNET_COVERAGE_VERSION = '18.5.2'
.\.github\scripts\setup-coverage.ps1 -InstallPath .\.tools
$command = @(
  'dotnet', 'test',
  '--project', 'test/Transactions/Orleans.Transactions.DynamoDB.Test/Orleans.Transactions.DynamoDB.Test.csproj',
  '--framework', 'net10.0',
  '--filter-query', '/[(Provider=DynamoDB)&(Suite=BVT)&(Area=Transactions)&(Category=DynamoDBStorage)]',
  '--minimum-expected-tests', '1',
  '--max-parallel-test-modules', '1'
)
.\.github\scripts\invoke-coverage.ps1 `
  -Settings .\.github\coverage.config.xml `
  -Output TestResults\dynamodb-storage-unit-net10.cobertura.xml `
  -CoverageCommand .\.tools\dotnet-coverage.exe `
  -Command $command
```

Repeat for `net8.0`, substitute the exact generated case count for `1`, and aggregate by physical source path so linked copies count once. Static pairing does not establish percentages.

## Recommendations
1. Test the public transaction production copy with a recording client subclass and reflection only for `_ddbClient` and the two private methods.
2. Cover `UpdateTableAsync` and `TableIndexWaitOnStatusAsync` first because baseline CRAP is 287.52 and 210.
3. Cover request/error branches for `PutEntriesAsync` and `WriteTxAsync`, then `ConvertUpdate`/`UpsertEntryAsync`.
4. Retain the emulator conditional-update test; add emulator cases only for behavior the fake cannot prove.
5. Treat empty-field `ConvertUpdate` and empty transaction requests as characterization/defect decisions.

## Acceptance Checklist
- [ ] Research stays bounded to the requested source, two relevant test projects, seams, linked inclusion, manifests, MTP categorization/commands, and coverage tooling. **Evidence**: scope/inventory above.
- [ ] Workspace is followed as delivered after merged PR #10952. **Evidence**: merge commit `607fe8a582...`; no checkout/restore/reset/clean used.
- [ ] Unrelated changes are preserved; production/test code is untouched. **Evidence**: only this research artifact is updated.
- [ ] `test/AGENTS.md` and unit-test guidance are applied. **Evidence**: isolated fakes, zero-delay waits, exact assertions, both TFMs, discovery checks.
- [ ] All five baseline metrics are verbatim. **Evidence**: baseline table.
- [ ] Exact branches/seams cover all named methods. **Evidence**: branch inventory.
- [ ] Conditional update, empty/mixed mutations, cancellation, and AWS service failures are included. **Evidence**: method sections and async limitation.
- [ ] Unit tests are credential-free. **Anticipated evidence**: fake-client BVT tests pass with AWS variables unset.
- [ ] Emulator is used only where fake behavior is insufficient. **Anticipated evidence**: only server condition/transaction semantics use `AWSTestConstants`.
- [ ] SDK style and registration are determined. **Evidence**: SDK declarations, implicit test glob, explicit linked-source list.
- [ ] Exact net10.0/net8.0 MTP filters/minimums are recorded. **Evidence**: verified 21-case commands; generated tests replace minimum `1` with exact count.
- [ ] Full workspace build is identified. **Evidence**: `dotnet build Orleans.slnx --no-incremental -bl`.
- [ ] Focused coverage command/tooling are identified. **Evidence**: CI-pinned `dotnet-coverage 18.5.2` and command above.
- [ ] Existing source/test pairs and static caveat are recorded. **Evidence**: Existing Tests section.
- [ ] Harness discovery sees every new case on both TFMs. **Anticipated evidence**: solution-level discovery delta equals generated cases.
- [ ] Named methods reach 80% line and 70% branch; CRAP reaches below 30 where method complexity permits it. **Blocker/evidence**: focused Cobertura must confirm; `UpdateTableAsync` has a structural CRAP floor of 54.
- [ ] Retained AWS provider coverage reaches 90% line or has a tracked rationale/follow-up. **Blocker/evidence**: requires canonical aggregated provider report, not the focused run alone.
- [ ] Linked shared sources count once per physical file. **Evidence**: aggregate on `src/AWS/Shared/Storage/DynamoDBStorage.cs`; do not claim canonical gains from excluded `Orleans.AWS.Tests.dll`.
