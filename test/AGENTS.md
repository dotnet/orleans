# Flaky test reliability

- Isolate mutable fixture state between tests. Reset or recreate clocks, clusters, topology, storage, and background tasks; sequential execution does not prevent one test from contaminating the next.
- Use exactly one fake-time driver. Let concurrent workers request or await progress, and give a single orchestrator sole ownership of time advancement.
- Replace sleeps and polling with explicit phase barriers: wait for ownership and readiness, arm observers or synchronization primitives, perform one action or time advance, then await completion.
- Prefer exact assertions when time and synchronization are controlled. Do not use ranges or "at least" assertions to conceal uncontrolled advancement or nondeterminism.
- Instrument the system under test instead of changing production behavior solely for tests. Use diagnostic events, and when appropriate in in-process tests, access grain contexts or instances (for example, with `InProcessTestCluster.TryGetGrainContext`) to expose test-only synchronization primitives.
- Arm and materialize waits before triggering the event. Materialize lazy `IEnumerable<Task>` sequences so subscriptions exist before the action occurs.
- Stress the complete shared-fixture class or suite in its relevant test order, not only an isolated test. Test every supported target framework when runtime behavior can differ.
- Make timeout failures contextual. Include the phase, entity or grain ID, expected and actual state or count, ownership, and armed schedules.
- Treat a timeout as missing evidence, not a root cause. Identify the exact transition or event that did not occur.
- Verify the patch itself. Do not rely on unrelated CI runs from branches that do not contain the change.
