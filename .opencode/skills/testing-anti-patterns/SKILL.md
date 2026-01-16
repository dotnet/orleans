---
name: testing-anti-patterns
description: Use when writing or changing tests, adding mocks, or tempted to add test-only methods to production code - prevents testing mock behavior, production pollution with test-only methods, mocking without understanding dependencies, sleep-style waiting, and non-deterministic tests
---

# Testing Anti-Patterns

## Overview

Tests must verify real behavior, not mock behavior. Mocks are a means to isolate, not the thing being tested.

**Core principle:** Test what the code does, not what the mocks do.

**Following strict TDD prevents these anti-patterns.**

## The Iron Laws

```
1. NEVER test mock behavior
2. NEVER add test-only methods to production classes
3. NEVER mock without understanding dependencies
```

## Anti-Pattern 1: Testing Mock Behavior

**The violation:**
```typescript
// ❌ BAD: Testing that the mock exists
test('renders sidebar', () => {
  render(<Page />);
  expect(screen.getByTestId('sidebar-mock')).toBeInTheDocument();
});
```

**Why this is wrong:**
- You're verifying the mock works, not that the component works
- Test passes when mock is present, fails when it's not
- Tells you nothing about real behavior

**your human partner's correction:** "Are we testing the behavior of a mock?"

**The fix:**
```typescript
// ✅ GOOD: Test real component or don't mock it
test('renders sidebar', () => {
  render(<Page />);  // Don't mock sidebar
  expect(screen.getByRole('navigation')).toBeInTheDocument();
});

// OR if sidebar must be mocked for isolation:
// Don't assert on the mock - test Page's behavior with sidebar present
```

### Gate Function

```
BEFORE asserting on any mock element:
  Ask: "Am I testing real component behavior or just mock existence?"

  IF testing mock existence:
    STOP - Delete the assertion or unmock the component

  Test real behavior instead
```

## Anti-Pattern 2: Test-Only Methods in Production

**The violation:**
```typescript
// ❌ BAD: destroy() only used in tests
class Session {
  async destroy() {  // Looks like production API!
    await this._workspaceManager?.destroyWorkspace(this.id);
    // ... cleanup
  }
}

// In tests
afterEach(() => session.destroy());
```

**Why this is wrong:**
- Production class polluted with test-only code
- Dangerous if accidentally called in production
- Violates YAGNI and separation of concerns
- Confuses object lifecycle with entity lifecycle

**The fix:**
```typescript
// ✅ GOOD: Test utilities handle test cleanup
// Session has no destroy() - it's stateless in production

// In test-utils/
export async function cleanupSession(session: Session) {
  const workspace = session.getWorkspaceInfo();
  if (workspace) {
    await workspaceManager.destroyWorkspace(workspace.id);
  }
}

// In tests
afterEach(() => cleanupSession(session));
```

### Gate Function

```
BEFORE adding any method to production class:
  Ask: "Is this only used by tests?"

  IF yes:
    STOP - Don't add it
    Put it in test utilities instead

  Ask: "Does this class own this resource's lifecycle?"

  IF no:
    STOP - Wrong class for this method
```

## Anti-Pattern 3: Mocking Without Understanding

**The violation:**
```typescript
// ❌ BAD: Mock breaks test logic
test('detects duplicate server', () => {
  // Mock prevents config write that test depends on!
  vi.mock('ToolCatalog', () => ({
    discoverAndCacheTools: vi.fn().mockResolvedValue(undefined)
  }));

  await addServer(config);
  await addServer(config);  // Should throw - but won't!
});
```

**Why this is wrong:**
- Mocked method had side effect test depended on (writing config)
- Over-mocking to "be safe" breaks actual behavior
- Test passes for wrong reason or fails mysteriously

**The fix:**
```typescript
// ✅ GOOD: Mock at correct level
test('detects duplicate server', () => {
  // Mock the slow part, preserve behavior test needs
  vi.mock('MCPServerManager'); // Just mock slow server startup

  await addServer(config);  // Config written
  await addServer(config);  // Duplicate detected ✓
});
```

### Gate Function

```
BEFORE mocking any method:
  STOP - Don't mock yet

  1. Ask: "What side effects does the real method have?"
  2. Ask: "Does this test depend on any of those side effects?"
  3. Ask: "Do I fully understand what this test needs?"

  IF depends on side effects:
    Mock at lower level (the actual slow/external operation)
    OR use test doubles that preserve necessary behavior
    NOT the high-level method the test depends on

  IF unsure what test depends on:
    Run test with real implementation FIRST
    Observe what actually needs to happen
    THEN add minimal mocking at the right level

  Red flags:
    - "I'll mock this to be safe"
    - "This might be slow, better mock it"
    - Mocking without understanding the dependency chain
```

## Anti-Pattern 4: Incomplete Mocks

**The violation:**
```typescript
// ❌ BAD: Partial mock - only fields you think you need
const mockResponse = {
  status: 'success',
  data: { userId: '123', name: 'Alice' }
  // Missing: metadata that downstream code uses
};

// Later: breaks when code accesses response.metadata.requestId
```

**Why this is wrong:**
- **Partial mocks hide structural assumptions** - You only mocked fields you know about
- **Downstream code may depend on fields you didn't include** - Silent failures
- **Tests pass but integration fails** - Mock incomplete, real API complete
- **False confidence** - Test proves nothing about real behavior

**The Iron Rule:** Mock the COMPLETE data structure as it exists in reality, not just fields your immediate test uses.

**The fix:**
```typescript
// ✅ GOOD: Mirror real API completeness
const mockResponse = {
  status: 'success',
  data: { userId: '123', name: 'Alice' },
  metadata: { requestId: 'req-789', timestamp: 1234567890 }
  // All fields real API returns
};
```

### Gate Function

```
BEFORE creating mock responses:
  Check: "What fields does the real API response contain?"

  Actions:
    1. Examine actual API response from docs/examples
    2. Include ALL fields system might consume downstream
    3. Verify mock matches real response schema completely

  Critical:
    If you're creating a mock, you must understand the ENTIRE structure
    Partial mocks fail silently when code depends on omitted fields

  If uncertain: Include all documented fields
```

## Anti-Pattern 5: Integration Tests as Afterthought

**The violation:**
```
✅ Implementation complete
❌ No tests written
"Ready for testing"
```

**Why this is wrong:**
- Testing is part of implementation, not optional follow-up
- TDD would have caught this
- Can't claim complete without tests

**The fix:**
```
TDD cycle:
1. Write failing test
2. Implement to pass
3. Refactor
4. THEN claim complete
```

## Anti-Pattern 6: Sleep-Style Waiting

**The violation:**
```csharp
// ❌ BAD: Fixed delay hoping state changes
[Fact]
public async Task GrainActivates()
{
    var grain = GrainFactory.GetGrain<IMyGrain>(Guid.NewGuid());
    await grain.StartProcessing();
    
    await Task.Delay(5000);  // Hope 5 seconds is enough!
    
    var result = await grain.GetResult();
    Assert.NotNull(result);
}
```

**Why this is wrong:**
- **Non-deterministic** - Works on fast machines, fails on slow CI runners
- **Wastes time** - If operation takes 100ms, you still wait 5 seconds
- **Fragile** - Any timing change breaks tests
- **Slow test suites** - Many delays compound into minutes of wasted time
- **False confidence** - Passes locally, fails in CI (or vice versa)

**The Iron Rule:** Tests must be as fast and deterministic as possible. Never use fixed delays.

**The fixes:**

```csharp
// ✅ GOOD: Event-based waiting (preferred)
[Fact]
public async Task GrainActivates()
{
    var completionSource = new TaskCompletionSource();
    var grain = GrainFactory.GetGrain<IMyGrain>(Guid.NewGuid());
    
    grain.OnProcessingComplete += () => completionSource.SetResult();
    await grain.StartProcessing();
    
    await completionSource.Task.WaitAsync(TimeSpan.FromSeconds(30));
    var result = await grain.GetResult();
    Assert.NotNull(result);
}

// ✅ GOOD: Polling loop with observable condition
[Fact]
public async Task GrainActivates()
{
    var grain = GrainFactory.GetGrain<IMyGrain>(Guid.NewGuid());
    await grain.StartProcessing();
    
    // Delay in loop with guard condition - this is acceptable
    for (int i = 0; i < 100; i++)
    {
        var status = await grain.GetStatus();
        if (status == ProcessingStatus.Complete)
            break;
        await Task.Delay(100);  // Short delay, but checking condition
    }
    
    var result = await grain.GetResult();
    Assert.NotNull(result);
}

// ✅ GOOD: Use test utilities for common patterns
[Fact]
public async Task GrainActivates()
{
    var grain = GrainFactory.GetGrain<IMyGrain>(Guid.NewGuid());
    await grain.StartProcessing();
    
    await TestingUtils.WaitUntilAsync(
        () => grain.GetStatus(),
        status => status == ProcessingStatus.Complete,
        timeout: TimeSpan.FromSeconds(30));
    
    var result = await grain.GetResult();
    Assert.NotNull(result);
}
```

### Gate Function

```
BEFORE adding Task.Delay/Thread.Sleep to a test:
  STOP - This is almost always wrong

  Ask: "What condition am I waiting for?"

  IF waiting for state change:
    Use event-based waiting (TaskCompletionSource, events, callbacks)
    OR use polling loop with observable guard condition

  IF no observable condition exists:
    STOP - Improve system testability first (see Anti-Pattern 7)
    Add events, callbacks, or observable state to enable proper testing

  Acceptable delay patterns:
    ✅ Delay inside loop with guard condition check
    ✅ Delay to simulate real-world timing (rare, document why)
    
  Unacceptable patterns:
    ❌ Fixed delay hoping operation completes
    ❌ Delay without any condition check
    ❌ "Just add more delay" to fix flaky test
```

## Anti-Pattern 7: Untestable Behavior

**The violation:**
```csharp
// ❌ BAD: No way to observe completion
public class BackgroundProcessor
{
    public void StartProcessing()  // Fire and forget!
    {
        Task.Run(async () => {
            await DoWork();
            // No signal that work completed
        });
    }
}

// Test has no choice but to use fixed delay
[Fact]
public async Task ProcessorCompletesWork()
{
    var processor = new BackgroundProcessor();
    processor.StartProcessing();
    await Task.Delay(5000);  // Forced into bad pattern!
    // Assert something...
}
```

**Why this is wrong:**
- System lacks observable state or completion signals
- Tests forced into non-deterministic patterns
- "Untestable" is a design smell, not a testing problem

**The Iron Rule:** If behavior cannot easily be tested, improve the testability of the system itself. Then rewrite the test using the new, more testable functionality.

**The fix:**

```csharp
// ✅ GOOD: Design for testability
public class BackgroundProcessor
{
    public event Action? OnProcessingComplete;
    public ProcessingStatus Status { get; private set; }
    
    public Task StartProcessingAsync()  // Return awaitable task
    {
        return Task.Run(async () => {
            Status = ProcessingStatus.Running;
            await DoWork();
            Status = ProcessingStatus.Complete;
            OnProcessingComplete?.Invoke();
        });
    }
}

// Now test is deterministic and fast
[Fact]
public async Task ProcessorCompletesWork()
{
    var processor = new BackgroundProcessor();
    var completed = new TaskCompletionSource();
    processor.OnProcessingComplete += () => completed.SetResult();
    
    _ = processor.StartProcessingAsync();
    await completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
    
    Assert.Equal(ProcessingStatus.Complete, processor.Status);
}
```

### Gate Function

```
BEFORE accepting "this can't be tested properly":
  STOP - This is a design problem, not a testing limitation

  Ask: "What would make this testable?"
    - Add completion events/callbacks
    - Expose observable state
    - Return Task/awaitable instead of fire-and-forget
    - Add dependency injection for time-dependent components

  Steps:
    1. Identify what condition the test needs to observe
    2. Add that observability to the production code (this IS production value)
    3. Rewrite test using the new testable interface

  Remember:
    - Testability improvements ARE production improvements
    - Observable state helps debugging and monitoring too
    - Fire-and-forget is often a code smell anyway
```

## Anti-Pattern 8: Non-Deterministic Tests

**The violation:**
```csharp
// ❌ BAD: Race conditions in test
[Fact]
public async Task ConcurrentGrainAccess()
{
    var grain = GrainFactory.GetGrain<ICounterGrain>(Guid.NewGuid());
    
    // Fire off concurrent calls
    var tasks = Enumerable.Range(0, 100)
        .Select(_ => grain.Increment());
    await Task.WhenAll(tasks);
    
    await Task.Delay(1000);  // Hope everything settled!
    
    var count = await grain.GetCount();
    Assert.Equal(100, count);  // Sometimes 99, sometimes 100...
}
```

**Why this is wrong:**
- Test outcome depends on timing, not correctness
- Flaky tests erode confidence in test suite
- Developers start ignoring test failures
- CI becomes unreliable

**The Iron Rule:** Tests must be deterministic. Same inputs → same outputs, every time.

**The fix:**
```csharp
// ✅ GOOD: Proper synchronization
[Fact]
public async Task ConcurrentGrainAccess()
{
    var grain = GrainFactory.GetGrain<ICounterGrain>(Guid.NewGuid());
    
    // Await all operations properly
    var tasks = Enumerable.Range(0, 100)
        .Select(_ => grain.Increment())
        .ToList();
    await Task.WhenAll(tasks);
    
    // All increments complete before check - no delay needed
    var count = await grain.GetCount();
    Assert.Equal(100, count);
}

// ✅ GOOD: If order matters, use explicit synchronization
[Fact]
public async Task SequentialOperations()
{
    var grain = GrainFactory.GetGrain<IMyGrain>(Guid.NewGuid());
    
    using var semaphore = new SemaphoreSlim(1);
    
    async Task SafeOperation(int value)
    {
        await semaphore.WaitAsync();
        try { await grain.Process(value); }
        finally { semaphore.Release(); }
    }
    
    await Task.WhenAll(
        SafeOperation(1),
        SafeOperation(2),
        SafeOperation(3));
}
```

### Gate Function

```
BEFORE writing concurrent test code:
  Ask: "Can this test ever produce different results on different runs?"

  IF yes:
    STOP - Make it deterministic first

  Checklist:
    □ All async operations properly awaited
    □ No race conditions between setup and assertion  
    □ No timing-dependent assertions
    □ Shared state properly synchronized

  IF test is flaky:
    DO NOT add delays to "fix" it
    DO find and fix the race condition
    DO improve observability if needed
```

## When Mocks Become Too Complex

**Warning signs:**
- Mock setup longer than test logic
- Mocking everything to make test pass
- Mocks missing methods real components have
- Test breaks when mock changes

**your human partner's question:** "Do we need to be using a mock here?"

**Consider:** Integration tests with real components often simpler than complex mocks

## TDD Prevents These Anti-Patterns

**Why TDD helps:**
1. **Write test first** → Forces you to think about what you're actually testing
2. **Watch it fail** → Confirms test tests real behavior, not mocks
3. **Minimal implementation** → No test-only methods creep in
4. **Real dependencies** → You see what the test actually needs before mocking

**If you're testing mock behavior, you violated TDD** - you added mocks without watching test fail against real code first.

## Quick Reference

| Anti-Pattern                    | Fix                                           |
| ------------------------------- | --------------------------------------------- |
| Assert on mock elements         | Test real component or unmock it              |
| Test-only methods in production | Move to test utilities                        |
| Mock without understanding      | Understand dependencies first, mock minimally |
| Incomplete mocks                | Mirror real API completely                    |
| Tests as afterthought           | TDD - tests first                             |
| Over-complex mocks              | Consider integration tests                    |
| Fixed delays (Task.Delay)       | Event-based waiting or polling with guard     |
| Untestable behavior             | Improve system testability, then rewrite test |
| Non-deterministic tests         | Proper synchronization, no timing dependence  |

## Red Flags

- Assertion checks for `*-mock` test IDs
- Methods only called in test files
- Mock setup is >50% of test
- Test fails when you remove mock
- Can't explain why mock is needed
- Mocking "just to be safe"
- `Task.Delay` or `Thread.Sleep` without loop/guard condition
- "Just increase the delay" to fix flaky test
- Fire-and-forget operations with no completion signal
- Test passes locally but fails in CI (or vice versa)
- Test marked `[Trait("Flaky", "true")]` without investigation

## The Bottom Line

**Mocks are tools to isolate, not things to test.**

If TDD reveals you're testing mock behavior, you've gone wrong.

Fix: Test real behavior or question why you're mocking at all.