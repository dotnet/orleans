# Test Implementation Plan

## Overview

Issue #10861 is bounded to `src/AWS/Shared/Storage/DynamoDBStorage.cs` after merged PR #10952 (`607fe8a5828b4973453a17b7050c7fe9588956de`). Use the public `Orleans.Transactions.DynamoDB.DynamoDBStorage` production copy from the existing transaction test project. Add a credential-free recording `AmazonDynamoDBClient` subclass, replace only private `_ddbClient`, and invoke only private `UpdateTableAsync` and `TableIndexWaitOnStatusAsync` by reflection.

The implementation slice is deterministic and complete for request construction, branch flow, token propagation, and failure propagation. It uses zero-delay scripted responses and no emulator. Existing emulator tests remain the evidence for real server-side conditional-expression behavior. No production change, new test project, dependency, polling sleep, or `AWSTestConstants` use is planned.

The single production target is assigned only to Phase 1. Phases 0, 2, and 3 establish baselines or validate Phase 1; they do not add another source target.

## Scope and Files

- **Production target**: `src/AWS/Shared/Storage/DynamoDBStorage.cs`
- **Existing test project**: `test/Transactions/Orleans.Transactions.DynamoDB.Test/Orleans.Transactions.DynamoDB.Test.csproj`
- **New test file**: `test/Transactions/Orleans.Transactions.DynamoDB.Test/DynamoDBStorageUnitTests.cs`
- **Test class**: `DynamoDBStorageUnitTests`
- **Production/test project changes**: none other than the new test file during implementation
- **Registration**: implicit SDK compile glob; no project-file edit
- **Excluded**: provider siblings, production seams, NSubstitute, simulator-only tests, and changes to existing AWS tests

## Baseline Metrics (verbatim)

| Method | Baseline physical coverage | Complexity | CRAP |
|---|---:|---:|---:|
| `UpdateTableAsync` | 56.9% | 54 | 287.52 |
| `TableIndexWaitOnStatusAsync` | 0% | 14 | 210 |
| `PutEntriesAsync` | 0% | 6 | 42 |
| `WriteTxAsync` | 65.5% | 16 | 26.5 |
| `ConvertUpdate` | 69.7% | 16 | 23.12 |

Required result: each named hotspot reaches at least 80% line coverage and 70% branch coverage. CRAP below 30 is required when the method's cyclomatic complexity permits it; a method with complexity above 30 cannot reach CRAP below 30 through coverage alone and must record that structural blocker. Retained provider families must reach 90% line coverage or have a tracked rationale/follow-up.

## Commands

- **Build**: `dotnet build Orleans.slnx --no-incremental -bl`
- **Focused test, net10.0**:
  `dotnet test --project test/Transactions/Orleans.Transactions.DynamoDB.Test/Orleans.Transactions.DynamoDB.Test.csproj --framework net10.0 --filter-query "/[(Provider=DynamoDB)&(Suite=BVT)&(Area=Transactions)&(Category=DynamoDBStorage)]" --minimum-expected-tests <NEW_CASE_COUNT> --max-parallel-test-modules 1`
- **Focused test, net8.0**: same command with `--framework net8.0`
- **Lint**: no separate command; the full build enforces analyzers, nullable checks, code style, and warnings-as-errors
- **Coverage**: CI-pinned `dotnet-coverage` `18.5.2` using `.github/coverage.config.xml` and `.github/scripts/invoke-coverage.ps1`

## Phase Summary

| Phase | Focus | Source files assigned | Est. new cases |
|---|---|---:|---:|
| 0 | Preserve state and capture discovery baselines | 0 | 0 |
| 1 | Complete credential-free DynamoDBStorage unit slice | 1 | 68-72 |
| 2 | Both-TFM count, focused execution, and coverage | 0 | 0 |
| 3 | Full build, solution discovery, and quality review | 0 | 0 |

---

## Phase 0: Baselines and Guardrails

### Overview

Run before adding tests. Preserve all unrelated changes and establish discovery counts without using checkout, restore, reset, or clean.

### Validation

1. Record `git status --short` and `git diff --name-only` in the implementation report.
2. Verify merged PR ancestry:
   `git merge-base --is-ancestor 607fe8a5828b4973453a17b7050c7fe9588956de HEAD`
3. Confirm the existing AWS Persistence BVT baseline is exactly 21 on each TFM:
   - `dotnet test --project test/Extensions/Orleans.AWS.Tests/Orleans.AWS.Tests.csproj --framework net10.0 --filter-query "/[(Provider=DynamoDB)&(Suite=BVT)&(Area=Persistence)&(Category!=Performance)&(Category!=Stress)]" --list-tests --minimum-expected-tests 21 --max-parallel-test-modules 1`
   - Repeat with `--framework net8.0`.
4. Capture the solution-level canonical discovery baseline on each TFM:
   - `dotnet test --solution Orleans.slnx --framework net10.0 --filter-query "/[(Provider=DynamoDB)&((Suite=BVT)|(Suite=SlowBVT)|(Suite=Functional))&(Category!=Performance)&(Category!=Stress)]" --list-tests --minimum-expected-tests 1 --max-parallel-test-modules 1`
   - Repeat with `--framework net8.0`.
5. Record the AWS-project canonical reference of 145 DynamoDB cases per TFM; do not reinterpret it as production coverage.

### Artifacts

- Phase report containing initial status/diff, ancestry exit code, both 21-case listings, and both solution discovery totals.
- No `status.md`.

### Success Criteria

- [ ] Existing 21-case AWS BVT discovery passes on both TFMs.
- [ ] Solution baseline totals are recorded before test generation.
- [ ] No workspace file is changed in this phase.

---

## Phase 1: Complete Deterministic DynamoDBStorage Unit Slice

### Overview

Create one credential-free test file in the existing transaction test project. Implement in the order below, running the focused net10.0 test command with temporary minimum `1` after each subsection so every checkpoint compiles and passes. Finish by repeating on net8.0.

### Test Harness

#### File

- **Source**: `src/AWS/Shared/Storage/DynamoDBStorage.cs`
- **Test File**: `test/Transactions/Orleans.Transactions.DynamoDB.Test/DynamoDBStorageUnitTests.cs`
- **Test Class**: `DynamoDBStorageUnitTests`

#### Required attributes

Apply the canonical attributes to the test class so every case is discoverable:

- `[TestSuite("BVT")]`
- `[TestProvider("DynamoDB")]`
- `[TestArea("Transactions")]`
- `[TestCategory("BVT")]`
- `[TestCategory("AWS")]`
- `[TestCategory("DynamoDB")]`
- `[TestCategory("Transactions")]`
- `[TestCategory("DynamoDBStorage")]`

#### Required helpers

- A nested `RecordingAmazonDynamoDBClient : AmazonDynamoDBClient` with scripted response/task/exception queues and call records for `DescribeTableAsync`, `DescribeTimeToLiveAsync`, `UpdateTableAsync`, `UpdateTimeToLiveAsync`, `UpdateItemAsync`, `BatchWriteItemAsync`, and `TransactWriteItemsAsync`.
- Every call record captures the complete request object and exact `CancellationToken`.
- A storage factory uses an HTTP service URL and dummy credentials, creates fresh storage/client state per test, and injects the client into `_ddbClient` by reflection.
- Reflection helpers invoke only `UpdateTableAsync` and `TableIndexWaitOnStatusAsync`, unwrap `TargetInvocationException`, and return the underlying task.
- Request/response builders create explicit table status, throughput, TTL, and GSI states. No shared mutable fixtures.
- Private wait tests always pass `delay: 0`; cancellation-during-delay is forced with a pre-canceled token only after a successful scripted describe.

### Cases: `UpdateTableAsync` and `InitializeTable`

Implement these first because they address the highest CRAP hotspot.

1. `UpdateTableAsync_UpdateIfExistsFalse_ReturnsWithoutClientCall`
   - Assert no describe, update, TTL, or GSI client call.
2. `UpdateTableAsync_UnsupportedTableStatus_ThrowsBeforeClientCall`
   - Script the unsupported status input and assert the implementation's exact non-AWS exception and zero client calls.
   - **Blocker**: research does not state the concrete exception type/message; resolve it while compiling against the production API and record it in the test body rather than weakening the assertion.
3. `UpdateTableAsync_CreatingStatus_WaitsBeforeEvaluatingChanges`
4. `UpdateTableAsync_UpdatingStatus_WaitsBeforeEvaluatingChanges`
   - Queue transition to `ACTIVE`; assert describe order and supplied-token identity.
5. `UpdateTableAsync_ActiveStatus_SkipsInitialTableWait`
   - Assert no extra table-status describe.
6. `UpdateTableAsync_RequestedReadCapacityDiffers_SubmitsProvisionedRequestAndWaits`
   - Assert table name, `PROVISIONED`, requested read/write throughput, one update action per existing GSI in source order, then `UPDATING -> ACTIVE`.
7. `UpdateTableAsync_RequestedWriteCapacityDiffers_SubmitsProvisionedRequestAndWaits`
   - Keep read equal so the second capacity term is evaluated; assert one update and wait.
8. `UpdateTableAsync_SwitchingProvisionedTableToOnDemand_SubmitsPayPerRequestShape`
   - Keep requested capacities otherwise equal; assert `PAY_PER_REQUEST`, null table throughput, null GSI updates, and supplied token.
9. `UpdateTableAsync_UnchangedCapacity_SkipsCapacityUpdateAndWait`
   - Assert no `UpdateTableAsync` call and no capacity-status wait.
10. `UpdateTableAsync_NullExistingIndexesAndNullRequestedIndexes_PerformsNoIndexWork`
11. `UpdateTableAsync_ExistingIndexesInCreatingUpdatingAndActiveStates_WaitsOnlyForTransientIndexes`
   - Assert waits for `CREATING` and `UPDATING`, not `ACTIVE`, in enumeration order.
12. `UpdateTableAsync_MissingRequestedIndexes_CreatesOnlyMissingIndexesInOrder`
   - Include one existing and two missing names; assert two update requests in requested order, each create action has the expected index name/definition, and tokens are identical to the supplied token.
13. `UpdateTableAsync_NullOrEmptyRequestedIndexes_CreatesNoIndexes`
   - Use theory cases for null and empty.
14. `UpdateTableAsync_TtlNonDisabledOnWrongAttribute_DoesNotUpdateTtl`
15. `UpdateTableAsync_EmptyTtlDescription_DoesNotUpdateTtl`
16. `UpdateTableAsync_TtlAlreadyEnabledOnRequestedAttribute_DoesNotUpdateTtl`
17. `UpdateTableAsync_DisabledTtl_EnablesRequestedAttributeAndWaits`
   - Assert update request table name, attribute name, `Enabled == true`, subsequent TTL describe sequence, and token identity.
18. `UpdateTableAsync_TtlAmazonServiceFailure_IsSwallowed`
   - Throw a named `AmazonDynamoDBException` from TTL work; assert completion and that later scripted index work still occurs.
19. `UpdateTableAsync_CanceledClientTask_PropagatesCancellationAndToken`
   - Assert `OperationCanceledException`/`TaskCanceledException` according to the returned task and the exact supplied token on the failing call.
20. `UpdateTableAsync_AmazonServiceFailure_RethrowsSameException`
   - Assert `Assert.Same` on the scripted `AmazonDynamoDBException`, no later calls, and exact token propagation.
21. `InitializeTable_ExistingTable_RoutesToUpdateAndPropagatesToken`
   - Script describe of an existing table; assert the update path is used rather than create and all recorded calls receive the supplied token.

### Cases: `TableIndexWaitOnStatusAsync`

1. `TableIndexWaitOnStatusAsync_MissingIndexAndNullDesiredStatus_ReturnsWithoutDelay`
2. `TableIndexWaitOnStatusAsync_IndexOutsideWhileStatusAndNullDesiredStatus_ReturnsWithoutDelay`
3. `TableIndexWaitOnStatusAsync_IndexInitiallyInWhileStatus_DescribesUntilTransition`
   - Queue transient then desired state; assert exact describe count/order and no real sleep.
4. `TableIndexWaitOnStatusAsync_IndexAlreadyAtDesiredStatus_Returns`
5. `TableIndexWaitOnStatusAsync_MissingIndexWithDesiredStatus_Throws`
6. `TableIndexWaitOnStatusAsync_WrongFinalStatus_Throws`
   - For both validation failures, assert the implementation's exact exception type/message and final describe count.
   - **Blocker**: research does not identify the concrete validation exception; bind to the production type during implementation.
7. `TableIndexWaitOnStatusAsync_DescribeServiceFailure_RethrowsSameException`
8. `TableIndexWaitOnStatusAsync_DescribeCancellation_PropagatesAndPreservesToken`
9. `TableIndexWaitOnStatusAsync_CancellationDuringDelay_Propagates`
   - Return a matching transient index despite a pre-canceled token; assert the describe saw that token and the zero-delay call still cancels.

### Cases: `PutEntriesAsync`

1. `PutEntriesAsync_NullEntries_ThrowsArgumentNullException`
2. `PutEntriesAsync_EmptyEntries_CompletesWithoutClientCall`
3. `PutEntriesAsync_NonEmptyEntries_SubmitsOrderedPutRequestsForExactTable`
   - Assert exactly one dictionary key equal to the requested table, one `PutRequest` per item, original order and item-reference identity, and `CancellationToken.None`.
4. `PutEntriesAsync_SynchronousClientFailure_ThrowsSameException`
5. `PutEntriesAsync_FaultedClientTask_PropagatesSameException`
6. `PutEntriesAsync_CanceledClientTask_PropagatesCancellation`
   - Distinguish synchronous throw from returned faulted/canceled task; do not claim the method's catch sees asynchronous completion.

### Cases: both `WriteTxAsync` overloads

#### Component-enumerable overload

1. `WriteTxAsync_AllComponentInputsNull_SubmitsEmptyTransaction`
   - Assert one client call with a non-null, empty `TransactItems` list and `CancellationToken.None`.
2. `WriteTxAsync_AllComponentInputsEmpty_SubmitsEmptyTransaction`
   - Pass four distinct empty enumerables to traverse every non-null branch; assert empty request.
3. `WriteTxAsync_MixedComponents_GroupsItemsAndPreservesGroupOrder`
   - Supply puts, updates, deletes, and condition checks. Assert exact order: all puts, then updates, deletes, condition checks; assert identity within every group.
4. `WriteTxAsync_DeferredEnumerationFailure_ThrowsSynchronouslyWithoutClientCall`
   - Throw during `AddRange`; assert the same exception and zero transaction calls.
5. `WriteTxAsync_ComponentOverloadSynchronousClientFailure_ThrowsSameException`
6. `WriteTxAsync_ComponentOverloadFaultedClientTask_PropagatesSameException`
7. `WriteTxAsync_ComponentOverloadCanceledClientTask_PropagatesCancellation`

#### List overload

8. `WriteTxAsync_ListOverload_ForwardsSuppliedListByIdentity`
9. `WriteTxAsync_ListOverload_EmptyList_SubmitsEmptyListByIdentity`
10. `WriteTxAsync_ListOverload_NullListAtRuntime_SubmitsNullTransactItems`
11. `WriteTxAsync_ListOverloadSynchronousClientFailure_ThrowsSameException`
12. `WriteTxAsync_ListOverloadFaultedClientTask_PropagatesSameException`
13. `WriteTxAsync_ListOverloadCanceledClientTask_PropagatesCancellation`

For every submitted request, assert the complete `TransactItems` shape and `CancellationToken.None`. Empty and null transactions are construction characterizations only; do not add an emulator merely to have DynamoDB reject them.

### Cases: `ConvertUpdate` and adjacent `UpsertEntryAsync`

1. `ConvertUpdate_OneField_ProducesSetExpressionWithoutTrailingComma`
2. `ConvertUpdate_MultipleFields_PreservesDictionaryOrder`
   - Assert exact text `SET field = :field` entries and exact generated placeholder/value mappings.
3. `ConvertUpdate_NullEmptyOrWhitespaceExtraExpression_UsesNoExtraBranch`
   - Three theory cases; assert no separator or extra text.
4. `ConvertUpdate_NonblankExtraExpression_AppendsSetAssignment`
5. `ConvertUpdate_NullOrEmptyExtraValues_AddsNoExtraValues`
6. `ConvertUpdate_PopulatedExtraValues_AddsEveryEntry`
7. `ConvertUpdate_NullOrEmptyConditionValues_AddsNoConditionValues`
8. `ConvertUpdate_PopulatedConditionValues_AddsEveryEntry`
9. `ConvertUpdate_DuplicateGeneratedAndExtraValueKey_ThrowsArgumentException`
10. `ConvertUpdate_DuplicateGeneratedAndConditionValueKey_ThrowsArgumentException`
11. `ConvertUpdate_DuplicateExtraAndConditionValueKey_ThrowsArgumentException`
12. `ConvertUpdate_EmptyFieldsWithoutExtraExpression_ReturnsCurrentSECharacterization`
   - Assert exactly `"SE"` and identify this as characterization, not a valid DynamoDB expression.
13. `UpsertEntryAsync_BlankCondition_SubmitsUnconditionalUpdateShape`
   - Assert exact table/key/update expression, absent condition expression, merged expression values, and exact token.
14. `UpsertEntryAsync_NonblankCondition_SubmitsConditionalUpdateShape`
   - Assert exact condition text and condition values in `UpdateItemRequest`.
15. `UpsertEntryAsync_ResponseAttributes_ReplacesExistingKeysAndAddsNewKeys`
   - Return one replacement and one new key; assert both values in the caller-visible dictionary.
16. `UpsertEntryAsync_ConversionFailure_ThrowsBeforeClientCall`
17. `UpsertEntryAsync_AmazonServiceFailure_RethrowsSameException`
18. `UpsertEntryAsync_CanceledClientTask_PropagatesCancellationAndToken`

The existing emulator-backed `DynamoDBDataManager_UpsertItemAsync` remains the server-semantics evidence for ETag conditional success/failure; do not duplicate it here.

### Phase 1 Success Criteria

- [ ] The new file is implicitly compiled by the existing project.
- [ ] Every listed test has exact state/request, call-count, order/identity, token, or exception assertions.
- [ ] All scripted client queues are exhausted or explicitly asserted.
- [ ] No test uses credentials, shared state, sleep, polling, emulator, or NSubstitute.
- [ ] Focused tests compile and pass on net10.0 and net8.0 with temporary minimum `1`.

---

## Phase 2: Exact Counts, Both-TFM Execution, and Focused Coverage

### Exact Test-Count Update Procedure

1. Run the focused command on net10.0 with `--list-tests --minimum-expected-tests 1`.
2. Count only listed xUnit cases whose fully qualified name contains `DynamoDBStorageUnitTests.`; count every theory data row separately. Record this as `N10`.
3. Repeat on net8.0 and record `N8`.
4. Assert the listed names are identical and `N10 == N8`; otherwise stop and fix discovery. Set `<NEW_CASE_COUNT>` to that common exact integer.
5. Replace temporary minimum `1` with that integer in both focused execution commands and in both coverage command arrays.
6. Run both focused commands without `--list-tests`; the discovered and passed totals must both equal `<NEW_CASE_COUNT>`, with zero skipped/failed tests.

### Credential-Free Validation

Run both focused commands in a process where AWS credential/profile variables are unset. The storage factory must still pass dummy credentials directly and every operation must terminate in the recording client. Evidence is both passing test summaries plus zero emulator/network setup.

### Focused Coverage: net10.0

```powershell
$env:DOTNET_COVERAGE_VERSION = '18.5.2'
.\.github\scripts\setup-coverage.ps1 -InstallPath .\.tools
$command = @(
  'dotnet', 'test',
  '--project', 'test/Transactions/Orleans.Transactions.DynamoDB.Test/Orleans.Transactions.DynamoDB.Test.csproj',
  '--framework', 'net10.0',
  '--filter-query', '/[(Provider=DynamoDB)&(Suite=BVT)&(Area=Transactions)&(Category=DynamoDBStorage)]',
  '--minimum-expected-tests', '<NEW_CASE_COUNT>',
  '--max-parallel-test-modules', '1'
)
.\.github\scripts\invoke-coverage.ps1 `
  -Settings .\.github\coverage.config.xml `
  -Output TestResults\dynamodb-storage-unit-net10.cobertura.xml `
  -CoverageCommand .\.tools\dotnet-coverage.exe `
  -Command $command
```

Repeat exactly with `net8.0` and output `TestResults\dynamodb-storage-unit-net8.cobertura.xml`.

### Coverage Evaluation

- Aggregate both reports by the physical path `src/AWS/Shared/Storage/DynamoDBStorage.cs`; linked assembly copies must not be counted as separate source files.
- Confirm `UpdateTableAsync`, `TableIndexWaitOnStatusAsync`, `PutEntriesAsync`, both `WriteTxAsync` overloads, and `ConvertUpdate` each meet 80% line and 70% branch. Confirm CRAP below 30 where complexity permits it, and record the complexity floor otherwise.
- Do not claim hits from `Orleans.AWS.Tests.dll`; test assemblies are excluded by canonical coverage settings.
- If a named metric misses its threshold, use uncovered lines/branches to add only another deterministic case from the documented branch inventory, then repeat both TFM runs.

### Explicit Coverage Blockers

- Focused Cobertura is the only acceptable proof for named-method percentages; static source/test pairing is not proof.
- The retained AWS provider-family 90% requirement cannot be proven by this focused transaction-project run. It requires the canonical aggregated provider report. If below 90%, record the measured percentage, excluded/provider scope, rationale, owner, and follow-up issue rather than claiming success.

### Phase 2 Success Criteria

- [ ] Exact new case count is identical on both TFMs and replaces every temporary minimum.
- [ ] Every focused case passes with AWS credentials unset.
- [ ] Both exact Cobertura artifacts exist.
- [ ] Named thresholds are verified or a concrete uncovered branch/blocker is recorded.

---

## Phase 3: Workspace Validation, Discovery Delta, and Quality Review

### Validation Sequence

1. Run `dotnet build Orleans.slnx --no-incremental -bl`.
   - Artifact: `msbuild.binlog`; require zero warnings-as-errors/analyzer failures.
2. Re-run the Phase 0 AWS Persistence BVT commands on both TFMs with minimum 21.
   - Require the existing count to remain 21.
3. Run solution-level canonical discovery on net10.0:
   `dotnet test --solution Orleans.slnx --framework net10.0 --filter-query "/[(Provider=DynamoDB)&((Suite=BVT)|(Suite=SlowBVT)|(Suite=Functional))&(Category!=Performance)&(Category!=Stress)]" --list-tests --minimum-expected-tests 1 --max-parallel-test-modules 1`
4. Repeat on net8.0.
   - For each TFM, require final total minus its Phase 0 baseline to equal `<NEW_CASE_COUNT>`.
   - Require every new case to carry Provider `DynamoDB`, Suite `BVT`, Area `Transactions`, and Category `DynamoDBStorage`.
5. Run a final static test-gap/source-pair review bounded to `DynamoDBStorage.cs`.
   - Artifact: a review record showing the new test file as a source/test pair and reiterating that pairing is parse-only, not coverage.
6. Run an assertion-quality review over every method in `DynamoDBStorageUnitTests`.
   - Require no assertion-free, presence-only, self-referential, or exception-without-identity tests.
   - Require request tests to assert table key, operation kind, collection count/order/identity, relevant optional nulls, token, call count, and exception identity.
   - Artifact: per-test review table with no unresolved weak-assertion findings.
7. Record final `git status --short` and `git diff --name-only`.
   - Preserve unrelated entries exactly.
   - Production source and project files must be unchanged; only the planned new test file plus requested `.testagent` artifacts may be attributable to this work.

### Phase 3 Success Criteria

- [ ] Full workspace build passes.
- [ ] Existing AWS 21-case discovery is unchanged on both TFMs.
- [ ] Solution discovery delta exactly equals the new case count on both TFMs.
- [ ] Test-gap and assertion-quality reviews have no unaddressed deterministic gaps.
- [ ] No production file, existing test, project file, dependency manifest, or `status.md` is changed.

---

## Acceptance Checklist and Verification Map

The checklist text below is preserved exactly. Each nested mapping makes the item independently verifiable.

- [ ] Research stays bounded to the requested source, two relevant test projects, seams, linked inclusion, manifests, MTP categorization/commands, and coverage tooling. **Evidence**: scope/inventory above.
  - **Plan mapping**: Scope and Files; Phase 0 AWS-project checks; Phase 1 transaction production-copy tests; Phase 2 coverage. No provider sibling is assigned.
- [ ] Workspace is followed as delivered after merged PR #10952. **Evidence**: merge commit `607fe8a582...`; no checkout/restore/reset/clean used.
  - **Plan mapping**: Phase 0 ancestry command and initial status/diff; Phase 3 final status/diff. The prohibited commands are absent from every phase.
- [ ] Unrelated changes are preserved; production/test code is untouched. **Evidence**: only this research artifact is updated.
  - **Plan mapping/blocker**: for this plan-authoring task, verify only `.testagent/plan.md` was added and production/tests stayed untouched. The original phrase “only this research artifact is updated” becomes stale because the requested plan is a second artifact; treat that wording as an explicit documentary blocker, not permission to alter unrelated files. During later implementation, only the new planned test file may additionally change.
- [ ] `test/AGENTS.md` and unit-test guidance are applied. **Evidence**: isolated fakes, zero-delay waits, exact assertions, both TFMs, discovery checks.
  - **Plan mapping**: Phase 1 fresh client/storage per test, zero-delay reflection helper, complete request assertions; Phases 2-3 both-TFM execution and discovery.
- [ ] All five baseline metrics are verbatim. **Evidence**: baseline table.
  - **Plan mapping**: “Baseline Metrics (verbatim)” reproduces all five values and Phase 2 compares the new report.
- [ ] Exact branches/seams cover all named methods. **Evidence**: branch inventory.
  - **Plan mapping**: every named method has concrete Phase 1 test names; `_ddbClient` plus the two private reflection helpers are the only reflection seams.
- [ ] Conditional update, empty/mixed mutations, cancellation, and AWS service failures are included. **Evidence**: method sections and async limitation.
  - **Plan mapping**: `UpsertEntryAsync_NonblankCondition_SubmitsConditionalUpdateShape`; all empty/mixed `PutEntriesAsync`/`WriteTxAsync` cases; named cancellation and same-instance AWS failure tests; synchronous versus returned-task failures are separate.
- [ ] Unit tests are credential-free. **Anticipated evidence**: fake-client BVT tests pass with AWS variables unset.
  - **Plan mapping**: Phase 2 credential-free run on both TFMs; recording-client call records prove no network boundary is crossed.
- [ ] Emulator is used only where fake behavior is insufficient. **Anticipated evidence**: only server condition/transaction semantics use `AWSTestConstants`.
  - **Plan mapping**: the new file has no `AWSTestConstants` reference and uses no emulator; existing emulator conditional test is retained as server-semantics evidence.
- [ ] SDK style and registration are determined. **Evidence**: SDK declarations, implicit test glob, explicit linked-source list.
  - **Plan mapping**: Scope and Files records implicit registration and the existing transaction project; no `.csproj` edit is planned.
- [ ] Exact net10.0/net8.0 MTP filters/minimums are recorded. **Evidence**: verified 21-case commands; generated tests replace minimum `1` with exact count.
  - **Plan mapping**: Commands, Phase 0, and Phase 2 give both TFM commands and the six-step exact count replacement procedure.
- [ ] Full workspace build is identified. **Evidence**: `dotnet build Orleans.slnx --no-incremental -bl`.
  - **Plan mapping**: Phase 3 step 1; artifact `msbuild.binlog`.
- [ ] Focused coverage command/tooling are identified. **Evidence**: CI-pinned `dotnet-coverage 18.5.2` and command above.
  - **Plan mapping**: Phase 2 provides the exact pinned net10.0 command, net8.0 substitution, and exact Cobertura artifact names.
- [ ] Existing source/test pairs and static caveat are recorded. **Evidence**: Existing Tests section.
  - **Plan mapping**: Phase 3 test-gap review must pair the new test and explicitly state that static pairing does not establish coverage.
- [ ] Harness discovery sees every new case on both TFMs. **Anticipated evidence**: solution-level discovery delta equals generated cases.
  - **Plan mapping**: Phase 0 captures both baselines; Phase 3 requires each final delta to equal `<NEW_CASE_COUNT>`.
- [ ] Named methods reach 80% line and 70% branch; CRAP reaches below 30 where method complexity permits it. **Blocker/evidence**: focused Cobertura must confirm; `UpdateTableAsync` has a CRAP floor of 54 because its complexity is 54.
  - **Plan mapping**: Phase 1 uses the public transaction production copy and supported private seams; Phase 2 evaluates both Cobertura artifacts against each threshold and blocks unsupported claims.
- [ ] Retained AWS provider coverage reaches 90% line or has a tracked rationale/follow-up. **Blocker/evidence**: requires canonical aggregated provider report, not the focused run alone.
  - **Plan mapping**: Phase 2 explicitly blocks a focused-run claim and requires measured canonical evidence or a rationale with owner and follow-up issue.
- [ ] Linked shared sources count once per physical file. **Evidence**: aggregate on `src/AWS/Shared/Storage/DynamoDBStorage.cs`; do not claim canonical gains from excluded `Orleans.AWS.Tests.dll`.
  - **Plan mapping**: Phase 2 aggregates by that exact physical path and excludes test-assembly hits.
