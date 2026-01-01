# Project Determinism - Orleans Deterministic Testing Initiative

## Executive Summary

Project Determinism is an initiative to improve the observability and testability of Microsoft Orleans by making tests deterministic wherever possible. This involves eliminating timing-based waits (`Task.Delay`, `Thread.Sleep`, polling loops) and replacing them with mechanisms that leverage:

1. **DiagnosticListener hooks** for in-process event observation
2. **System.TimeProvider** and **FakeTimeProvider** for virtual time
3. **ILoggerProvider interception** for log-level assertions
4. **InProcessTestCluster improvements** for easier multi-silo testing

## Current State Analysis

### Existing Infrastructure Strengths

| Component | Location | Notes |
|-----------|----------|-------|
| `InProcessTestCluster` | `src/Orleans.TestingHost/InProcTestCluster.cs` | Already supports in-memory transport, shared membership table, shared grain directory |
| `TimeProvider` integration | `src/Orleans.Runtime/Hosting/DefaultSiloServices.cs:67` | Already registered as `TimeProvider.System` by default |
| `GrainTimer` | `src/Orleans.Runtime/Timers/GrainTimer.cs` | Already uses `TimeProvider.CreateTimer()` |
| `DiagnosticListener` | `src/Orleans.Core/Diagnostics/MessagingTrace.cs` | Exists but limited scope |
| `TestHooksSystemTarget` | `src/Orleans.Runtime/Silo/TestHooks/` | White-box testing hooks exist |
| `InMemoryTransport` | `src/Orleans.TestingHost/InMemoryTransport/` | Already eliminates network latency |

### Problem Areas (Technical Debt)

| Pattern | Count | Examples |
|---------|-------|----------|
| `Task.Delay` in tests | 100+ | Activation collection, liveness stabilization |
| `Thread.Sleep` in tests | 53 | Reminder tests, synchronization waits |
| `TestingUtils.WaitUntilAsync` | 70+ | Streaming tests, queue balancer tests |
| `WaitForLivenessToStabilizeAsync` | Multiple | Uses calculated real-time waits |

## Architecture

### High-Level Design

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                        Test Harness Layer                                    │
├─────────────────────────────────────────────────────────────────────────────┤
│  DeterministicTestCluster                                                    │
│  ├── SimulationTimeProvider (virtual time)                                  │
│  ├── InMemoryLoggerProvider (log capture & assertions)                      │
│  ├── DiagnosticEventCollector (event hooks)                                 │
│  └── InProcessTestCluster (existing infrastructure)                         │
└─────────────────────────────────────────────────────────────────────────────┘
                                     │
                                     ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                        Orleans Runtime (Modified)                            │
├─────────────────────────────────────────────────────────────────────────────┤
│  DiagnosticListener Integration:                                             │
│  ├── Orleans.Silo.Lifecycle      (stage transitions)                        │
│  ├── Orleans.Activation          (create/deactivate/collect)                │
│  ├── Orleans.Reminders           (register/fire/unregister)                 │
│  ├── Orleans.Streaming           (produce/consume/agent lifecycle)          │
│  ├── Orleans.Membership          (join/leave/suspect/declare dead)          │
│  └── Orleans.Placement            (placement decisions)                      │
│                                                                              │
│  TimeProvider Usage:                                                         │
│  ├── GrainTimer (already done)                                              │
│  ├── ActivationCollector (partial)                                          │
│  ├── Reminder scheduling (needs work)                                        │
│  ├── Membership probing (needs work)                                         │
│  └── Gateway refresh (needs work)                                            │
└─────────────────────────────────────────────────────────────────────────────┘
```

## Implementation Plan

### Phase 1: Foundation Infrastructure

#### 1.1 DiagnosticListener Event System

Create a centralized diagnostic event infrastructure in Orleans:

**New Files:**
- `src/Orleans.Core.Abstractions/Diagnostics/OrleansDiagnosticNames.cs`
- `src/Orleans.Core.Abstractions/Diagnostics/Events/LifecycleEvents.cs`
- `src/Orleans.Core.Abstractions/Diagnostics/Events/ActivationEvents.cs`
- `src/Orleans.Core.Abstractions/Diagnostics/Events/ReminderEvents.cs`
- `src/Orleans.Core.Abstractions/Diagnostics/Events/MembershipEvents.cs`

```csharp
// OrleansDiagnosticNames.cs
namespace Orleans.Diagnostics;

public static class OrleansDiagnosticNames
{
    public const string ListenerName = "Orleans";
    
    // Lifecycle events
    public const string SiloLifecycleStageStarting = "Orleans.Silo.Lifecycle.StageStarting";
    public const string SiloLifecycleStageCompleted = "Orleans.Silo.Lifecycle.StageCompleted";
    public const string SiloLifecycleStageFailed = "Orleans.Silo.Lifecycle.StageFailed";
    
    // Activation events
    public const string ActivationCreated = "Orleans.Activation.Created";
    public const string ActivationDeactivating = "Orleans.Activation.Deactivating";
    public const string ActivationDeactivated = "Orleans.Activation.Deactivated";
    public const string ActivationCollectionCycleStarted = "Orleans.Activation.CollectionCycleStarted";
    public const string ActivationCollectionCycleCompleted = "Orleans.Activation.CollectionCycleCompleted";
    
    // Reminder events  
    public const string ReminderRegistered = "Orleans.Reminders.Registered";
    public const string ReminderFiring = "Orleans.Reminders.Firing";
    public const string ReminderFired = "Orleans.Reminders.Fired";
    public const string ReminderUnregistered = "Orleans.Reminders.Unregistered";
    
    // Membership events
    public const string MembershipSiloJoining = "Orleans.Membership.SiloJoining";
    public const string MembershipSiloActive = "Orleans.Membership.SiloActive";
    public const string MembershipSiloSuspected = "Orleans.Membership.SiloSuspected";
    public const string MembershipSiloDead = "Orleans.Membership.SiloDead";
    public const string MembershipViewChanged = "Orleans.Membership.ViewChanged";
}
```

#### 1.2 InMemoryLoggerProvider for Testing

Port the `InMemoryLoggerProvider` from RapidCluster's Clockwork library:

**New Files:**
- `src/Orleans.TestingHost/Logging/InMemoryLoggerProvider.cs`
- `src/Orleans.TestingHost/Logging/InMemoryLogBuffer.cs`

```csharp
// InMemoryLogBuffer.cs
namespace Orleans.TestingHost.Logging;

public sealed class InMemoryLogBuffer
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private readonly TimeProvider _timeProvider;
    
    public InMemoryLogBuffer(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }
    
    public IReadOnlyList<LogEntry> AllEntries => [.. _entries];
    
    public IEnumerable<LogEntry> GetEntries(LogLevel minimumLevel) 
        => _entries.Where(e => e.LogLevel >= minimumLevel);
    
    public bool HasEntriesAtOrAbove(LogLevel level) 
        => _entries.Any(e => e.LogLevel >= level);
    
    public void AssertNoWarningsOrErrors()
    {
        var issues = GetEntries(LogLevel.Warning).ToList();
        if (issues.Count > 0)
        {
            throw new XunitException($"Found {issues.Count} warnings/errors...");
        }
    }
}

public readonly record struct LogEntry(
    DateTimeOffset Timestamp,
    LogLevel LogLevel,
    string Category,
    EventId EventId,
    string Message,
    Exception? Exception);
```

#### 1.3 DiagnosticEventCollector for Tests

Create a test utility for subscribing to diagnostic events:

**New File:** `src/Orleans.TestingHost/Diagnostics/DiagnosticEventCollector.cs`

```csharp
namespace Orleans.TestingHost.Diagnostics;

public sealed class DiagnosticEventCollector : IDisposable, IObserver<DiagnosticListener>
{
    private readonly ConcurrentQueue<DiagnosticEvent> _events = new();
    private readonly IDisposable _subscription;
    private readonly HashSet<string> _subscribedEvents;
    
    public DiagnosticEventCollector(params string[] eventNames)
    {
        _subscribedEvents = new HashSet<string>(eventNames);
        _subscription = DiagnosticListener.AllListeners.Subscribe(this);
    }
    
    public IReadOnlyList<DiagnosticEvent> Events => [.. _events];
    
    public Task<DiagnosticEvent> WaitForEventAsync(
        string eventName, 
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
    
    public TaskCompletionSource<DiagnosticEvent> CreateEventAwaiter(string eventName);
}

public readonly record struct DiagnosticEvent(
    string Name,
    object? Payload,
    DateTimeOffset Timestamp);
```

### Phase 2: Runtime Instrumentation

#### 2.1 Lifecycle Events

Modify `SiloLifecycleSubject` to emit diagnostic events:

```csharp
// In SiloLifecycleSubject.cs
private static readonly DiagnosticListener s_diagnosticListener = new(OrleansDiagnosticNames.ListenerName);

private async Task OnStartStage(int stage)
{
    if (s_diagnosticListener.IsEnabled(OrleansDiagnosticNames.SiloLifecycleStageStarting))
    {
        s_diagnosticListener.Write(OrleansDiagnosticNames.SiloLifecycleStageStarting, 
            new { SiloAddress = _siloAddress, Stage = stage, StageName = GetStageName(stage) });
    }
    
    var sw = Stopwatch.StartNew();
    try
    {
        await RunStage(stage);
        
        if (s_diagnosticListener.IsEnabled(OrleansDiagnosticNames.SiloLifecycleStageCompleted))
        {
            s_diagnosticListener.Write(OrleansDiagnosticNames.SiloLifecycleStageCompleted,
                new { SiloAddress = _siloAddress, Stage = stage, Duration = sw.Elapsed });
        }
    }
    catch (Exception ex)
    {
        if (s_diagnosticListener.IsEnabled(OrleansDiagnosticNames.SiloLifecycleStageFailed))
        {
            s_diagnosticListener.Write(OrleansDiagnosticNames.SiloLifecycleStageFailed,
                new { SiloAddress = _siloAddress, Stage = stage, Exception = ex });
        }
        throw;
    }
}
```

#### 2.2 Activation Collection Events

Modify `ActivationCollector` to emit events:

```csharp
// In ActivationCollector.cs
public async Task CollectActivationsAsync(bool force, CancellationToken cancellationToken)
{
    if (s_diagnosticListener.IsEnabled(OrleansDiagnosticNames.ActivationCollectionCycleStarted))
    {
        s_diagnosticListener.Write(OrleansDiagnosticNames.ActivationCollectionCycleStarted,
            new { SiloAddress = _siloAddress, ActivationCount = _activationDirectory.Count });
    }
    
    var collected = await DoCollectAsync(force, cancellationToken);
    
    if (s_diagnosticListener.IsEnabled(OrleansDiagnosticNames.ActivationCollectionCycleCompleted))
    {
        s_diagnosticListener.Write(OrleansDiagnosticNames.ActivationCollectionCycleCompleted,
            new { SiloAddress = _siloAddress, CollectedCount = collected });
    }
}
```

#### 2.3 Reminder Events

Modify `LocalReminderService` to emit events:

```csharp
// In LocalReminderService.cs
private async Task FireReminder(IGrainReminder reminder)
{
    if (s_diagnosticListener.IsEnabled(OrleansDiagnosticNames.ReminderFiring))
    {
        s_diagnosticListener.Write(OrleansDiagnosticNames.ReminderFiring,
            new { GrainId = reminder.GrainId, ReminderName = reminder.ReminderName });
    }
    
    var sw = Stopwatch.StartNew();
    var success = false;
    try
    {
        await InvokeReminderAsync(reminder);
        success = true;
    }
    finally
    {
        if (s_diagnosticListener.IsEnabled(OrleansDiagnosticNames.ReminderFired))
        {
            s_diagnosticListener.Write(OrleansDiagnosticNames.ReminderFired,
                new { GrainId = reminder.GrainId, ReminderName = reminder.ReminderName, 
                      Duration = sw.Elapsed, Success = success });
        }
    }
}
```

#### 2.4 Membership Events

Modify `MembershipTableManager` and related classes:

```csharp
// In MembershipTableManager.cs
private void OnMembershipChanged(MembershipTableSnapshot snapshot)
{
    if (s_diagnosticListener.IsEnabled(OrleansDiagnosticNames.MembershipViewChanged))
    {
        s_diagnosticListener.Write(OrleansDiagnosticNames.MembershipViewChanged,
            new { Version = snapshot.Version, 
                  ActiveCount = snapshot.Entries.Count(e => e.Value.Status == SiloStatus.Active) });
    }
}
```

### Phase 3: TimeProvider Integration

#### 3.1 Extend TimeProvider Usage

Currently, `TimeProvider` is used in:
- `GrainTimer` (complete)
- `ActivationCollector` (partial)
- `ActivationData` (partial)

Extend to:
- `LocalReminderService` - Use `TimeProvider` for reminder scheduling
- `MembershipAgent` - Use `TimeProvider` for probe timing
- `ClusterHealthMonitor` - Use `TimeProvider` for health check intervals
- `GatewayManager` - Use `TimeProvider` for gateway refresh

```csharp
// Example: LocalReminderService with TimeProvider
public class LocalReminderService
{
    private readonly TimeProvider _timeProvider;
    
    public LocalReminderService(TimeProvider timeProvider, ...)
    {
        _timeProvider = timeProvider;
    }
    
    private ITimer CreateReminderTimer(TimeSpan dueTime, TimeSpan period)
    {
        return _timeProvider.CreateTimer(
            OnReminderTick, 
            state: null, 
            dueTime, 
            period);
    }
}
```

#### 3.2 AsyncTimerFactory with TimeProvider

Modify `AsyncTimerFactory` to use `TimeProvider`:

```csharp
// In AsyncTimerFactory.cs
public class AsyncTimerFactory : IAsyncTimerFactory
{
    private readonly TimeProvider _timeProvider;
    
    public AsyncTimerFactory(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }
    
    public IAsyncTimer Create(TimeSpan period, string name)
    {
        return new AsyncTimer(_timeProvider, period, name);
    }
}

public class AsyncTimer : IAsyncTimer
{
    private readonly TimeProvider _timeProvider;
    
    public AsyncTimer(TimeProvider timeProvider, TimeSpan period, string name)
    {
        _timeProvider = timeProvider;
        // Use _timeProvider.CreateTimer(...) instead of Task.Delay
    }
}
```

### Phase 4: Test Infrastructure Enhancement

#### 4.1 DeterministicTestCluster

Create a new test cluster type optimized for deterministic testing:

**New File:** `src/Orleans.TestingHost/DeterministicTestCluster.cs`

```csharp
namespace Orleans.TestingHost;

/// <summary>
/// A test cluster optimized for deterministic, repeatable testing with virtual time.
/// </summary>
public sealed class DeterministicTestCluster : IAsyncDisposable
{
    private readonly InProcessTestCluster _cluster;
    private readonly FakeTimeProvider _timeProvider;
    private readonly InMemoryLoggerProvider _loggerProvider;
    private readonly DiagnosticEventCollector _eventCollector;
    
    public DeterministicTestCluster(DeterministicTestClusterOptions options)
    {
        _timeProvider = new FakeTimeProvider(options.StartTime);
        _loggerProvider = new InMemoryLoggerProvider(_timeProvider);
        _eventCollector = new DiagnosticEventCollector(options.SubscribedEvents);
        
        var clusterOptions = new InProcessTestClusterOptions
        {
            ClusterId = options.ClusterId ?? $"test-{Guid.NewGuid():N}",
            ServiceId = options.ServiceId ?? "test-service",
            InitialSilosCount = options.InitialSilosCount,
        };
        
        clusterOptions.SiloHostConfigurationDelegates.Add((siloOptions, builder) =>
        {
            // Inject FakeTimeProvider
            builder.Services.AddSingleton<TimeProvider>(_timeProvider);
            
            // Add in-memory logging
            builder.Logging.AddProvider(_loggerProvider);
        });
        
        _cluster = new InProcessTestCluster(clusterOptions, new TestClusterPortAllocator());
    }
    
    /// <summary>
    /// The virtual time provider. Use to advance time deterministically.
    /// </summary>
    public FakeTimeProvider TimeProvider => _timeProvider;
    
    /// <summary>
    /// The captured log buffer. Use to assert on log levels.
    /// </summary>
    public InMemoryLogBuffer LogBuffer => _loggerProvider.Buffer;
    
    /// <summary>
    /// The diagnostic event collector. Use to wait for events.
    /// </summary>
    public DiagnosticEventCollector Events => _eventCollector;
    
    /// <summary>
    /// Advances time and processes any triggered work.
    /// </summary>
    public void AdvanceTime(TimeSpan delta)
    {
        _timeProvider.Advance(delta);
    }
    
    /// <summary>
    /// Waits for a specific diagnostic event without relying on real time.
    /// </summary>
    public Task<DiagnosticEvent> WaitForEventAsync(
        string eventName,
        CancellationToken cancellationToken = default)
    {
        return _eventCollector.WaitForEventAsync(eventName, Timeout.InfiniteTimeSpan, cancellationToken);
    }
    
    /// <summary>
    /// Waits until a condition is met or max iterations exceeded.
    /// Advances virtual time between checks.
    /// </summary>
    public async Task<bool> WaitUntilAsync(
        Func<Task<bool>> condition,
        TimeSpan? checkInterval = null,
        int maxIterations = 1000)
    {
        var interval = checkInterval ?? TimeSpan.FromMilliseconds(100);
        
        for (int i = 0; i < maxIterations; i++)
        {
            if (await condition())
                return true;
            
            AdvanceTime(interval);
            await Task.Yield(); // Allow timers to fire
        }
        
        return false;
    }
    
    /// <summary>
    /// Asserts that no warnings or errors were logged.
    /// </summary>
    public void AssertNoWarningsOrErrors()
    {
        LogBuffer.AssertNoWarningsOrErrors();
    }
}
```

#### 4.2 Test Helper Extensions

**New File:** `src/Orleans.TestingHost/Extensions/DeterministicTestExtensions.cs`

```csharp
namespace Orleans.TestingHost;

public static class DeterministicTestExtensions
{
    /// <summary>
    /// Waits for activation collection to complete.
    /// </summary>
    public static Task WaitForActivationCollectionAsync(
        this DeterministicTestCluster cluster,
        CancellationToken cancellationToken = default)
    {
        return cluster.WaitForEventAsync(
            OrleansDiagnosticNames.ActivationCollectionCycleCompleted,
            cancellationToken);
    }
    
    /// <summary>
    /// Waits for a reminder to fire.
    /// </summary>
    public static Task WaitForReminderFiredAsync(
        this DeterministicTestCluster cluster,
        GrainId grainId,
        string reminderName,
        CancellationToken cancellationToken = default)
    {
        return cluster.Events.WaitForEventAsync(
            OrleansDiagnosticNames.ReminderFired,
            e => e.Payload is { GrainId: var gid, ReminderName: var name } 
                 && gid == grainId && name == reminderName,
            cancellationToken);
    }
    
    /// <summary>
    /// Waits for membership to stabilize (all silos see the same view).
    /// </summary>
    public static async Task WaitForMembershipStabilizationAsync(
        this DeterministicTestCluster cluster,
        int expectedActiveSilos,
        CancellationToken cancellationToken = default)
    {
        await cluster.WaitUntilAsync(async () =>
        {
            foreach (var silo in cluster.Silos)
            {
                var hooks = cluster.Client.GetTestHooks(silo);
                var statuses = await hooks.GetApproximateSiloStatuses();
                var activeCount = statuses.Count(s => s.Value == SiloStatus.Active);
                if (activeCount != expectedActiveSilos)
                    return false;
            }
            return true;
        }, cancellationToken: cancellationToken);
    }
}
```

### Phase 5: Test Migration

#### 5.1 Migration Pattern

Convert existing timing-based tests to use the new infrastructure:

**Before (Non-Deterministic):**
```csharp
[Fact]
public async Task Grain_ShouldDeactivate_AfterIdleTimeout()
{
    var grain = Client.GetGrain<IMyGrain>(Guid.NewGuid());
    await grain.DoSomething();
    
    // BAD: Real-time wait
    await Task.Delay(TimeSpan.FromMinutes(2));
    
    // Hope the grain was collected...
    var isActive = await IsGrainActive(grain);
    Assert.False(isActive);
}
```

**After (Deterministic):**
```csharp
[Fact]
public async Task Grain_ShouldDeactivate_AfterIdleTimeout()
{
    await using var cluster = new DeterministicTestCluster(new()
    {
        InitialSilosCount = 1,
        SubscribedEvents = [OrleansDiagnosticNames.ActivationDeactivated]
    });
    await cluster.DeployAsync();
    
    var grain = cluster.Client.GetGrain<IMyGrain>(Guid.NewGuid());
    await grain.DoSomething();
    
    // GOOD: Virtual time advancement
    cluster.AdvanceTime(TimeSpan.FromMinutes(2));
    
    // Wait for the deactivation event
    var deactivatedEvent = await cluster.WaitForEventAsync(
        OrleansDiagnosticNames.ActivationDeactivated);
    
    // Verify the grain was deactivated
    cluster.AssertNoWarningsOrErrors();
}
```

#### 5.2 Priority Test Categories

1. **High Priority (Flaky in CI):**
   - Activation collection tests
   - Liveness/membership tests
   - Reminder tests

2. **Medium Priority:**
   - Streaming tests (70+ `WaitUntilAsync` calls)
   - Load balancing tests

3. **Lower Priority:**
   - Tests that work reliably today

### Phase 6: Documentation & Examples

1. **Developer Guide:** How to write deterministic Orleans tests
2. **Migration Guide:** Converting existing tests
3. **API Reference:** New testing APIs
4. **Best Practices:** Patterns and anti-patterns

## Detailed Work Items

### Milestone 1: Core Infrastructure (2-3 weeks)

| Task | Files | Complexity |
|------|-------|------------|
| Create `OrleansDiagnosticNames` constants | New file | Low |
| Create diagnostic event payload types | New files | Low |
| Port `InMemoryLoggerProvider` from Clockwork | New files | Low |
| Create `DiagnosticEventCollector` | New file | Medium |
| Add `DiagnosticListener` to `SiloLifecycleSubject` | Modify existing | Medium |

### Milestone 2: Runtime Instrumentation (2-3 weeks)

| Task | Files | Complexity |
|------|-------|------------|
| Add events to `ActivationCollector` | Modify existing | Medium |
| Add events to `LocalReminderService` | Modify existing | Medium |
| Add events to membership components | Modify existing | Medium |
| Extend `TimeProvider` usage in `AsyncTimerFactory` | Modify existing | Medium |
| Extend `TimeProvider` usage in reminders | Modify existing | High |

### Milestone 3: Test Infrastructure (2-3 weeks)

| Task | Files | Complexity |
|------|-------|------------|
| Create `DeterministicTestCluster` | New file | High |
| Create `DeterministicTestClusterOptions` | New file | Low |
| Create test helper extensions | New file | Medium |
| Update `InProcessTestCluster` for `TimeProvider` injection | Modify existing | Medium |

### Milestone 4: Test Migration (4-6 weeks)

| Task | Test Categories | Complexity |
|------|-----------------|------------|
| Migrate activation collection tests | `test/TesterInternal/` | High |
| Migrate liveness tests | `test/TesterInternal/` | High |
| Migrate reminder tests | `test/Tester/` | Medium |
| Migrate streaming tests | `test/Tester/StreamingTests/` | High |

## Dependencies

### NuGet Packages Required

- `Microsoft.Extensions.TimeProvider.Testing` (for `FakeTimeProvider`)
- Already available in .NET 8+

### Breaking Changes

- **None expected for public APIs**
- Internal changes to use `TimeProvider` instead of `Task.Delay` where appropriate

## Risks & Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| Performance overhead from DiagnosticListener | Low | `IsEnabled()` checks are very fast; events only fire when subscribed |
| Test migration effort | Medium | Gradual migration; both patterns can coexist |
| Incomplete TimeProvider coverage | Medium | Focus on high-value areas first (timers, collection, reminders) |

## Success Metrics

1. **Flaky Test Reduction:** 50%+ reduction in flaky tests
2. **Test Execution Time:** 30%+ reduction by eliminating forced waits
3. **Coverage:** All timing-dependent tests have deterministic alternatives
4. **Developer Experience:** Positive feedback from contributors

## Progress Report

### Completed Work (fix/test-flakiness/1 branch)

#### Phase 1: Reminder Tests - SUCCESS

Successfully implemented deterministic reminder tests using `DiagnosticListener` hooks and `FakeTimeProvider`.

**Files Modified:**
- `src/Orleans.Reminders/ReminderService/LocalReminderService.cs`
  - Added `TimeProvider` injection
  - Added `DiagnosticListener` for reminder tick events
  - Events: `ReminderTickScheduled`, `ReminderTickCompleted`, `ReminderTickFailed`

**Files Created:**
- `src/Orleans.TestingHost/Diagnostics/DiagnosticEventCollector.cs` - Collects and awaits diagnostic events
- Diagnostic event infrastructure in `Orleans.Reminders`

**Test Results:**
- `ReminderTests_TableGrain` tests now complete in ~6-8 seconds (previously took minutes)
- 5/5 tests pass consistently
- Uses event-driven waiting instead of `Task.Delay`

**Key Pattern:**
```csharp
// Wait for reminder tick using diagnostic events
await WaitForReminderTickCountAsync(grain, reminderName, expectedCount, timeout);

// Advance virtual time to trigger ticks
await AdvanceTimeAsync(period);
```

#### Phase 2: Streaming Tests - SUCCESS

Fixed streaming test flakiness by addressing the root causes of test failures.

**Root Cause Analysis:**
1. `MemoryStreamResumeTests` were failing due to **non-reentrant grain blocking**
2. The `WaitForEventCount()` method blocks inside the grain waiting for events via `TaskCompletionSource`
3. Without reentrancy, stream events cannot be delivered while the grain is blocked
4. For `ResumeAfterDeactivationActiveStream`, the grain deactivates after each event, losing the waiter state

**Solution Implemented:**
1. Added `[AlwaysInterleave]` attribute to `WaitForEventCount` on the interface
   - Allows stream events to be delivered while the method is awaiting
   - Orleans requires this attribute on the interface, not implementation (enforced by `ORLEANS0001` analyzer)

2. Added `PollForEventCount` helper for deactivation tests
   - Polls persisted state which survives deactivations
   - Used in `ResumeAfterDeactivationActiveStream` test

3. Un-skipped batching tests disabled since 2019
   - `SingleSendBatchConsume` (issue #5649)
   - `BatchSendBatchConsume` (issue #5632)
   - Both tests now pass

**Files Modified:**
- `test/Grains/TestGrainInterfaces/IImplicitSubscriptionCounterGrain.cs` - Added `[AlwaysInterleave]` to `WaitForEventCount`
- `test/Tester/StreamingTests/StreamingResumeTests.cs` - Added `PollForEventCount` helper
- `test/Tester/StreamingTests/StreamBatchingTestRunner.cs` - Un-skipped batching tests

**Test Results:**
- `MemoryStreamResumeTests`: 5/5 passing
- `MemoryStreamBatchingTests`: 3/3 passing (2 previously skipped)

**Previous Streaming Infrastructure Changes (Diagnostic Events):**
- `src/Orleans.Streaming/Diagnostics/OrleansStreamingDiagnosticEvents.cs` - Streaming diagnostic events

**Note on Full TimeProvider Integration for Streaming:**
Full TimeProvider integration is a larger effort due to `DateTime.UtcNow` usage in 16+ places:
- `MemoryPooledCache.cs` (3 uses)
- `GeneratorPooledCache.cs` (2 uses)
- `PooledQueueCache.cs` (2 uses)
- `ObjectPool.cs` (2 uses)
- And more...

This is tracked as future work and not required for test reliability.

#### Recommendations

1. **Reminder Tests:** Ready to merge - significant improvement in test speed and reliability

2. **Streaming Tests:** Ready to merge - fixed flakiness with `[AlwaysInterleave]` and polling

3. **Future Work:**
   - Complete TimeProvider integration for reminders (partially done)
   - Consider full TimeProvider refactoring for streaming (large effort, lower priority)

### Git Status

```
Branch: fix/test-flakiness/1

Recent Commits:
- Fix streaming test flakiness with AlwaysInterleave and polling
- Add streaming diagnostic events and document progress on determinism initiative
- Add ReminderDiagnosticObserver and convert more tests to event-driven waiting
- Fix flaky streaming resume tests with event-driven waiting
- Add timer diagnostic events and TimerDiagnosticObserver for event-driven timer tests
- Remove unnecessary 5-second delay in MultipleGrainDirectoriesTests

Key Modified Files:
- src/Orleans.Reminders/ReminderService/LocalReminderService.cs
- test/Grains/TestGrainInterfaces/IImplicitSubscriptionCounterGrain.cs
- test/Tester/StreamingTests/StreamingResumeTests.cs
- test/Tester/StreamingTests/StreamBatchingTestRunner.cs
- test/Grains/TestGrains/ImplicitSubscriptionCounterGrain.cs
- test/TesterInternal/TimerTests/ReminderTests_Base.cs
- test/TesterInternal/TimerTests/ReminderTests_TableGrain.cs

New Files:
- src/Orleans.Streaming/Diagnostics/OrleansStreamingDiagnosticEvents.cs
- src/Orleans.TestingHost/Diagnostics/DiagnosticEventCollector.cs (and related)
```

## References

- [Aspire DiagnosticListener pattern](https://github.com/dotnet/aspire/blob/main/src/Aspire.Hosting/DistributedApplicationBuilder.cs)
- [Aspire Testing hooks](https://github.com/dotnet/aspire/blob/main/src/Aspire.Hosting.Testing/DistributedApplicationEntryPointInvoker.cs)
- [RapidCluster Clockwork library](C:\dev\RapidCluster\src\Clockwork)
- [System.TimeProvider documentation](https://learn.microsoft.com/en-us/dotnet/api/system.timeprovider)
- [FakeTimeProvider documentation](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.time.testing.faketimeprovider)
