---
name: review-testing
description: Testing lens for Orleans — spec-derived oracles, Accordant model-based tests, CsCheck property-based tests, deterministic simulation with controlled time, fault injection, and Verify snapshots. Use during code review, and when planning or writing tests.
---

# Testing lens

Apply with root `AGENTS.md` and `test/AGENTS.md`. This lens examines evidence:
tests that check the implementation against its spec and invariants, including
under failure.

## Procedure

1. Derive the oracle from the spec and the component's invariants, independent of the current implementation.
2. Choose the technique per property using the checklist below.
3. Cover valid input, invalid input, and the transition between them; exercise every error path.
4. Keep each test deterministic and seeded so every failure reproduces.
5. Reuse existing test infrastructure, fixtures, and TestKits.

## Checklist

- **Model-based tests (Accordant)** check stateful components against a simple reference model across operation sequences, concurrency, and failures (see `test/Orleans.Clustering.TestKit.Tests/MembershipTableModelBasedTestRunnerTests.cs`).
- **Property-based tests (CsCheck)** cover invariants, round-trips, and algebraic laws with reproducible seeds and effective shrinking (see `test/Orleans.Core.Tests/Directory/RingRangeTests.cs`).
- **Deterministic simulation** drives concurrent and distributed code with `InProcessTestCluster`, `FakeTimeProvider`, and explicit phase barriers.
- **Fault injection** exercises every boundary: storage (`FaultInjectionStorageProvider`, `FaultyMemoryStorage`), transactions (`src/Orleans.Transactions.TestKit.Base/FaultInjection`), and silo stops and kills (`StopSiloAsync`, `KillSiloAsync`).
- **Snapshots (Verify)** pin wire formats and generated output; baseline changes are reviewed as semantic changes (see `test/Orleans.Serialization.UnitTests/GeneratedSerializerBitwiseCompatibilityTests.cs`).
- **Compatibility tests** cover serialization and persisted-data changes across versions.
- **Right level.** Unit tests cover pure logic; cluster tests cover runtime boundaries; provider TestKits cover provider contracts. Each test carries the appropriate `[TestCategory]`.
- **Behavioral assertions.** Tests assert observable outcomes and contracts with exact expectations.
- **Fuzzing finds bugs; it does not prove absence.** A clear mental model and assertions come first; simulation is the last line of defense.

## Findings

Report gaps ordered by risk, each with the untested property or invariant and the
technique that covers it.
