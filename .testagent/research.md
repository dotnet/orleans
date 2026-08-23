# Test Generation Research

## Project Overview
- **Path**: `C:\Users\rebond\.copilot\repos\copilot-worktrees\orleans\efcore-providers`
- **Language**: C# with .NET 8 and .NET 10 targets
- **Framework**: Orleans EF Core 8 providers for MySQL/MariaDB, PostgreSQL, and SQL Server
- **Test Framework**: xUnit v3 `3.2.2` on Microsoft Testing Platform v2 `2.3.3`
- **Project system**: SDK-style
- **Dependency format and versions**: Central `PackageReference`; EF Core/SQLite/SQL Server `8.0.29`, Pomelo MySQL `8.0.3`, Npgsql EF Core `8.0.11`, `xunit.v3.mtp-v2` `3.2.2`, MTP/MSBuild `2.3.3`
- **New-file registration**: Implicit SDK `Compile` glob; no project edit is needed for a new `*.cs` test file.
- **Runner configuration**: `global.json` selects SDK `10.0.400` with MTP; `test/Directory.Build.props` makes test projects MTP executables and supplies xUnit v3; `test/testconfig.json` disables collection parallelism and pre-enumerates theories.
- **Guidance read**: Root `AGENTS.md` and `test/AGENTS.md`. Tests must isolate mutable databases, use explicit barriers instead of sleeps/polling, make exact assertions, and validate every affected target framework.

## Dependency Graph
- **Leaf types** (no in-scope dependencies): EF record types and provider model mappings in the six MySQL/SQL Server `*DbContext` targets; GUID/rowversion ETag converters.
- **Mid-layer types**: `EFGrainStorage` (factory, ETag converter, serializer, activation service); `EFMembershipTable`, `EFCoreGrainDirectory`, and `EFReminderTable` (factory plus ETag converter).
- **Top-layer types**: Existing provider-specific test subclasses and hosting registrations. No higher production layer is required for these seven regressions.
- **Test strategy**: Exercise model/query behavior through lightweight SQLite contexts where provider semantics are irrelevant; exercise key length, collation, and provider exception behavior against the real provider migrations.

## Build & Test Commands
- **Build**: `dotnet build test/Extensions/Orleans.EntityFrameworkCore.Tests/Orleans.EntityFrameworkCore.Tests.csproj --framework net10.0`
- **Test (scoped — provider-free fix cycles)**: `dotnet test --project test/Extensions/Orleans.EntityFrameworkCore.Tests/Orleans.EntityFrameworkCore.Tests.csproj --framework net10.0 --filter-query "/[(Area=EFCore)&(Provider=None)&(Suite=BVT)]" --minimum-expected-tests 1 --max-parallel-test-modules 1`
- **Test (scoped — MySQL)**: `dotnet test --project test/Extensions/Orleans.EntityFrameworkCore.Tests/Orleans.EntityFrameworkCore.Tests.csproj --framework net10.0 --filter-query "/[(Provider=EFCore-MySql)&(Suite=Functional)&(Category!=Performance)&(Category!=Stress)]" --minimum-expected-tests 1 --max-parallel-test-modules 1`
- **Test (scoped — SQL Server)**: `dotnet test --project test/Extensions/Orleans.EntityFrameworkCore.Tests/Orleans.EntityFrameworkCore.Tests.csproj --framework net10.0 --filter-query "/[(Provider=EFCore-SqlServer)&(Suite=Functional)&(Category!=Performance)&(Category!=Stress)]" --minimum-expected-tests 1 --max-parallel-test-modules 1`
- **Test (harness-equivalent — discovery check)**: `dotnet test --solution Orleans.slnx --framework net10.0 --list-tests --filter-query "/[(Area=EFCore)]" --minimum-expected-tests 1 --max-parallel-test-modules 1`
- **Second-framework validation**: Repeat build, discovery, and applicable tests with `--framework net8.0`.
- **Lint**: No dedicated bounded lint command found; warnings and repository analyzers are enforced by the build.

Provider discovery does not create fixtures or require a database connection. A real provider run needs `ORLEANSMYSQLCONNECTIONSTRING`, `ORLEANSMSSQLCONNECTIONSTRING`, or `ORLEANSPOSTGRESCONNECTIONSTRING`. The existing fixture dynamically skips when one is absent; that is not acceptable evidence that a provider regression passed. Do not add skips or SQLite substitutes for provider-specific identity semantics: use provider-free tests for iteration/discovery, then obtain real MySQL and SQL Server results from the corresponding CI jobs.

## Scope
- **Boundary**: The seven PR #8654 EF Core regression areas only, within `src/EFCore` and `test/Extensions/Orleans.EntityFrameworkCore.Tests`. Migrations are exercised through `EFCoreDatabaseFixture.MigrateAsync` but generated migration/snapshot files are not direct test-generation targets.
- **Targets**:
  - `src/EFCore/Orleans.Persistence.EntityFrameworkCore/EFGrainStorage.cs`
  - `src/EFCore/Orleans.Clustering.EntityFrameworkCore/EFMembershipTable.cs`
  - `src/EFCore/Orleans.GrainDirectory.EntityFrameworkCore/EFCoreGrainDirectory.cs`
  - `src/EFCore/Orleans.Reminders.EntityFrameworkCore/EFReminderTable.cs`
  - `src/EFCore/Orleans.Persistence.EntityFrameworkCore.MySql/Data/MySqlGrainStateDbContext.cs`
  - `src/EFCore/Orleans.Persistence.EntityFrameworkCore.SqlServer/Data/SqlServerGrainStateDbContext.cs`
  - `src/EFCore/Orleans.GrainDirectory.EntityFrameworkCore.MySql/Data/MySqlGrainDirectoryDbContext.cs`
  - `src/EFCore/Orleans.GrainDirectory.EntityFrameworkCore.SqlServer/Data/SqlServerGrainDirectoryDbContext.cs`
  - `src/EFCore/Orleans.Reminders.EntityFrameworkCore.MySql/Data/MySqlReminderDbContext.cs`
  - `src/EFCore/Orleans.Reminders.EntityFrameworkCore.SqlServer/Data/SqlServerReminderDbContext.cs`
- **Representative existing tests**:
  - `test/Extensions/Orleans.EntityFrameworkCore.Tests/Persistence/EFCorePersistenceProviderTestsBase.cs`
  - `test/Extensions/Orleans.EntityFrameworkCore.Tests/Clustering/EFCoreMembershipTableTestsBase.cs`
- **Requested artifacts**: This research file first, followed by seven separately identifiable regression-test items matching the acceptance checklist. This research task must not modify production.
- **Validation requirement**: New tests must be discovered by the root MTP command, provider-free tests must pass locally on both TFMs, and provider-dependent tests must pass on both TFMs in the real MySQL/SQL Server (and PostgreSQL where applicable) jobs. A skipped provider test is not validation.

## Roslyn Static Pairing Heuristic

The shipped analyzer was invoked exactly once from its skill directory against the repository root, the narrowest common root containing both `src/EFCore` and its test project. It reported 2,934 source files, 1,029 test files, 1,300 statically paired files, and 1,634 unpaired files repo-wide.

Relevant analyzer output:
- Provider DbContexts pair with their provider-specific persistence, membership, grain-directory, reminder, hosting, and model tests.
- `GrainStateDbContext.cs` pairs with `EFCorePersistenceFixture.cs` and `EFCorePersistenceProviderTestsBase.cs`.
- `ClusterDbContext.cs` pairs with `EFCoreMembershipTableTestsBase.cs`.
- `GrainDirectoryDbContext.cs` pairs with `EFCoreGrainDirectoryTestsBase.cs`.
- `ReminderDbContext.cs` pairs with `EFCoreReminderServiceTestsBase.cs` and `EFCoreReminderTableTestsBase.cs`.
- The analyzer attributes `EFGrainStorage.cs`, `EFMembershipTable.cs`, `EFCoreGrainDirectory.cs`, and `EFReminderTable.cs` only to hosting tests. This is an expected static-heuristic limitation: generic inherited tests and internal/DI-created types are under-attributed.

This pairing is a parse-only source-to-test heuristic, not line or branch coverage.

## Files to Test

### High Priority
| File | Classes/Functions | Testability | Estimated Coverage | Notes |
|------|-------------------|-------------|-------------------|-------|
| `.../Persistence.EntityFrameworkCore/EFGrainStorage.cs` | `ClearStateAsync`, `WriteStateAsync`, missing-state activation | Medium | Partial | Existing tests miss race, required exception translation, and constructor-restricted activation. |
| `.../Clustering.EntityFrameworkCore/EFMembershipTable.cs` | `ReadRow`, `ReadAll` | High | Partial | Missing-row behavior is absent; `ReadAll` inherits caller split-query mode. |
| `.../GrainDirectory.EntityFrameworkCore/EFCoreGrainDirectory.cs` | register/lookup/unregister | Medium | Partial | No long/trailing-space identifier tests. |
| `.../Reminders.EntityFrameworkCore/EFReminderTable.cs` | upsert/read/remove | Medium | Partial | No long/trailing-space identifier tests. |
| Six MySQL/SQL Server provider DbContexts listed in Scope | key lengths and identifier collations | Medium | Partial | Model tests assert metadata, not real database identity behavior. |

### Medium Priority
| File | Classes/Functions | Testability | Estimated Coverage | Notes |
|------|-------------------|-------------|-------------------|-------|
| Common `*DbContext.cs` and record types for the four features | keys, relationships, rowversions | High | Substantial | Supporting fixtures only; do not duplicate existing model tests. |

### Low Priority / Skip
| File | Reason |
|------|--------|
| Provider migrations and model snapshots | Generated; cover indirectly by migrating an isolated database. |
| Hosting extensions and ETag converters | Outside the seven requested regressions and already have focused tests. |
| PostgreSQL-only model/hosting files | No PostgreSQL-specific trailing-space requirement; include PostgreSQL only in shared behavioral/long-ID matrices. |

## Seven-Item Acceptance Inventory

1. **ClearStateAsync missing/unversioned race**
   - Canonical test: `Persistence/EFCorePersistenceProviderTestsBase.cs`.
   - Prepare a caller state which observed “missing” (`RecordExists=false`) or is unversioned (`ETag=null`), insert a winner after that observation, then clear with the stale caller object. Assert the winner remains byte-for-byte/ETag-identical and the caller is reset. This deterministically models the race without timing.
   - Existing `ClearMissingState_IsIdempotentButClaimedExistingRowFailsConcurrency` covers only a stable missing row: **partial**.

2. **Duplicate initial write translation to `InconsistentStateException`**
   - Canonical test: replace/extend `DuplicateInsertWithoutETag_FailsAndPreservesOriginal` in the persistence base.
   - Assert exact exception type, stored/current ETag fields, unchanged losing caller flags, preserved winner payload/ETag, and one row.
   - Current code catches `DbUpdateConcurrencyException` only; provider duplicate-key failures are `DbUpdateException`, and the current test explicitly expects that raw type. A production translation seam is absent, so the new regression should fail until production distinguishes an existing-key conflict from unrelated database failures.

3. **Constructor-restricted serializable state activation via `IActivatorProvider`**
   - Canonical test: persistence base or a provider-free SQLite regression class.
   - Use a `[GenerateSerializer]` state with no accessible constructor and verify both missing read and post-clear reset obtain an instance from `IActivatorProvider.GetActivator<T>().Create()`. Prefer a recording activator provider and exact call count.
   - Current `DependencyConstructedState` only proves `ActivatorUtilities` constructor DI, while `PrivateSerializedState` has a public parameterless constructor: **partial**.
   - Blocker: `EFGrainStorage` stores `IServiceProvider` and calls `ActivatorUtilities.CreateInstance<T>`; it has no `IActivatorProvider` dependency.

4. **Absent membership `ReadRow` returns current version and no member**
   - Canonical test: `Clustering/EFCoreMembershipTableTestsBase.cs`, or provider-free SQLite if service independence is preferred.
   - Initialize the table, advance its version with a different member, read an absent address, and assert an empty member list plus the exact current version and ETag.
   - Existing inherited read-row test covers only an existing member. Current production throws and wraps `InvalidOperationException`: **untested regression/current blocker**.

5. **`ReadAll` atomicity with caller-enabled `SplitQuery`**
   - Canonical artifact: a new provider-free BVT test near `Clustering/`, using a SQLite relational context configured with `UseQuerySplittingBehavior(SplitQuery)`.
   - Install a `DbCommandInterceptor`/explicit barrier to mutate membership between potential parent/child statements; assert the returned table version and members represent one snapshot. No sleeps or probabilistic parallel loop.
   - Current `Include(...).AsNoTracking()` obeys the caller's split-query setting and has no transaction/`AsSingleQuery` override: **untested regression/current blocker**.

6. **Long Orleans grain identifiers across persistence, grain-directory, and reminder providers**
   - Canonical tests: add one exact round-trip test to each of the persistence, grain-directory, and reminder bases. Use a key long enough to cross current 191/255/280/299 limits (at least 300 characters), and assert exact ID plus payload/address/reminder identity on read and raw stored value.
   - Current model limits include MySQL persistence 191, SQL Server persistence 299, MySQL directory/reminder `varchar(255)`, and SQL Server directory/reminder 512. Existing model tests merely codify these limits: **untested behavior/current schema blocker**.
   - Run the shared tests on all three providers; do not treat model metadata or SQLite as proof of provider storage capacity.

7. **Exact identity for trailing-space identifiers across MySQL and SQL Server**
   - Canonical artifact: provider-feature matrix tests covering persistence, grain directory, and reminders for `EFCore-MySql` and `EFCore-SqlServer`.
   - Persist two otherwise identical IDs whose keys differ only by a terminal space, then independently read/update/remove both and assert two distinct rows and exact payload ownership. Assert the two `GrainId` inputs differ before database use.
   - `utf8mb4_bin` and `Latin1_General_100_BIN2` metadata tests prove binary collation selection but not trailing-space distinction; both string equality/key semantics can still pad/ignore trailing spaces. A real provider database is mandatory: **untested behavior/current schema/query blocker**.

## Existing Tests & Coverage Classification
- `EFGrainStorage.cs` ↔ `EFCorePersistenceProviderTestsBase.cs`: **partial**; broad CRUD/concurrency exists, but items 1–3 are missing or assert the wrong outcome.
- `EFMembershipTable.cs` ↔ `EFCoreMembershipTableTestsBase.cs`: **partial**; substantial conformance exists, but items 4–5 are absent.
- `EFCoreGrainDirectory.cs` ↔ `EFCoreGrainDirectoryTestsBase.cs`: **partial**; normal registration and concurrency are covered, long/trailing-space IDs are not.
- `EFReminderTable.cs` ↔ `EFCoreReminderTableTestsBase.cs`: **partial**; normal CRUD/ranges are covered, long/trailing-space IDs are not.
- Provider DbContexts ↔ `GuidDbContextModelTests.cs` / `SqlServerDbContextModelTests.cs`: **partial** for these regressions; metadata and generated DDL are asserted, but no real-provider long/trailing identity behavior is asserted.
- No numeric coverage percentage is claimed.

## Existing Test Projects
- **Project file**: `test/Extensions/Orleans.EntityFrameworkCore.Tests/Orleans.EntityFrameworkCore.Tests.csproj`
- **Target source projects**: all 16 common/provider-specific clustering, persistence, grain-directory, and reminder EF Core projects via `ProjectReference`.
- **Bounded target test files**:
  - `Persistence/EFCorePersistenceProviderTestsBase.cs`, `Persistence/EFCorePersistenceProviderTests.cs`
  - `Clustering/EFCoreMembershipTableTestsBase.cs` and three provider subclasses
  - `GrainDirectory/EFCoreGrainDirectoryTestsBase.cs` and three provider subclasses
  - `Reminders/EFCoreReminderTableTestsBase.cs` and three provider subclasses
  - `Models/GuidDbContextModelTests.cs`, `Models/SqlServerDbContextModelTests.cs`
  - `Infrastructure/EFCoreDatabaseFixture.cs`, `EFCoreProviderConfiguration.cs`, `EFCoreTestDatabase.cs`

## Testing Patterns
- Use `[Fact]`/`[Theory]` and async `Task`/`ValueTask`; lifecycle is `IAsyncLifetime`.
- New traits are `TestArea`, `TestSuite`, and `TestProvider`; do not add legacy `TestCategory` for suite/provider classification.
- Provider classes carry `Area=EFCore`, `Suite=Functional`, the exact provider trait, and a feature area. Provider-free relational/model tests use `Provider=None`, `Suite=BVT`, `Area=EFCore`.
- Databases are isolated by unique names and migrated through `EFCoreDatabaseFixture`; cleanup runs in async disposal.
- Assert exact state, ETag, row count, and raw persisted identifiers. Avoid sleeps, broad exception assertions, weakened ranges, and conditional early returns.

## Recommendations
1. Add provider-free deterministic tests for items 3–5 first; these give the fastest red/green loop without external services.
2. Add persistence regressions 1–2 using the existing three-provider matrix.
3. Add shared long-ID coverage for all three feature bases, then MySQL/SQL Server trailing-space matrices.
4. Treat failures in items 2–7 as evidence of the production blockers above; do not alter expectations, skip, or replace provider assertions with metadata-only tests.
5. Validate discovery first, then both TFMs. Final acceptance requires green real-provider runs, not merely successful discovery or dynamic skips.
