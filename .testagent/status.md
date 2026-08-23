# PR #8654 Regression Test Status

## Summary

- Scope: seven requested EF Core runtime and identifier contracts.
- Tests added: 17 methods, producing 29 focused executions per target framework.
- Production changes: runtime providers, shared identifier hashing, EF models, migrations, snapshots, SQL scripts, and operator documentation.
- Final state: focused provider-free and real-provider matrices pass on `net8.0` and `net10.0`.

## Requirement Evidence

| Requirement | Evidence |
|---|---|
| Clear after a missing or unversioned observation preserves a concurrent winner | `PR8654_Persistence_ClearStateAsync_MissingCallerLosesInsertRace_PreservesWinnerAndResetsCaller`; `PR8654_Persistence_ClearStateAsync_UnversionedCallerLosesInsertRace_PreservesWinnerAndResetsCaller` |
| Duplicate initial writes translate to `InconsistentStateException` | `PR8654_Persistence_WriteStateAsync_DuplicateInitialWriteThrowsInconsistentStateAndPreservesWinner`; `PR8654_Persistence_WriteStateAsync_NonDuplicateDbUpdateExceptionPropagatesUntranslated` |
| Constructor-restricted state uses `IActivatorProvider` | `PR8654_Persistence_ReadStateAsync_MissingStateUsesOrleansActivator`; `PR8654_Persistence_ClearStateAsync_ResetUsesOrleansActivator` |
| Absent membership rows return the current table version | `PR8654_Membership_ReadRow_AbsentAddressReturnsCurrentVersionAndNoMember` |
| Split-query configuration cannot split membership snapshots | `PR8654_Membership_ReadAll_CallerSplitQueryReturnsOneAtomicSnapshot` |
| Long identifiers round-trip across persistence, grain directory, and reminders | `PR8654_Persistence_LongGrainIdentifierRoundTripsPayloadAndRawKeyExactly`; `PR8654_GrainDirectory_LongGrainIdentifierRoundTripsAddressAndRawKeyExactly`; `PR8654_Reminder_LongGrainIdentifierRoundTripsReminderAndRawKeyExactly` |
| MySQL and SQL Server preserve trailing-space identity | Provider-specific `PR8654_*_TrailingSpaceIdentifiersRemainDistinct` tests for persistence, grain directory, and reminders |

## Validation

- `dotnet build Orleans.slnx -bl`: passed.
- Provider-free BVT: 124/124 passed on each target framework.
- Docker-backed focused matrix: 29/29 passed with 0 skips on each target framework across MariaDB, PostgreSQL, and SQL Server.
- All nine provider migration snapshots report no pending model changes.
- Documentation validation: `npm run validate` from `docs/site`.

## Production and Schema Outcome

1. Grain-state resets use `IActivatorProvider`.
2. Confirmed duplicate initial writes translate to `InconsistentStateException`; unrelated database failures retain their original exception.
3. Missing or unversioned clears reset the caller without deleting a concurrently inserted row.
4. Membership row reads return an empty result with the current version when the silo is absent, and all-member reads force one SQL query.
5. Persistence, grain-directory, and reminder schemas use fixed-width SHA-256 identity keys while retaining complete identifier values.
6. Runtime lookups verify full values ordinally after hash lookup, preserving long identifiers and trailing spaces and reporting hash collisions.

## Quality Review

- Every requested contract maps to a dedicated test with exact state, ETag, row-count, and identifier assertions.
- The non-duplicate database-failure guard prevents broad exception translation.
- The provider identity tests perform independent create, read, update, remove, and raw-row checks.
