# Orleans testing guidance

These rules apply recursively to tests under `test/`. The repository-level
implementation principles also apply.

## Test design

- Model-based tests compare implementation behavior with contracts across operation
  sequences, including relevant concurrency and failures.
- Property-based failures must be reproducible, with effective shrinking.
- Keep snapshots deterministic and focused; inspect baseline changes for intended
  semantics.

## Reliability

- Isolate mutable fixture state between tests, including clocks, clusters, topology,
  storage, and background tasks.
- Give one orchestrator sole ownership of fake-time advancement; workers request
  or await progress.
- Use `FakeTimeProvider` for grain-facing time, keyed `TimeProviderNames` for
  subsystem clocks, and `TimeProviderTestingExtensions.UseTimeProviderForBackgroundAreas`
  for unrelated real-time work (`src/Orleans.TestingHost/TimeProviderTestingExtensions.cs`).
  For timer-schedule synchronization, follow `TrackingFakeTimeProvider` in
  `test/Orleans.DefaultCluster.Tests/FakeTimeSilo.cs`.
- Replace sleeps and polling with explicit phase barriers: wait for ownership and readiness, arm observers or synchronization primitives, perform one action or time advance, then await completion.
- Use exact assertions with controlled time and synchronization; fix nondeterminism
  instead of widening expected ranges.
- Observe lifecycle transitions with `DiagnosticListener` and `DiagnosticEventCollector`
  (`src/Orleans.TestingHost/Diagnostics/DiagnosticEventCollector.cs`); see
  `test/Orleans.GrainDirectory.Tests/GrainDirectory/GrainDirectoryLeaseTests.cs`.
  For in-process synchronization, use `InProcessTestCluster.TryGetGrainContext`
  where appropriate; preserve production behavior.
- Arm and materialize waits before triggering the event. Materialize lazy `IEnumerable<Task>` sequences so subscriptions exist before the action occurs.
- Stress the complete shared-fixture class or suite in its relevant test order, not only an isolated test. Test every supported target framework when runtime behavior can differ.
- Diagnose timeouts by the missing transition or event. Include phase, entity ID,
  expected/actual state, ownership, and armed schedules.
- Verify the patch itself. Do not rely on unrelated CI runs from branches that do not contain the change.
