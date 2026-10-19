---
name: review-testing
description: Testing lens for Orleans — spec-derived oracles, example tests, Accordant model-based tests, CsCheck property-based tests, deterministic simulation with controlled time, fault injection, and Verify snapshots. Use during code review, and when planning or writing tests.
---

# Testing lens

Apply with root `AGENTS.md` and `test/AGENTS.md`. This lens examines evidence:
tests that check the implementation against its spec and invariants, including
under failure.

## Procedure

1. Derive the oracle from the spec and the component's invariants, independent of the current implementation.
2. Choose the simplest technique that establishes each property, using the checklist below.
3. Cover representative valid and invalid inputs, the boundary between them, and the changed error paths.
4. Make each failure reproducible: record seeds for randomized tests and control time, synchronization, and external state.
5. Reuse existing test infrastructure, fixtures, and TestKits.

## Checklist

- **Example tests** pin specific regressions and scenarios.
- **Model-based tests ([Accordant](https://www.nuget.org/packages/Microsoft.Accordant))** check stateful components that have a simpler reference model against it across operation sequences, including concurrency and failures where the contract covers them (see `test/Orleans.Clustering.TestKit.Tests/MembershipTableModelBasedTestRunnerTests.cs`).
- **Property-based tests ([CsCheck](https://github.com/AnthonyLloyd/CsCheck))** cover invariants, round-trips, and algebraic laws with reproducible seeds and effective shrinking (see `test/Orleans.Core.Tests/Directory/RingRangeTests.cs`).
- **Deterministic simulation** drives concurrent and distributed behavior with the controlled clocks and explicit phase barriers in `test/AGENTS.md`, using `InProcessTestCluster` when the property crosses runtime boundaries ([FoundationDB](https://apple.github.io/foundationdb/testing.html)).
- **Fault injection** exercises changed failure boundaries: storage (`FaultInjectionGrainStorage`, `AddFaultInjectionMemoryStorage`), transactions (`src/Orleans.Transactions.TestKit.Base/FaultInjection`), and silo stops and kills (`StopSiloAsync`, `KillSiloAsync`).
- **Safety and progress phases.** Failure tests check safety while faults are active, then restore the protocol's progress assumptions and verify completion under a stated bound ([Paxos Made Live](https://research.google/pubs/paxos-made-live-an-engineering-perspective-2006-invited-talk/)).
- **Snapshots ([Verify](https://github.com/VerifyTests/Verify))** pin wire formats and generated output; baseline changes are reviewed as semantic changes (see `test/Orleans.Serialization.UnitTests/GeneratedSerializerBitwiseCompatibilityTests.cs`).
- **Compatibility tests** cover changes to shipped serialization or persisted data in both directions the rolling-upgrade window requires.
- **Right level.** Unit tests cover pure logic, cluster tests cover runtime boundaries, and provider TestKits cover provider contracts; new tests follow the containing suite's `[TestCategory]` conventions.
- **Contract assertions.** Assertions check the observable contract exactly: precise where the contract is precise (values, ordering under controlled time), and silent on incidental details.
- **Reasoning first.** Randomized testing finds bugs but cannot prove their absence; a clear model of the invariants and assertions in code establish correctness, and tests confirm it.

## Findings

Report gaps ordered by risk, each with the untested property or invariant and the
technique that covers it.
