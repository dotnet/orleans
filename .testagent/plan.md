# Test Implementation Plan

## Overview

This plan adds the broad PR #8654 regression suite to the existing
`test/Extensions/Orleans.EntityFrameworkCore.Tests/Orleans.EntityFrameworkCore.Tests.csproj`.
It covers all seven acceptance items without changing production code, schemas, migrations, or
snapshots. The phases follow the dependency graph: persistence behavior and activation first,
membership query behavior second, then the grain-directory and reminder provider identity
contracts.

All new tests use xUnit v3/Microsoft Testing Platform v2 conventions from `test/AGENTS.md`.
Provider-free tests carry `Area=EFCore`, `Provider=None`, and `Suite=BVT`; real-provider tests
retain `Area=EFCore`, `Suite=Functional`, and the exact provider trait. Every new method is
prefixed `PR8654_` to support narrow MTP filtering. No test may sleep, poll, conditionally return,
weaken an exact assertion, or convert a missing provider into passing evidence.

## Commands

- **Build**: `dotnet build test/Extensions/Orleans.EntityFrameworkCore.Tests/Orleans.EntityFrameworkCore.Tests.csproj --framework net10.0`
- **Test (provider-free)**: `dotnet test --project test/Extensions/Orleans.EntityFrameworkCore.Tests/Orleans.EntityFrameworkCore.Tests.csproj --framework net10.0 --filter-query "/[(Area=EFCore)&(Provider=None)&(Suite=BVT)]" --minimum-expected-tests 1 --max-parallel-test-modules 1`
- **Test (MySQL)**: `dotnet test --project test/Extensions/Orleans.EntityFrameworkCore.Tests/Orleans.EntityFrameworkCore.Tests.csproj --framework net10.0 --filter-query "/[(Provider=EFCore-MySql)&(Suite=Functional)&(Category!=Performance)&(Category!=Stress)]" --minimum-expected-tests 1 --max-parallel-test-modules 1`
- **Test (SQL Server)**: `dotnet test --project test/Extensions/Orleans.EntityFrameworkCore.Tests/Orleans.EntityFrameworkCore.Tests.csproj --framework net10.0 --filter-query "/[(Provider=EFCore-SqlServer)&(Suite=Functional)&(Category!=Performance)&(Category!=Stress)]" --minimum-expected-tests 1 --max-parallel-test-modules 1`
- **Discovery**: `dotnet test --solution Orleans.slnx --framework net10.0 --list-tests --filter-query "/[(Area=EFCore)]" --minimum-expected-tests 1 --max-parallel-test-modules 1`
- **Second TFM**: Repeat each applicable command with `--framework net8.0`.
- **Lint**: No separate bounded command; repository analyzers and warnings run during build.

## Acceptance Checklist Mapping

| Item | Exact proposed tests | Test file(s) | Principal fixture/fake/barrier | Expected production seam or blocker |
|---|---|---|---|---|
| 1. Missing/unversioned clear race | `PR8654_Persistence_ClearStateAsync_MissingCallerLosesInsertRace_PreservesWinnerAndResetsCaller`; `PR8654_Persistence_ClearStateAsync_UnversionedCallerLosesInsertRace_PreservesWinnerAndResetsCaller` | `Persistence/EFCorePersistenceProviderTestsBase.cs` | Existing isolated provider fixture; two caller objects and a deterministic winner insert, with no timing | `ClearStateAsync` must not delete a row which appeared after a missing/unversioned observation; reset must use the configured activation path |
| 2. Duplicate initial write translation | `PR8654_Persistence_WriteStateAsync_DuplicateInitialWriteThrowsInconsistentStateAndPreservesWinner` | `Persistence/EFCorePersistenceProviderTestsBase.cs` | Existing provider fixture; winner and stale initial writers | Duplicate-key `DbUpdateException` needs narrowly scoped translation to `InconsistentStateException`; unrelated database errors must remain unaltered |
| 3. Constructor-restricted activation | `PR8654_Persistence_ReadStateAsync_MissingStateUsesOrleansActivator`; `PR8654_Persistence_ClearStateAsync_ResetUsesOrleansActivator` | `Persistence/EFGrainStorageActivationTests.cs` | SQLite fixture, constructor-restricted generated-serializer state, `RecordingActivatorProvider`, recording `IActivator<T>` | `EFGrainStorage` needs an `IActivatorProvider` seam instead of direct `ActivatorUtilities.CreateInstance<T>` |
| 4. Absent membership row | `PR8654_Membership_ReadRow_AbsentAddressReturnsCurrentVersionAndNoMember` | `Clustering/EFMembershipTableRegressionTests.cs` | Provider-free SQLite membership fixture and context factory | `ReadRow` must use no-row semantics rather than throwing/wrapping `InvalidOperationException`, while still loading the current table version |
| 5. Split-query atomicity | `PR8654_Membership_ReadAll_CallerSplitQueryReturnsOneAtomicSnapshot` | `Clustering/EFMembershipTableRegressionTests.cs` | SQLite WAL database, `SplitQueryBarrierInterceptor`, two contexts, `TaskCompletionSource` barriers | `ReadAll` must force one query or create an equivalent transaction/snapshot boundary instead of inheriting caller `SplitQuery` behavior |
| 6. Long identifiers | `PR8654_Persistence_LongGrainIdentifierRoundTripsPayloadAndRawKeyExactly`; `PR8654_GrainDirectory_LongGrainIdentifierRoundTripsAddressAndRawKeyExactly`; `PR8654_Reminder_LongGrainIdentifierRoundTripsReminderAndRawKeyExactly` | The three existing feature bases | Existing isolated/migrated real-provider fixtures and raw DbContext verification | Current 191/255/280/299/512 limits block the contract; schema/migration changes belong to the parent agent |
| 7. Trailing-space identity | `PR8654_Persistence_TrailingSpaceIdentifiersRemainDistinct`; `PR8654_GrainDirectory_TrailingSpaceIdentifiersRemainDistinct`; `PR8654_Reminder_TrailingSpaceIdentifiersRemainDistinct` in each MySQL and SQL Server matrix class | Three focused provider identity files | Real MySQL/SQL Server fixtures; two keys differing only by one terminal space | Binary collation metadata alone is insufficient; provider schema/query key semantics must preserve terminal spaces, with fixes owned by the parent agent |

## Phase Summary

| Phase | Focus | Owned source files | Est. test methods / executions |
|---|---|---:|---:|
| 1 | Persistence races, exception translation, activation, and identifier contracts | 3 | 7 methods / 16 provider-expanded executions |
| 2 | Membership missing-row and atomic snapshot behavior | 1 | 2 provider-free methods |
| 3 | Grain-directory long and trailing-space identity | 3 | 3 methods / 5 provider executions |
| 4 | Reminder long and trailing-space identity | 3 | 3 methods / 5 provider executions |

---

## Phase 1: Persistence Regressions and Activation

### Overview

Establish the deterministic persistence regressions first. The activation tests provide the
fastest provider-free red/green loop; the concurrency, exception, and identifier tests then run
through the existing shared provider matrix.

### Files to Test

#### 1. `EFGrainStorage.cs`
- **Source**: `src/EFCore/Orleans.Persistence.EntityFrameworkCore/EFGrainStorage.cs`
- **Existing Test File**: `test/Extensions/Orleans.EntityFrameworkCore.Tests/Persistence/EFCorePersistenceProviderTestsBase.cs`
- **New Test File**: `test/Extensions/Orleans.EntityFrameworkCore.Tests/Persistence/EFGrainStorageActivationTests.cs`
- **Test Classes**: existing `EFCorePersistenceProviderTestsBase` hierarchy; new `EFGrainStorageActivationTests`

**Methods to test**:

1. `ClearStateAsync`
   - `PR8654_Persistence_ClearStateAsync_MissingCallerLosesInsertRace_PreservesWinnerAndResetsCaller`
     - Create a stale caller with `RecordExists == false` and `ETag == null`.
     - Insert a winner for the same grain after the stale observation, then clear using the stale
       caller. This ordering is explicit; no parallel timing is needed.
     - Capture the winner's serialized payload bytes and ETag before clear.
     - Assert the stale caller is reset exactly (`RecordExists == false`, `ETag == null`, and a
       newly activated default state), while a fresh read and raw row both retain the exact winner
       payload and ETag and the table contains exactly one matching row.
   - `PR8654_Persistence_ClearStateAsync_UnversionedCallerLosesInsertRace_PreservesWinnerAndResetsCaller`
     - Repeat with the caller explicitly marked unversioned (`ETag == null`) rather than relying
       only on the stable-missing case.
     - Assert the same exact caller reset and byte-for-byte/ETag-identical winner preservation.
   - **Blocker**: a clear based only on key/missing state can remove the concurrent winner.
     Production must make the delete conditional on the observed version or treat a
     missing/unversioned clear as a reset-only operation.

2. `WriteStateAsync`
   - `PR8654_Persistence_WriteStateAsync_DuplicateInitialWriteThrowsInconsistentStateAndPreservesWinner`
     - Persist a winner, then issue an initial write for the same key from a caller with no ETag.
     - Use `Assert.ThrowsAsync<InconsistentStateException>` and `Assert.IsType` semantics so a raw
       `DbUpdateException` or subtype-only match cannot pass.
     - Assert the exception's stored/current ETag fields exactly match the winner and losing
       caller values.
     - Assert the losing caller's ETag, `RecordExists`, and state payload are unchanged.
     - Assert a fresh API read and raw query return exactly one row with the original winner
       payload and ETag.
   - **Blocker**: production currently catches only `DbUpdateConcurrencyException`. It needs a
     duplicate-existing-key translation seam which does not mask unrelated `DbUpdateException`s.

3. Missing read and post-clear state activation
   - `PR8654_Persistence_ReadStateAsync_MissingStateUsesOrleansActivator`
     - Use a `[GenerateSerializer]` state whose constructor is inaccessible to normal activation.
     - Register `RecordingActivatorProvider` and a recording `IActivator<T>` which creates a
       distinguishable default instance.
     - Assert the missing read succeeds, returns that exact activated instance, leaves
       `RecordExists == false` and `ETag == null`, and invokes `GetActivator<T>()` and `Create()`
       exactly once.
   - `PR8654_Persistence_ClearStateAsync_ResetUsesOrleansActivator`
     - Seed a row, clear it, and assert the row is absent, the caller contains the exact state
       produced by the recording activator, and both recording calls occur exactly once.
   - **Fixture/fakes**: unique SQLite database, ordinary context factory, a
     `ConstructorRestrictedSerializableState`, `RecordingActivatorProvider`, and
     `RecordingActivator<ConstructorRestrictedSerializableState>`. Keep counts per test.
   - **Blocker**: `EFGrainStorage` currently stores only `IServiceProvider` and calls
     `ActivatorUtilities`; the expected parent-owned seam is injected `IActivatorProvider`.

#### 2. `MySqlGrainStateDbContext.cs`
- **Source**: `src/EFCore/Orleans.Persistence.EntityFrameworkCore.MySql/Data/MySqlGrainStateDbContext.cs`
- **Existing Test File**: `test/Extensions/Orleans.EntityFrameworkCore.Tests/Persistence/EFCorePersistenceProviderTestsBase.cs`
- **New Matrix File**: `test/Extensions/Orleans.EntityFrameworkCore.Tests/Persistence/EFCorePersistenceIdentifierIdentityTests.cs`
- **Test Classes**: existing shared persistence base; new
  `MySqlPersistenceIdentifierIdentityTests`

#### 3. `SqlServerGrainStateDbContext.cs`
- **Source**: `src/EFCore/Orleans.Persistence.EntityFrameworkCore.SqlServer/Data/SqlServerGrainStateDbContext.cs`
- **Existing Test File**: `test/Extensions/Orleans.EntityFrameworkCore.Tests/Persistence/EFCorePersistenceProviderTestsBase.cs`
- **New Matrix File**: `test/Extensions/Orleans.EntityFrameworkCore.Tests/Persistence/EFCorePersistenceIdentifierIdentityTests.cs`
- **Test Classes**: existing shared persistence base; new
  `SqlServerPersistenceIdentifierIdentityTests`

**Identifier methods to test for both provider contexts**:

1. `PR8654_Persistence_LongGrainIdentifierRoundTripsPayloadAndRawKeyExactly`
   - Add to the shared persistence base so MySQL, SQL Server, and PostgreSQL execute it.
   - Use a deterministic 600-character ASCII identifier, exceeding every current listed bound.
   - Assert the API read returns the exact identifier-associated payload and ETag; query through
     the fixture DbContext and assert one row, exact untrimmed identifier, payload bytes, and ETag.
   - **Blocker**: current provider key lengths and migrations cannot satisfy this contract.

2. `PR8654_Persistence_TrailingSpaceIdentifiersRemainDistinct`
   - Implement once in each new MySQL/SQL Server matrix class, sharing only a helper which does
     not hide provider assertions.
   - Assert before database use that `GrainId("key") != GrainId("key ")`.
   - Write distinct payloads under both IDs; assert two raw rows and exact stored keys.
   - Independently read both, update both to different second payloads, clear the unspaced key,
     and assert the spaced key and its payload/ETag remain unchanged; finally clear it and assert
     zero rows.
   - **Blocker**: provider key comparison/padding can collapse the two IDs despite binary
     collation metadata.

### Narrow Validation

Provider-free activation loop, run once per TFM:

```powershell
dotnet test --project test/Extensions/Orleans.EntityFrameworkCore.Tests/Orleans.EntityFrameworkCore.Tests.csproj --framework net10.0 --filter-query "/*/*/*/PR8654_Persistence_*[(Area=EFCore)&(Provider=None)&(Suite=BVT)]" --minimum-expected-tests 2 --max-parallel-test-modules 1
dotnet test --project test/Extensions/Orleans.EntityFrameworkCore.Tests/Orleans.EntityFrameworkCore.Tests.csproj --framework net8.0 --filter-query "/*/*/*/PR8654_Persistence_*[(Area=EFCore)&(Provider=None)&(Suite=BVT)]" --minimum-expected-tests 2 --max-parallel-test-modules 1
```

Real-provider persistence loop (run in the corresponding provisioned environment, for both
TFMs; shown for `net10.0`):

```powershell
dotnet test --project test/Extensions/Orleans.EntityFrameworkCore.Tests/Orleans.EntityFrameworkCore.Tests.csproj --framework net10.0 --filter-query "/*/*/*/PR8654_Persistence_*[(Provider=EFCore-MySql)&(Suite=Functional)]" --minimum-expected-tests 1 --max-parallel-test-modules 1
dotnet test --project test/Extensions/Orleans.EntityFrameworkCore.Tests/Orleans.EntityFrameworkCore.Tests.csproj --framework net10.0 --filter-query "/*/*/*/PR8654_Persistence_*[(Provider=EFCore-SqlServer)&(Suite=Functional)]" --minimum-expected-tests 1 --max-parallel-test-modules 1
dotnet test --project test/Extensions/Orleans.EntityFrameworkCore.Tests/Orleans.EntityFrameworkCore.Tests.csproj --framework net10.0 --filter-query "/*/*/*/PR8654_Persistence_Long*[(Provider=EFCore-PostgreSQL)&(Suite=Functional)]" --minimum-expected-tests 1 --max-parallel-test-modules 1
```

### Success Criteria
- [ ] Existing persistence tests are extended rather than replaced.
- [ ] Provider-free activation tests pass on `net8.0` and `net10.0`.
- [ ] Before parent fixes, provider failures match only the documented blockers.
- [ ] After parent fixes, every real-provider execution passes without skips.

---

## Phase 2: Membership Missing-Row and Snapshot Semantics

### Overview

Add deterministic, provider-free relational tests for both membership regressions. This phase
uses SQLite because neither behavior depends on provider-specific identity semantics.

### Files to Test

#### 1. `EFMembershipTable.cs`
- **Source**: `src/EFCore/Orleans.Clustering.EntityFrameworkCore/EFMembershipTable.cs`
- **Test File**: `test/Extensions/Orleans.EntityFrameworkCore.Tests/Clustering/EFMembershipTableRegressionTests.cs`
- **Test Class**: `EFMembershipTableRegressionTests`

**Methods to test**:

1. `ReadRow`
   - `PR8654_Membership_ReadRow_AbsentAddressReturnsCurrentVersionAndNoMember`
   - Initialize the table, add/update a different member to advance the table version, and retain
     the exact resulting version number and ETag.
   - Read an address which was never inserted.
   - Assert the result is non-null, its member collection is exactly empty, and both version and
     ETag exactly equal the known current table version. Do not accept a default or merely
     non-zero version.
   - **Blocker**: current no-row lookup throws `InvalidOperationException`, which is wrapped
     instead of returning the current version with no member.

2. `ReadAll`
   - `PR8654_Membership_ReadAll_CallerSplitQueryReturnsOneAtomicSnapshot`
   - Configure the SQLite relational context with
     `UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery)` and WAL mode.
   - Seed snapshot A with known version/ETag and an exact member set.
   - `SplitQueryBarrierInterceptor.ReaderExecutedAsync` signals when the first membership reader
     is open, then awaits a writer-completed `TaskCompletionSource`.
   - A separate context waits for that signal, commits snapshot B (version and member mutation),
     signals completion, and exits. Use run-continuations-asynchronously barriers and cancellation
     timeouts only to fail a deadlock; never use delay/sleep/polling.
   - Assert `ReadAll` returns the exact snapshot-A version, ETag, and member set. Explicitly reject
     the mixed result of snapshot-A version plus snapshot-B members.
   - The interceptor must record command count/text so the test proves the barrier was reached in
     the vulnerable implementation; after a single-query fix, allow one command and still require
     snapshot-A data.
   - **Blocker**: `Include(...).AsNoTracking()` inherits caller split-query mode without a
     transaction. Expected parent fix is `AsSingleQuery` or an equivalent atomic snapshot seam.

**Fixture/fakes/barriers**:
- `SqliteMembershipRegressionFixture`: unique database path, schema setup, WAL mode, and cleanup.
- `TestClusterDbContextFactory`: creates independent reader/writer contexts with the same options.
- `SplitQueryBarrierInterceptor`: exact command observation plus two
  `TaskCompletionSource` barriers; no mutable static state.

### Narrow Validation

```powershell
dotnet test --project test/Extensions/Orleans.EntityFrameworkCore.Tests/Orleans.EntityFrameworkCore.Tests.csproj --framework net10.0 --filter-query "/*/*/*/PR8654_Membership_*[(Area=EFCore)&(Provider=None)&(Suite=BVT)]" --minimum-expected-tests 2 --max-parallel-test-modules 1
dotnet test --project test/Extensions/Orleans.EntityFrameworkCore.Tests/Orleans.EntityFrameworkCore.Tests.csproj --framework net8.0 --filter-query "/*/*/*/PR8654_Membership_*[(Area=EFCore)&(Provider=None)&(Suite=BVT)]" --minimum-expected-tests 2 --max-parallel-test-modules 1
```

### Success Criteria
- [ ] Both tests are deterministic and service-independent.
- [ ] Exact version, ETag, members, and command/barrier observations are asserted.
- [ ] Both TFMs pass after the parent-owned production fix.

---

## Phase 3: Grain-Directory Identifier Contracts

### Overview

Cover long IDs on all providers and trailing-space identity on real MySQL/SQL Server databases.
SQLite and model metadata are not acceptable substitutes for this phase.

### Files to Test

#### 1. `EFCoreGrainDirectory.cs`
- **Source**: `src/EFCore/Orleans.GrainDirectory.EntityFrameworkCore/EFCoreGrainDirectory.cs`
- **Existing Test File**: `test/Extensions/Orleans.EntityFrameworkCore.Tests/GrainDirectory/EFCoreGrainDirectoryTestsBase.cs`
- **Test Class**: existing `EFCoreGrainDirectoryTestsBase` hierarchy

#### 2. `MySqlGrainDirectoryDbContext.cs`
- **Source**: `src/EFCore/Orleans.GrainDirectory.EntityFrameworkCore.MySql/Data/MySqlGrainDirectoryDbContext.cs`
- **New Matrix File**: `test/Extensions/Orleans.EntityFrameworkCore.Tests/GrainDirectory/EFCoreGrainDirectoryIdentifierIdentityTests.cs`
- **Test Class**: `MySqlGrainDirectoryIdentifierIdentityTests`

#### 3. `SqlServerGrainDirectoryDbContext.cs`
- **Source**: `src/EFCore/Orleans.GrainDirectory.EntityFrameworkCore.SqlServer/Data/SqlServerGrainDirectoryDbContext.cs`
- **New Matrix File**: `test/Extensions/Orleans.EntityFrameworkCore.Tests/GrainDirectory/EFCoreGrainDirectoryIdentifierIdentityTests.cs`
- **Test Class**: `SqlServerGrainDirectoryIdentifierIdentityTests`

**Methods to test**:

1. `Register`, `Lookup`, and `Unregister` with a long ID
   - `PR8654_GrainDirectory_LongGrainIdentifierRoundTripsAddressAndRawKeyExactly`
   - Add to the shared base for MySQL, SQL Server, and PostgreSQL.
   - Register a deterministic 600-character ID and a fully known activation address.
   - Assert lookup returns exactly one matching address and all address fields match.
   - Raw-query exactly one row and assert the full untrimmed ID and address fields.
   - Unregister and assert both API lookup and raw row count are empty.
   - **Blocker**: provider key lengths/migrations impose shorter limits.

2. Exact trailing-space identity
   - `PR8654_GrainDirectory_TrailingSpaceIdentifiersRemainDistinct`
   - Execute separately in the two matrix classes with exact provider traits.
   - Assert the two input `GrainId` values differ before I/O, register different addresses under
     `"directory-key"` and `"directory-key "`, and assert two exact raw keys.
   - Independently look up and update each registration, then unregister one and prove the other
     retains exact ownership; remove the second and assert zero rows.
   - **Blocker**: padded provider equality can merge lookup/update/delete predicates.

### Narrow Validation

Run each command with `net10.0` and then `net8.0` in the named real-provider job:

```powershell
dotnet test --project test/Extensions/Orleans.EntityFrameworkCore.Tests/Orleans.EntityFrameworkCore.Tests.csproj --framework net10.0 --filter-query "/*/*/*/PR8654_GrainDirectory_*[(Provider=EFCore-MySql)&(Suite=Functional)]" --minimum-expected-tests 1 --max-parallel-test-modules 1
dotnet test --project test/Extensions/Orleans.EntityFrameworkCore.Tests/Orleans.EntityFrameworkCore.Tests.csproj --framework net10.0 --filter-query "/*/*/*/PR8654_GrainDirectory_*[(Provider=EFCore-SqlServer)&(Suite=Functional)]" --minimum-expected-tests 1 --max-parallel-test-modules 1
dotnet test --project test/Extensions/Orleans.EntityFrameworkCore.Tests/Orleans.EntityFrameworkCore.Tests.csproj --framework net10.0 --filter-query "/*/*/*/PR8654_GrainDirectory_Long*[(Provider=EFCore-PostgreSQL)&(Suite=Functional)]" --minimum-expected-tests 1 --max-parallel-test-modules 1
```

### Success Criteria
- [ ] Shared long-ID test executes on all three providers.
- [ ] Trailing-space test executes, without skips, on MySQL and SQL Server.
- [ ] API values and raw persisted keys/row counts are exact.

---

## Phase 4: Reminder Identifier Contracts

### Overview

Complete the cross-feature identity matrix with reminder upsert/read/remove behavior.

### Files to Test

#### 1. `EFReminderTable.cs`
- **Source**: `src/EFCore/Orleans.Reminders.EntityFrameworkCore/EFReminderTable.cs`
- **Existing Test File**: `test/Extensions/Orleans.EntityFrameworkCore.Tests/Reminders/EFCoreReminderTableTestsBase.cs`
- **Test Class**: existing `EFCoreReminderTableTestsBase` hierarchy

#### 2. `MySqlReminderDbContext.cs`
- **Source**: `src/EFCore/Orleans.Reminders.EntityFrameworkCore.MySql/Data/MySqlReminderDbContext.cs`
- **New Matrix File**: `test/Extensions/Orleans.EntityFrameworkCore.Tests/Reminders/EFCoreReminderIdentifierIdentityTests.cs`
- **Test Class**: `MySqlReminderIdentifierIdentityTests`

#### 3. `SqlServerReminderDbContext.cs`
- **Source**: `src/EFCore/Orleans.Reminders.EntityFrameworkCore.SqlServer/Data/SqlServerReminderDbContext.cs`
- **New Matrix File**: `test/Extensions/Orleans.EntityFrameworkCore.Tests/Reminders/EFCoreReminderIdentifierIdentityTests.cs`
- **Test Class**: `SqlServerReminderIdentifierIdentityTests`

**Methods to test**:

1. `UpsertRow`, `ReadRow`, and `RemoveRow` with a long ID
   - `PR8654_Reminder_LongGrainIdentifierRoundTripsReminderAndRawKeyExactly`
   - Add to the shared base for MySQL, SQL Server, and PostgreSQL.
   - Use a deterministic 600-character ID, exact reminder name, start time, and period.
   - Assert the returned ETag, exact read-back identity and schedule, one raw row with the complete
     untrimmed key, and successful removal using the returned ETag.
   - Assert the final API result is absent and raw row count is zero.
   - **Blocker**: existing provider key lengths/migrations are too short.

2. Exact trailing-space identity
   - `PR8654_Reminder_TrailingSpaceIdentifiersRemainDistinct`
   - Execute separately in the MySQL and SQL Server matrix classes.
   - Assert the two input IDs differ before I/O. Upsert reminders under IDs differing only by one
     terminal space, using different names/schedules so ownership is unambiguous.
   - Assert two exact raw keys, independently read both, update both with their own ETags, and
     verify exact new schedules and distinct ETags.
   - Remove one and prove the other is unchanged; remove the second and assert zero rows.
   - **Blocker**: padded comparison can collapse upsert/read/remove predicates even when model
     collation metadata is binary.

### Narrow Validation

Run each command with `net10.0` and then `net8.0` in the named real-provider job:

```powershell
dotnet test --project test/Extensions/Orleans.EntityFrameworkCore.Tests/Orleans.EntityFrameworkCore.Tests.csproj --framework net10.0 --filter-query "/*/*/*/PR8654_Reminder_*[(Provider=EFCore-MySql)&(Suite=Functional)]" --minimum-expected-tests 1 --max-parallel-test-modules 1
dotnet test --project test/Extensions/Orleans.EntityFrameworkCore.Tests/Orleans.EntityFrameworkCore.Tests.csproj --framework net10.0 --filter-query "/*/*/*/PR8654_Reminder_*[(Provider=EFCore-SqlServer)&(Suite=Functional)]" --minimum-expected-tests 1 --max-parallel-test-modules 1
dotnet test --project test/Extensions/Orleans.EntityFrameworkCore.Tests/Orleans.EntityFrameworkCore.Tests.csproj --framework net10.0 --filter-query "/*/*/*/PR8654_Reminder_Long*[(Provider=EFCore-PostgreSQL)&(Suite=Functional)]" --minimum-expected-tests 1 --max-parallel-test-modules 1
```

### Success Criteria
- [ ] Shared long-ID test executes on all three providers.
- [ ] Both real-provider trailing-space tests run without skips.
- [ ] ETags, reminder fields, raw keys, and row counts are asserted exactly.

---

## Final Validation and Two-TFM Expectations

Run the following from the repository root after all test and parent-owned production/schema
changes are present:

```powershell
dotnet build test/Extensions/Orleans.EntityFrameworkCore.Tests/Orleans.EntityFrameworkCore.Tests.csproj --framework net10.0
dotnet build test/Extensions/Orleans.EntityFrameworkCore.Tests/Orleans.EntityFrameworkCore.Tests.csproj --framework net8.0
dotnet test --solution Orleans.slnx --framework net10.0 --list-tests --filter-query "/[(Area=EFCore)]" --minimum-expected-tests 1 --max-parallel-test-modules 1
dotnet test --solution Orleans.slnx --framework net8.0 --list-tests --filter-query "/[(Area=EFCore)]" --minimum-expected-tests 1 --max-parallel-test-modules 1
dotnet test --project test/Extensions/Orleans.EntityFrameworkCore.Tests/Orleans.EntityFrameworkCore.Tests.csproj --framework net10.0 --filter-query "/*/*/*/PR8654_*[(Area=EFCore)&(Provider=None)&(Suite=BVT)]" --minimum-expected-tests 4 --max-parallel-test-modules 1
dotnet test --project test/Extensions/Orleans.EntityFrameworkCore.Tests/Orleans.EntityFrameworkCore.Tests.csproj --framework net8.0 --filter-query "/*/*/*/PR8654_*[(Area=EFCore)&(Provider=None)&(Suite=BVT)]" --minimum-expected-tests 4 --max-parallel-test-modules 1
```

Expected results:

- Both builds complete with zero errors; analyzer warnings are not suppressed to make the build
  pass.
- Both root discovery runs list every `PR8654_` method, including provider-specific tests, without
  constructing database fixtures or requiring connection strings.
- The `net8.0` and `net10.0` discovery inventories contain the same PR #8654 method/provider
  matrix after theory expansion.
- All four provider-free tests pass on both TFMs.
- The existing scoped MySQL and SQL Server functional commands pass on both TFMs in provisioned
  CI, and the shared long-ID tests also pass in PostgreSQL jobs.
- A dynamically skipped provider test, missing connection string, metadata-only assertion, or
  SQLite substitution for items 6–7 does not satisfy acceptance.
- Existing test files remain intact except for additive methods/helpers; no production, schema,
  migration, or snapshot file is changed by the test implementation agent.
