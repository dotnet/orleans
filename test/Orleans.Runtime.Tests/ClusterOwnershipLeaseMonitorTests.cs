using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Orleans.Configuration;
using Orleans.GrainReferences;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Runtime.Diagnostics;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Placement;
using Orleans.TestingHost.Diagnostics;
using Xunit;

namespace UnitTests.Runtime;

[TestArea("Runtime")]
[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[Trait("Phase", "7")]
[Trait("FullyQualifiedName", "UnitTests.Runtime.ClusterOwnershipLeaseMonitorTests")]
public sealed class ClusterOwnershipLeaseMonitorTests
{
    private static readonly DateTimeOffset Start = new(2042, 3, 4, 5, 6, 7, TimeSpan.Zero);

    [Fact]
    public void OwnershipAccessor_Current_IsNullOutsideInvocation()
    {
        var accessor = new ClusterOwnershipAccessor();

        Assert.Null(accessor.Current);
    }

    [Fact]
    public void OwnershipAccessor_Current_ExposesOwnerVersionEpochFenceAndLeaseDuringInvocation()
    {
        var context = new TestActivationContext(
            GrainId.Create(TestHarness.DirectoryGrainType, "accessor"),
            ActivationId.NewId());
        var expected = Entry(context.GrainId, "west", version: 17, epoch: 23, fence: 31, Start.AddMinutes(5));
        context.SetComponent(expected);
        var accessor = new ClusterOwnershipAccessor();

        var actual = RunInContext(context, () => accessor.Current);

        Assert.Same(expected, actual);
        Assert.Equal("west", actual!.ClusterId);
        Assert.Equal(17, actual.Version);
        Assert.Equal(23, actual.TopologyEpoch);
        Assert.Equal(31, actual.FencingToken);
        Assert.Equal(Start.AddMinutes(5), actual.LeaseExpiration);
        Assert.Null(accessor.Current);
    }

    [Fact]
    public void OwnershipAccessor_Current_IsRestoredAfterSuccessExceptionAndCancellation()
    {
        var accessor = new ClusterOwnershipAccessor();
        var outer = ContextWithEntry("outer", version: 2, epoch: 3, fence: 4);
        var inner = ContextWithEntry("inner", version: 5, epoch: 6, fence: 7);
        RuntimeContext.SetExecutionContext(outer, out var original);
        try
        {
            Assert.Equal("inner", RunInContext(inner, () => accessor.Current)!.ClusterId);
            Assert.Equal("outer", accessor.Current!.ClusterId);

            var failure = Assert.Throws<InvalidOperationException>(
                () => RunInContext<object?>(inner, () => throw new InvalidOperationException("boom")));
            Assert.Equal("boom", failure.Message);
            Assert.Equal("outer", accessor.Current!.ClusterId);

            var cancellation = Assert.Throws<OperationCanceledException>(
                () => RunInContext<object?>(inner, () => throw new OperationCanceledException(new CancellationToken(true))));
            Assert.True(cancellation.CancellationToken.IsCancellationRequested);
            Assert.Equal("outer", accessor.Current!.ClusterId);
        }
        finally
        {
            RuntimeContext.ResetExecutionContext(original);
        }

        Assert.Null(accessor.Current);
    }

    [Fact]
    public async Task Monitor_Start_WithValidOwnership_TracksActivationAndSchedulesRenewal()
    {
        await using var harness = new TestHarness();
        var context = harness.CreateContext("start");
        var ownership = harness.CreateEntry(context.GrainId, version: 3, epoch: 5, fence: 7);
        harness.Validator.Enqueue(ownership);

        harness.Monitor.Track(context);
        await context.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        await harness.StartMonitorAsync();
        await harness.Timer.WaitForScheduleAsync(1);

        Assert.Equal(1, harness.Validator.CallCount);
        Assert.Same(ownership, context.Ownership);
        Assert.Equal(harness.Period, harness.Timer.Period);
        Assert.Equal(nameof(ClusterOwnershipLeaseMonitor), harness.Timer.Name);
        Assert.Same(harness.TimeProvider, harness.Timer.TimeProvider);
        Assert.Equal(1, harness.Timer.ScheduleCount);
        Assert.Equal(0, context.DeactivationCount);
    }

    [Fact]
    public async Task Monitor_SubSecondRenewalWindow_SchedulesBeforeLeaseExpiry()
    {
        await using var harness = new TestHarness(TimeSpan.FromMilliseconds(250));

        Assert.Equal(TimeSpan.FromMilliseconds(125), harness.Period);
        Assert.Equal(harness.Period, harness.Timer.Period);
    }

    [Fact]
    public async Task Monitor_Disabled_DoesNotTrackOrValidateOwnership()
    {
        await using var harness = new TestHarness(enabled: false);
        var context = harness.CreateContext("disabled");

        harness.Monitor.Track(context);
        await context.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        await harness.StartMonitorAsync();

        Assert.Equal(0, harness.Validator.CallCount);
        Assert.Null(context.Ownership);
        Assert.Equal(0, harness.Timer.ScheduleCount);
    }

    [Fact]
    public async Task Monitor_Start_WithInitialValidationFailure_DeactivatesBeforeInvocation()
    {
        await using var harness = new TestHarness();
        var context = harness.CreateContext("initial-failure");
        var expected = new OwnershipLostException("initial ownership validation failed");
        harness.Validator.Enqueue(expected);
        harness.Monitor.Track(context);
        var invocationReached = false;

        var actual = await Assert.ThrowsAsync<OwnershipLostException>(
            () => ActivateThroughRuntimeBoundaryAsync(
                context,
                () => invocationReached = true,
                TestContext.Current.CancellationToken));

        Assert.Same(expected, actual);
        Assert.False(invocationReached);
        Assert.Null(context.Ownership);
        Assert.Equal(1, context.DeactivationCount);
        Assert.Equal(DeactivationReasonCode.ActivationFailed, context.DeactivationReasons.Single().ReasonCode);

        await harness.StartMonitorAsync();
        await harness.AdvanceAndTickAsync(1);
        await harness.Timer.WaitForScheduleAsync(2);
        Assert.Equal(1, harness.Validator.CallCount);
    }

    [Fact]
    public async Task Monitor_ActivationWithoutOwnershipLocator_RemainsUntracked()
    {
        await using var harness = new TestHarness();
        var context = harness.CreateContext("local-only", TestHarness.LocalGrainType);

        await harness.TrackAndActivateAsync(context);
        await harness.StartMonitorAsync();
        await harness.AdvanceAndTickAsync(1);
        await harness.Timer.WaitForScheduleAsync(2);

        Assert.Null(context.Ownership);
        Assert.Equal(0, context.OwnershipSetCount);
        Assert.Equal(0, context.DeactivationCount);
        Assert.Equal(0, harness.Validator.CallCount);
    }

    [Fact]
    public async Task Monitor_RenewBeforeExpiry_ExtendsLeaseAndKeepsFence()
    {
        await using var harness = new TestHarness();
        var context = harness.CreateContext("renew");
        var initial = harness.CreateEntry(context.GrainId, version: 8, epoch: 13, fence: 21, lease: Start.AddSeconds(3));
        var renewed = harness.CreateEntry(context.GrainId, version: 9, epoch: 13, fence: 21, lease: Start.AddSeconds(12));
        harness.Validator.Enqueue(initial);
        harness.Validator.Enqueue(renewed);

        await harness.TrackAndActivateAsync(context);
        await harness.StartMonitorAsync();
        await harness.AdvanceAndTickAsync(1);
        await context.WaitForOwnershipSetCountAsync(2);

        Assert.Equal(Start.Add(harness.Period), harness.TimeProvider.GetUtcNow());
        Assert.Same(renewed, context.Ownership);
        Assert.True(renewed.LeaseExpiration > initial.LeaseExpiration);
        Assert.Equal(initial.FencingToken, renewed.FencingToken);
        Assert.Equal(initial.TopologyEpoch, renewed.TopologyEpoch);
        Assert.Equal(2, harness.Validator.CallCount);
        Assert.Equal(0, context.DeactivationCount);
    }

    [Fact]
    public async Task Monitor_RenewsTrackedActivationsConcurrently()
    {
        await using var harness = new TestHarness();
        var first = harness.CreateContext("concurrent-first");
        var second = harness.CreateContext("concurrent-second");
        harness.Validator.Enqueue(harness.CreateEntry(first.GrainId, version: 1, epoch: 1, fence: 1));
        harness.Validator.Enqueue(harness.CreateEntry(second.GrainId, version: 2, epoch: 1, fence: 2));
        await harness.TrackAndActivateAsync(first);
        await harness.TrackAndActivateAsync(second);

        var firstRenewed = harness.CreateEntry(first.GrainId, version: 1, epoch: 1, fence: 1);
        var secondRenewed = harness.CreateEntry(second.GrainId, version: 2, epoch: 1, fence: 2);
        var firstCompletion = new TaskCompletionSource<ClusterDirectoryEntry>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCompletion = new TaskCompletionSource<ClusterDirectoryEntry>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Validator.Handler = (grainId, _) => new ValueTask<ClusterDirectoryEntry>(
            grainId == first.GrainId ? firstCompletion.Task : secondCompletion.Task);

        await harness.StartMonitorAsync();
        await harness.AdvanceAndTickAsync(1);
        await harness.Validator.WaitForCallCountAsync(4);
        firstCompletion.SetResult(firstRenewed);
        secondCompletion.SetResult(secondRenewed);
        await Task.WhenAll(
            first.WaitForOwnershipSetCountAsync(2),
            second.WaitForOwnershipSetCountAsync(2));
        await harness.Timer.WaitForScheduleAsync(2);

        Assert.Same(firstRenewed, first.Ownership);
        Assert.Same(secondRenewed, second.Ownership);
        Assert.Equal(0, first.DeactivationCount);
        Assert.Equal(0, second.DeactivationCount);
    }

    [Fact]
    public async Task Monitor_HungRenewal_DoesNotBlockOtherActivationSweeps()
    {
        await using var harness = new TestHarness();
        var hung = harness.CreateContext("hung-renewal");
        var healthy = harness.CreateContext("healthy-renewal");
        harness.Validator.Enqueue(harness.CreateEntry(hung.GrainId, version: 1, epoch: 1, fence: 1));
        harness.Validator.Enqueue(harness.CreateEntry(healthy.GrainId, version: 2, epoch: 1, fence: 2));
        await harness.TrackAndActivateAsync(hung);
        await harness.TrackAndActivateAsync(healthy);

        var healthyRenewed = harness.CreateEntry(healthy.GrainId, version: 2, epoch: 1, fence: 2);
        harness.Validator.Handler = (grainId, cancellationToken) =>
            grainId == hung.GrainId
                ? AwaitCancellationAsync(cancellationToken)
                : new ValueTask<ClusterDirectoryEntry>(healthyRenewed);

        await harness.StartMonitorAsync();
        await harness.AdvanceAndTickAsync(1);
        await harness.Validator.WaitForCallCountAsync(4).WaitAsync(TimeSpan.FromSeconds(10));
        await healthy.WaitForOwnershipSetCountAsync(2).WaitAsync(TimeSpan.FromSeconds(10));
        await harness.AdvanceAndTickAsync(2);
        await harness.Validator.WaitForCallCountAsync(5).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Same(healthyRenewed, healthy.Ownership);
        Assert.Equal(0, healthy.DeactivationCount);
        Assert.Equal(0, hung.DeactivationCount);
    }

    [Fact]
    public async Task Monitor_RenewOwnershipChange_UpdatesExposedFenceMonotonically()
    {
        await using var harness = new TestHarness();
        var context = harness.CreateContext("ownership-change");
        var initial = harness.CreateEntry(context.GrainId, "east", version: 4, epoch: 10, fence: 40);
        var renewed = harness.CreateEntry(context.GrainId, "west", version: 5, epoch: 11, fence: 41);
        harness.Validator.Enqueue(initial);
        harness.Validator.Enqueue(renewed);

        await harness.TrackAndActivateAsync(context);
        await harness.StartMonitorAsync();
        await harness.AdvanceAndTickAsync(1);
        await context.WaitForOwnershipSetCountAsync(2);
        var exposed = RunInContext(context, () => new ClusterOwnershipAccessor().Current);

        Assert.Same(renewed, exposed);
        Assert.Equal("west", exposed!.ClusterId);
        Assert.True(exposed.Version > initial.Version);
        Assert.True(exposed.TopologyEpoch > initial.TopologyEpoch);
        Assert.True(exposed.FencingToken > initial.FencingToken);
        Assert.Equal(2, harness.Validator.CallCount);
        Assert.Equal(0, context.DeactivationCount);
    }

    [Fact]
    public async Task Monitor_RenewFailure_TriggersSingleDeactivation()
    {
        await using var harness = new TestHarness();
        var context = harness.CreateContext("renew-failure");
        var initial = harness.CreateEntry(context.GrainId, version: 5, epoch: 8, fence: 13);
        var expected = new OwnershipLostException("renewal rejected");
        harness.Validator.Enqueue(initial);
        harness.Validator.Enqueue(expected);

        await harness.TrackAndActivateAsync(context);
        await harness.StartMonitorAsync();
        await harness.AdvanceAndTickAsync(1);
        await context.WaitForDeactivationCountAsync(1);
        await harness.Timer.WaitForScheduleAsync(2);

        var reason = Assert.Single(context.DeactivationReasons);
        Assert.Equal(DeactivationReasonCode.DirectoryFailure, reason.ReasonCode);
        Assert.Same(expected, reason.Exception);
        Assert.Contains("ownership lease", reason.Description, StringComparison.Ordinal);
        Assert.Equal(2, harness.Validator.CallCount);

        await harness.AdvanceAndTickAsync(2);
        await harness.Timer.WaitForScheduleAsync(3);
        Assert.Equal(1, context.DeactivationCount);
        Assert.Equal(2, harness.Validator.CallCount);
    }

    [Fact]
    public async Task Monitor_ExpiredLease_RejectsInvocationAndDeactivates()
    {
        await using var harness = new TestHarness();
        var context = harness.CreateContext("expired");
        var initial = harness.CreateEntry(
            context.GrainId,
            version: 12,
            epoch: 15,
            fence: 18,
            lease: Start.AddSeconds(1));
        harness.Validator.Enqueue(initial);
        harness.Validator.Enqueue((_, _) =>
        {
            if (harness.TimeProvider.GetUtcNow() >= initial.LeaseExpiration)
            {
                throw new OwnershipLostException("lease expired");
            }

            return new ValueTask<ClusterDirectoryEntry>(initial);
        });

        await harness.TrackAndActivateAsync(context);
        await harness.StartMonitorAsync();
        await harness.AdvanceAndTickAsync(1);
        await context.WaitForDeactivationCountAsync(1);
        var invocationReached = context.DeactivationCount == 0;

        Assert.False(invocationReached);
        Assert.Equal(Start.Add(harness.Period), harness.TimeProvider.GetUtcNow());
        Assert.Equal(initial, context.Ownership);
        Assert.Equal(DeactivationReasonCode.DirectoryFailure, context.DeactivationReasons.Single().ReasonCode);
        Assert.IsType<OwnershipLostException>(context.DeactivationReasons.Single().Exception);
        Assert.Equal(2, harness.Validator.CallCount);
    }

    [Fact]
    public async Task Monitor_StaleVersionEpochOrFence_RejectsInvocation()
    {
        await using var harness = new TestHarness();
        var contexts = new[]
        {
            harness.CreateContext("stale-version"),
            harness.CreateContext("stale-epoch"),
            harness.CreateContext("stale-fence"),
        };
        var current = contexts.ToDictionary(
            static context => context.GrainId,
            context => harness.CreateEntry(context.GrainId, version: 10, epoch: 20, fence: 30));
        var candidates = new Dictionary<GrainId, ClusterDirectoryEntry>
        {
            [contexts[0].GrainId] = harness.CreateEntry(contexts[0].GrainId, version: 9, epoch: 20, fence: 30),
            [contexts[1].GrainId] = harness.CreateEntry(contexts[1].GrainId, version: 10, epoch: 19, fence: 30),
            [contexts[2].GrainId] = harness.CreateEntry(contexts[2].GrainId, version: 10, epoch: 20, fence: 29),
        };
        var calls = new ConcurrentDictionary<GrainId, int>();
        harness.Validator.Handler = (grainId, _) =>
        {
            if (calls.AddOrUpdate(grainId, 1, static (_, count) => count + 1) == 1)
            {
                return new ValueTask<ClusterDirectoryEntry>(current[grainId]);
            }

            var candidate = candidates[grainId];
            var expected = current[grainId];
            if (candidate.Version < expected.Version
                || candidate.TopologyEpoch < expected.TopologyEpoch
                || candidate.FencingToken < expected.FencingToken)
            {
                throw new OwnershipLostException($"stale ownership for {grainId}");
            }

            return new ValueTask<ClusterDirectoryEntry>(candidate);
        };

        foreach (var context in contexts)
        {
            await harness.TrackAndActivateAsync(context);
        }

        await harness.StartMonitorAsync();
        await harness.AdvanceAndTickAsync(1);
        await harness.Validator.WaitForCallCountAsync(6);
        await Task.WhenAll(contexts.Select(context => context.WaitForDeactivationCountAsync(1)));

        Assert.All(contexts, context =>
        {
            Assert.Equal(1, context.DeactivationCount);
            Assert.Same(current[context.GrainId], context.Ownership);
            Assert.IsType<OwnershipLostException>(context.DeactivationReasons.Single().Exception);
        });
        Assert.Equal(6, harness.Validator.CallCount);
    }

    [Fact]
    public async Task Monitor_Untrack_CancelsScheduleAndPreventsFurtherRenewal()
    {
        await using var harness = new TestHarness();
        var context = harness.CreateContext("untrack");
        var initial = harness.CreateEntry(context.GrainId, version: 2, epoch: 3, fence: 5);
        harness.Validator.Enqueue(initial);

        await harness.TrackAndActivateAsync(context);
        await harness.StartMonitorAsync();
        await harness.Timer.WaitForScheduleAsync(1);
        harness.Monitor.Untrack(context);
        await harness.AdvanceAndTickAsync(1);
        await harness.Timer.WaitForScheduleAsync(2);

        Assert.Equal(1, harness.Validator.CallCount);
        Assert.Same(initial, context.Ownership);
        Assert.Equal(0, context.DeactivationCount);
        Assert.Equal(2, harness.Timer.ScheduleCount);
    }

    [Fact]
    public async Task Monitor_Stop_CancelsAllSchedulesAndAwaitsWorkers()
    {
        await using var harness = new TestHarness();
        var context = harness.CreateContext("stop");
        var initial = harness.CreateEntry(context.GrainId, version: 3, epoch: 4, fence: 5);
        var renewed = harness.CreateEntry(context.GrainId, version: 4, epoch: 4, fence: 5);
        var renewal = new TaskCompletionSource<ClusterDirectoryEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Validator.Enqueue(initial);
        harness.Validator.Enqueue((_, _) => new ValueTask<ClusterDirectoryEntry>(renewal.Task));

        await harness.TrackAndActivateAsync(context);
        await harness.StartMonitorAsync();
        await harness.AdvanceAndTickAsync(1);
        await harness.Validator.WaitForCallCountAsync(2);
        await harness.Timer.WaitForScheduleAsync(2);

        var stopTask = harness.StopMonitorAsync();
        Assert.True(harness.Timer.IsDisposed);
        Assert.False(stopTask.IsCompleted);

        renewal.SetResult(renewed);
        await stopTask;

        Assert.True(stopTask.IsCompletedSuccessfully);
        Assert.Same(renewed, context.Ownership);
        Assert.Equal(2, harness.Validator.CallCount);
        Assert.Equal(1, harness.Timer.DisposeCount);
        Assert.Equal(2, harness.Timer.ScheduleCount);
        Assert.Equal(0, context.DeactivationCount);
    }

    [Fact]
    public async Task Monitor_MultipleActivations_IsolateOwnershipState()
    {
        await using var harness = new TestHarness();
        var first = harness.CreateContext("first");
        var second = harness.CreateContext("second");
        var calls = new ConcurrentDictionary<GrainId, int>();
        harness.Validator.Handler = (grainId, _) =>
        {
            var call = calls.AddOrUpdate(grainId, 1, static (_, count) => count + 1);
            var seed = grainId == first.GrainId ? 100L : 200L;
            return new ValueTask<ClusterDirectoryEntry>(
                harness.CreateEntry(
                    grainId,
                    grainId == first.GrainId ? "east" : "west",
                    version: seed + call,
                    epoch: seed + 10,
                    fence: seed + 20,
                    lease: harness.TimeProvider.GetUtcNow().AddSeconds(10 + call)));
        };

        await harness.TrackAndActivateAsync(first);
        await harness.TrackAndActivateAsync(second);
        var firstInitial = first.Ownership;
        var secondInitial = second.Ownership;
        await harness.StartMonitorAsync();
        await harness.AdvanceAndTickAsync(1);
        await Task.WhenAll(
            first.WaitForOwnershipSetCountAsync(2),
            second.WaitForOwnershipSetCountAsync(2));

        Assert.Equal("east", first.Ownership!.ClusterId);
        Assert.Equal("west", second.Ownership!.ClusterId);
        Assert.Equal(firstInitial!.Version + 1, first.Ownership.Version);
        Assert.Equal(secondInitial!.Version + 1, second.Ownership.Version);
        Assert.NotEqual(first.Ownership.FencingToken, second.Ownership.FencingToken);
        Assert.Equal(2, calls[first.GrainId]);
        Assert.Equal(2, calls[second.GrainId]);
        Assert.Equal(0, first.DeactivationCount);
        Assert.Equal(0, second.DeactivationCount);
    }

    [Fact]
    public async Task Monitor_ConcurrentRenewAndUntrack_HasSingleTerminalState()
    {
        await using var harness = new TestHarness();
        var context = harness.CreateContext("concurrent");
        var initial = harness.CreateEntry(context.GrainId, version: 30, epoch: 40, fence: 50);
        var renewed = harness.CreateEntry(context.GrainId, version: 31, epoch: 40, fence: 50);
        var renewal = new TaskCompletionSource<ClusterDirectoryEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Validator.Enqueue(initial);
        harness.Validator.Enqueue((_, _) => new ValueTask<ClusterDirectoryEntry>(renewal.Task));

        await harness.TrackAndActivateAsync(context);
        await harness.StartMonitorAsync();
        await harness.AdvanceAndTickAsync(1);
        await harness.Validator.WaitForCallCountAsync(2);

        harness.Monitor.Untrack(context);
        renewal.SetResult(renewed);
        await context.WaitForOwnershipSetCountAsync(2);
        await harness.Timer.WaitForScheduleAsync(2);
        await harness.AdvanceAndTickAsync(2);
        await harness.Timer.WaitForScheduleAsync(3);

        Assert.Same(renewed, context.Ownership);
        Assert.Equal(2, harness.Validator.CallCount);
        Assert.Equal(0, context.DeactivationCount);
        Assert.Equal(3, harness.Timer.ScheduleCount);
    }

    [Fact]
    public async Task GrainTypeSharedContext_RegistersAndUnregistersTrackedActivation()
    {
        await using var harness = new TestHarness();
        var context = harness.CreateContext("shared-lifecycle");
        var initial = harness.CreateEntry(context.GrainId, version: 7, epoch: 11, fence: 13);
        harness.Validator.Enqueue(initial);
        var shared = harness.CreateSharedContext(TestHarness.DirectoryGrainType);

        shared.OnCreateActivation(context);
        await context.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        await harness.StartMonitorAsync();
        await harness.Timer.WaitForScheduleAsync(1);
        shared.OnDestroyActivation(context);
        await harness.AdvanceAndTickAsync(1);
        await harness.Timer.WaitForScheduleAsync(2);

        Assert.Same(initial, context.Ownership);
        Assert.Equal(1, harness.Validator.CallCount);
        Assert.Equal(0, context.DeactivationCount);
        Assert.Equal(2, harness.Timer.ScheduleCount);
    }

    [Fact]
    public async Task GrainTypeSharedContext_ExposesOwnershipComponentOnlyForDirectoryBackedType()
    {
        await using var harness = new TestHarness();
        var directoryContext = harness.CreateContext("directory-backed", TestHarness.DirectoryGrainType);
        var localContext = harness.CreateContext("local", TestHarness.LocalGrainType);
        var ownership = harness.CreateEntry(directoryContext.GrainId, version: 5, epoch: 6, fence: 7);
        harness.Validator.Enqueue(ownership);
        var directoryShared = harness.CreateSharedContext(TestHarness.DirectoryGrainType);
        var localShared = harness.CreateSharedContext(TestHarness.LocalGrainType);

        directoryShared.OnCreateActivation(directoryContext);
        localShared.OnCreateActivation(localContext);
        await directoryContext.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        await localContext.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        var accessor = new ClusterOwnershipAccessor();

        Assert.Same(ownership, RunInContext(directoryContext, () => accessor.Current));
        Assert.Null(RunInContext(localContext, () => accessor.Current));
        Assert.Equal(1, harness.Validator.CallCount);
        Assert.Equal(1, directoryContext.OwnershipSetCount);
        Assert.Equal(0, localContext.OwnershipSetCount);

        directoryShared.OnDestroyActivation(directoryContext);
        localShared.OnDestroyActivation(localContext);
    }

    [Fact]
    public async Task Monitor_CallerCancellation_IsDistinctFromLeaseFailure()
    {
        await using var harness = new TestHarness();
        var context = harness.CreateContext("caller-cancellation");
        harness.Validator.Enqueue(
            static (_, cancellationToken) => AwaitCancellationAsync(cancellationToken));
        harness.Monitor.Track(context);
        using var callerCancellation = new CancellationTokenSource();

        var activationTask = context.Lifecycle.OnStart(callerCancellation.Token);
        await harness.Validator.WaitForCallCountAsync(1);
        callerCancellation.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => activationTask);

        Assert.True(exception.CancellationToken.IsCancellationRequested);
        Assert.True(harness.Validator.CancellationTokens.Single().IsCancellationRequested);
        Assert.Null(context.Ownership);
        Assert.Equal(0, context.DeactivationCount);

        await harness.StartMonitorAsync();
        await harness.AdvanceAndTickAsync(1);
        await harness.Timer.WaitForScheduleAsync(2);
        Assert.Equal(1, harness.Validator.CallCount);
    }

    [Fact]
    public async Task Monitor_EachLifecycleTransition_EmitsExpectedDiagnosticEvent()
    {
        await using var harness = new TestHarness();
        using var events = new DiagnosticEventCollector(SiloLifecycleEvents.ListenerName);
        var starting = events.CreateEventAwaiter(nameof(SiloLifecycleEvents.ObserverStarting));
        var completed = events.CreateEventAwaiter(nameof(SiloLifecycleEvents.ObserverCompleted));

        await harness.StartMonitorAsync();
        var startEvents = await Task.WhenAll(starting.Task, completed.Task);
        var stopping = events.CreateEventAwaiter(nameof(SiloLifecycleEvents.ObserverStopping));
        var stopped = events.CreateEventAwaiter(nameof(SiloLifecycleEvents.ObserverStopped));
        await harness.StopMonitorAsync();
        var stopEvents = await Task.WhenAll(stopping.Task, stopped.Task);

        var observerEvents = startEvents.Concat(stopEvents)
            .Select(static item => Assert.IsAssignableFrom<SiloLifecycleEvents.LifecycleEvent>(item.Payload))
            .ToArray();
        Assert.Collection(
            observerEvents,
            item => Assert.IsType<SiloLifecycleEvents.ObserverStarting>(item),
            item => Assert.IsType<SiloLifecycleEvents.ObserverCompleted>(item),
            item => Assert.IsType<SiloLifecycleEvents.ObserverStopping>(item),
            item => Assert.IsType<SiloLifecycleEvents.ObserverStopped>(item));
        Assert.All(observerEvents, item => Assert.Equal(ServiceLifecycleStage.BecomeActive, item.Stage));
        Assert.All(
            startEvents.Concat(stopEvents),
            item => Assert.Equal(
                nameof(ClusterOwnershipLeaseMonitor),
                ((dynamic)item.Payload!).ObserverName));
        Assert.True(harness.Timer.IsDisposed);
        Assert.Equal(1, harness.Timer.DisposeCount);
    }

    private static TestActivationContext ContextWithEntry(string key, long version, long epoch, long fence)
    {
        var context = new TestActivationContext(
            GrainId.Create(TestHarness.DirectoryGrainType, key),
            ActivationId.NewId());
        context.SetComponent(Entry(context.GrainId, key, version, epoch, fence, Start.AddMinutes(1)));
        return context;
    }

    private static ClusterDirectoryEntry Entry(
        GrainId grainId,
        string clusterId,
        long version,
        long epoch,
        long fence,
        DateTimeOffset lease)
        => new(grainId, clusterId, version, epoch, fence, lease);

    private static T RunInContext<T>(IGrainContext context, Func<T> action)
    {
        RuntimeContext.SetExecutionContext(context, out var original);
        try
        {
            return action();
        }
        finally
        {
            RuntimeContext.ResetExecutionContext(original);
        }
    }

    private static async Task ActivateThroughRuntimeBoundaryAsync(
        TestActivationContext context,
        Action invocation,
        CancellationToken cancellationToken)
    {
        try
        {
            await context.Lifecycle.OnStart(cancellationToken);
            invocation();
        }
        catch (Exception exception)
        {
            context.Deactivate(
                new DeactivationReason(
                    DeactivationReasonCode.ActivationFailed,
                    exception,
                    "Failed to activate grain."),
                CancellationToken.None);
            throw;
        }
    }

    private static async ValueTask<ClusterDirectoryEntry> AwaitCancellationAsync(
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<ClusterDirectoryEntry>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(
            static state =>
            {
                var (source, token) = ((TaskCompletionSource<ClusterDirectoryEntry>, CancellationToken))state!;
                source.TrySetCanceled(token);
            },
            (completion, cancellationToken));
        return await completion.Task;
    }

    private sealed class TestHarness : IAsyncDisposable
    {
        internal const string LocatorName = "phase7-directory";
        internal const string LocalCluster = "east";
        internal static readonly GrainType DirectoryGrainType = GrainType.Create("phase7.directory");
        internal static readonly GrainType LocalGrainType = GrainType.Create("phase7.local");
        private readonly List<ServiceProvider> _serviceProviders = [];
        private readonly ServiceProvider _locatorServices;
        private bool _started;
        private bool _stopped;

        public TestHarness(TimeSpan? renewalWindow = null, bool enabled = true)
        {
            Enabled = enabled;
            RenewalWindow = renewalWindow ?? TimeSpan.FromSeconds(4);
            TimeProvider = new FakeTimeProvider(Start);
            Timer = new ControlledAsyncTimer();
            Validator = new ControlledOwnershipValidator();
            TimerFactory = new ControlledAsyncTimerFactory(Timer);

            var properties = ImmutableDictionary<string, string>.Empty
                .WithComparers(StringComparer.Ordinal, StringComparer.Ordinal)
                .Add(WellKnownGrainTypeProperties.ClusterLocator, LocatorName);
            LocalManifest = new GrainManifest(
                ImmutableDictionary<GrainType, GrainProperties>.Empty
                    .Add(DirectoryGrainType, new GrainProperties(properties))
                    .Add(
                        LocalGrainType,
                        new GrainProperties(
                            ImmutableDictionary<string, string>.Empty
                                .WithComparers(StringComparer.Ordinal, StringComparer.Ordinal))),
                ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty);
            ManifestProvider = new FixedManifestProvider(
                new ClusterManifest(
                    MajorMinorVersion.Zero,
                    ImmutableDictionary<SiloAddress, GrainManifest>.Empty,
                    [LocalManifest]),
                LocalManifest);
            PropertiesResolver = new GrainPropertiesResolver(ManifestProvider);

            var locatorServices = new ServiceCollection();
            locatorServices.AddKeyedSingleton<IClusterLocator>(LocatorName, Validator);
            _locatorServices = locatorServices.BuildServiceProvider();
            LocatorResolver = new ClusterLocatorResolver(PropertiesResolver, _locatorServices);
            BindingResolver = new UniversalReferenceBindingResolver(
                Options.Create(new ClusterOptions { ServiceId = "phase7-service", ClusterId = LocalCluster }),
                Options.Create(new MetaclusterOptions { Enabled = Enabled }),
                PropertiesResolver);
            Monitor = new ClusterOwnershipLeaseMonitor(
                LocatorResolver,
                BindingResolver,
                TimerFactory,
                Options.Create(
                    new MetaclusterOptions
                    {
                        Enabled = Enabled,
                        ClusterOwnershipLeaseRenewalWindow = RenewalWindow,
                    }),
                NullLogger<ClusterOwnershipLeaseMonitor>.Instance,
                TimeProvider);
            Lifecycle = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);
            ((ILifecycleParticipant<ISiloLifecycle>)Monitor).Participate(Lifecycle);
        }

        public TimeSpan RenewalWindow { get; }

        public bool Enabled { get; }

        public TimeSpan Period => TimeSpan.FromTicks(Math.Max(1, RenewalWindow.Ticks / 2));

        public FakeTimeProvider TimeProvider { get; }

        public ControlledAsyncTimer Timer { get; }

        public ControlledAsyncTimerFactory TimerFactory { get; }

        public ControlledOwnershipValidator Validator { get; }

        public FixedManifestProvider ManifestProvider { get; }

        public GrainManifest LocalManifest { get; }

        public GrainPropertiesResolver PropertiesResolver { get; }

        public ClusterLocatorResolver LocatorResolver { get; }

        public UniversalReferenceBindingResolver BindingResolver { get; }

        public ClusterOwnershipLeaseMonitor Monitor { get; }

        public SiloLifecycleSubject Lifecycle { get; }

        public TestActivationContext CreateContext(string key, GrainType? grainType = null)
            => new(
                GrainId.Create(grainType ?? DirectoryGrainType, key),
                ActivationId.NewId());

        public ClusterDirectoryEntry CreateEntry(
            GrainId grainId,
            long version,
            long epoch,
            long fence,
            DateTimeOffset? lease = null)
            => CreateEntry(grainId, LocalCluster, version, epoch, fence, lease);

        public ClusterDirectoryEntry CreateEntry(
            GrainId grainId,
            string clusterId,
            long version,
            long epoch,
            long fence,
            DateTimeOffset? lease = null)
            => Entry(
                grainId,
                clusterId,
                version,
                epoch,
                fence,
                lease ?? TimeProvider.GetUtcNow().AddSeconds(10));

        public async Task TrackAndActivateAsync(TestActivationContext context)
        {
            Monitor.Track(context);
            await context.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        }

        public async Task StartMonitorAsync()
        {
            Assert.False(_started);
            _started = true;
            await Lifecycle.OnStart(TestContext.Current.CancellationToken);
        }

        public async Task StopMonitorAsync()
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            await Lifecycle.OnStop(TestContext.Current.CancellationToken);
        }

        public async Task AdvanceAndTickAsync(int expectedSchedule)
        {
            await Timer.WaitForScheduleAsync(expectedSchedule);
            TimeProvider.Advance(Period);
            Timer.ReleaseNext();
        }

        public GrainTypeSharedContext CreateSharedContext(GrainType grainType)
        {
            var services = new ServiceCollection();
            services.AddMetrics();
            services.AddSingleton<OrleansInstruments>();
            services.AddSingleton<CatalogInstruments>();
            services.AddSingleton<GrainInstruments>();
            services.AddSingleton<MessagingProcessingInstruments>();
            services.AddSingleton<PlacementStrategy>(DirectorylessPlacementStrategy.Instance);
            services.AddSingleton(Monitor);
            services.AddSingleton(Options.Create(new MetaclusterOptions { Enabled = Enabled }));
            services.AddSingleton(
                serviceProvider => new GrainDirectoryResolver(
                    serviceProvider,
                    PropertiesResolver,
                    []));
            var serviceProvider = services.BuildServiceProvider();
            _serviceProviders.Add(serviceProvider);
            var placementResolver = new PlacementStrategyResolver(
                serviceProvider,
                [],
                PropertiesResolver);
            var classMap = new GrainClassMap(
                null!,
                ImmutableDictionary<GrainType, Type>.Empty
                    .Add(DirectoryGrainType, typeof(TestGrainClass))
                    .Add(LocalGrainType, typeof(TestGrainClass)));

            return new GrainTypeSharedContext(
                grainType,
                ManifestProvider,
                classMap,
                placementResolver,
                Options.Create(new SiloMessagingOptions()),
                Options.Create(new GrainCollectionOptions()),
                Options.Create(new SchedulingOptions()),
                null!,
                NullLoggerFactory.Instance,
                null!,
                serviceProvider,
                null!,
                LocatorResolver);
        }

        public async ValueTask DisposeAsync()
        {
            if (_started && !_stopped)
            {
                await StopMonitorAsync();
            }
            else if (!_started)
            {
                Timer.Dispose();
            }

            foreach (var serviceProvider in _serviceProviders)
            {
                serviceProvider.Dispose();
            }

            _locatorServices.Dispose();
        }
    }

    private sealed class ControlledOwnershipValidator : IClusterLocator, IClusterOwnershipValidator
    {
        private readonly ConcurrentQueue<
            Func<GrainId, CancellationToken, ValueTask<ClusterDirectoryEntry>>> _responses = new();
        private readonly ConcurrentQueue<CancellationToken> _cancellationTokens = new();
        private readonly CountBarrier _calls = new();

        public Func<GrainId, CancellationToken, ValueTask<ClusterDirectoryEntry>>? Handler { get; set; }

        public int CallCount => _calls.Count;

        public IReadOnlyCollection<CancellationToken> CancellationTokens => _cancellationTokens.ToArray();

        public void Enqueue(ClusterDirectoryEntry entry)
            => Enqueue((_, _) => new ValueTask<ClusterDirectoryEntry>(entry));

        public void Enqueue(Exception exception)
            => Enqueue((_, _) => ValueTask.FromException<ClusterDirectoryEntry>(exception));

        public void Enqueue(
            Func<GrainId, CancellationToken, ValueTask<ClusterDirectoryEntry>> response)
            => _responses.Enqueue(response);

        public ValueTask<ClusterDirectoryEntry> ValidateLocalOwnership(
            GrainId grainId,
            string localClusterId,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(TestHarness.LocalCluster, localClusterId);
            _cancellationTokens.Enqueue(cancellationToken);
            _calls.Signal();
            if (_responses.TryDequeue(out var response))
            {
                return response(grainId, cancellationToken);
            }

            return Handler is not null
                ? Handler(grainId, cancellationToken)
                : ValueTask.FromException<ClusterDirectoryEntry>(
                    new InvalidOperationException($"No ownership response was configured for {grainId}."));
        }

        public ValueTask<ClusterLocation> Locate(
            GrainId grainId,
            ClusterLocationContext context,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task WaitForCallCountAsync(int expected) => _calls.WaitAsync(expected);
    }

    private sealed class ControlledAsyncTimerFactory(ControlledAsyncTimer timer) : IAsyncTimerFactory
    {
        public IAsyncTimer Create(TimeSpan period, string name, TimeProvider timeProvider)
        {
            timer.Initialize(period, name, timeProvider);
            return timer;
        }
    }

    private sealed class ControlledAsyncTimer : IAsyncTimer
    {
        private readonly object _lock = new();
        private readonly Queue<PendingTick> _pending = new();
        private readonly CountBarrier _schedules = new();
        private bool _disposed;
        private int _disposeCount;

        public TimeSpan Period { get; private set; }

        public string Name { get; private set; } = string.Empty;

        public TimeProvider TimeProvider { get; private set; } = null!;

        public int ScheduleCount => _schedules.Count;

        public bool IsDisposed
        {
            get
            {
                lock (_lock)
                {
                    return _disposed;
                }
            }
        }

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public void Initialize(TimeSpan period, string name, TimeProvider timeProvider)
        {
            Period = period;
            Name = name;
            TimeProvider = timeProvider;
        }

        public Task<bool> NextTick(TimeSpan? overrideDelay = null)
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return Task.FromResult(false);
                }

                var delay = overrideDelay ?? Period;
                var pending = new PendingTick(
                    TimeProvider.GetUtcNow().Add(delay),
                    new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
                _pending.Enqueue(pending);
                _schedules.Signal();
                return pending.Completion.Task;
            }
        }

        public void ReleaseNext()
        {
            PendingTick pending;
            lock (_lock)
            {
                Assert.False(_disposed);
                Assert.NotEmpty(_pending);
                pending = _pending.Dequeue();
            }

            Assert.True(
                TimeProvider.GetUtcNow() >= pending.Due,
                $"Timer '{Name}' was released before due time {pending.Due:O}; actual {TimeProvider.GetUtcNow():O}.");
            pending.Completion.SetResult(true);
        }

        public Task WaitForScheduleAsync(int expected) => _schedules.WaitAsync(expected);

        public bool CheckHealth(DateTime lastCheckTime, [NotNullWhen(false)] out string? reason)
        {
            reason = null;
            return true;
        }

        public void Dispose()
        {
            List<PendingTick> pending;
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                Interlocked.Increment(ref _disposeCount);
                pending = [.. _pending];
                _pending.Clear();
            }

            foreach (var item in pending)
            {
                item.Completion.TrySetResult(false);
            }
        }

        private readonly record struct PendingTick(
            DateTimeOffset Due,
            TaskCompletionSource<bool> Completion);
    }

    private sealed class TestActivationContext : IGrainContext
    {
        private readonly ConcurrentDictionary<Type, object> _components = new();
        private readonly ConcurrentQueue<DeactivationReason> _deactivationReasons = new();
        private readonly CountBarrier _ownershipSets = new();
        private readonly CountBarrier _deactivations = new();
        private readonly TaskCompletionSource _deactivated = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TestActivationContext(GrainId grainId, ActivationId activationId)
        {
            GrainId = grainId;
            ActivationId = activationId;
            Lifecycle = new GrainLifecycle(NullLogger<GrainLifecycle>.Instance);
        }

        public GrainLifecycle Lifecycle { get; }

        public ClusterDirectoryEntry? Ownership
            => GetComponent(typeof(ClusterDirectoryEntry)) as ClusterDirectoryEntry;

        public int OwnershipSetCount => _ownershipSets.Count;

        public int DeactivationCount => _deactivations.Count;

        public IReadOnlyCollection<DeactivationReason> DeactivationReasons
            => _deactivationReasons.ToArray();

        public GrainReference GrainReference => null!;

        public GrainId GrainId { get; }

        public object? GrainInstance => null;

        public ActivationId ActivationId { get; }

        public GrainAddress Address => default!;

        public IServiceProvider ActivationServices => null!;

        IGrainLifecycle IGrainContext.ObservableLifecycle => Lifecycle;

        public IWorkItemScheduler Scheduler => null!;

        public Task Deactivated => _deactivated.Task;

        public object? GetTarget() => GrainInstance;

        public object? GetComponent(Type componentType)
            => _components.TryGetValue(componentType, out var value) ? value : null;

        public void SetComponent<TComponent>(TComponent? value)
            where TComponent : class
        {
            if (value is null)
            {
                _components.TryRemove(typeof(TComponent), out _);
                return;
            }

            _components[typeof(TComponent)] = value;
            if (value is ClusterDirectoryEntry)
            {
                _ownershipSets.Signal();
            }
        }

        public void ReceiveMessage(object message) => throw new NotSupportedException();

        public void Activate(
            Dictionary<string, object>? requestContext,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void Deactivate(
            DeactivationReason deactivationReason,
            CancellationToken cancellationToken = default)
        {
            _deactivationReasons.Enqueue(deactivationReason);
            _deactivations.Signal();
            _deactivated.TrySetResult();
        }

        public void Rehydrate(IRehydrationContext context) => throw new NotSupportedException();

        public void Migrate(
            Dictionary<string, object>? requestContext,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public bool Equals(IGrainContext? other) => ReferenceEquals(this, other);

        public Task WaitForOwnershipSetCountAsync(int expected) => _ownershipSets.WaitAsync(expected);

        public Task WaitForDeactivationCountAsync(int expected) => _deactivations.WaitAsync(expected);
    }

    private sealed class CountBarrier
    {
        private readonly object _lock = new();
        private readonly List<(int Expected, TaskCompletionSource Completion)> _waiters = [];
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Signal()
        {
            List<TaskCompletionSource> completed = [];
            lock (_lock)
            {
                var count = ++_count;
                for (var index = _waiters.Count - 1; index >= 0; --index)
                {
                    var waiter = _waiters[index];
                    if (count >= waiter.Expected)
                    {
                        completed.Add(waiter.Completion);
                        _waiters.RemoveAt(index);
                    }
                }
            }

            foreach (var completion in completed)
            {
                completion.TrySetResult();
            }
        }

        public Task WaitAsync(int expected)
        {
            lock (_lock)
            {
                if (_count >= expected)
                {
                    return Task.CompletedTask;
                }

                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((expected, completion));
                return completion.Task;
            }
        }
    }

    private sealed class FixedManifestProvider(
        ClusterManifest current,
        GrainManifest localGrainManifest) : IClusterManifestProvider
    {
        public ClusterManifest Current { get; } = current;

        public GrainManifest LocalGrainManifest { get; } = localGrainManifest;

        public IAsyncEnumerable<ClusterManifest> Updates => GetUpdates();

        private static async IAsyncEnumerable<ClusterManifest> GetUpdates()
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class DirectorylessPlacementStrategy : PlacementStrategy
    {
        public static DirectorylessPlacementStrategy Instance { get; } = new();

        public override bool IsUsingGrainDirectory => false;
    }

    private sealed class TestGrainClass;

    private sealed class OwnershipLostException(string message) : Exception(message);

    [Fact]
    [Trait("Phase", "3")]
    public async Task Monitor_SubSecondRenewal_RunsAtHalfWindowAndExtendsLeaseBeforeExpiry()
    {
        await using var harness = new TestHarness(TimeSpan.FromMilliseconds(250));
        var context = harness.CreateContext("subsecond-renewal");
        var initial = harness.CreateEntry(
            context.GrainId,
            version: 1,
            epoch: 2,
            fence: 3,
            lease: Start.AddMilliseconds(250));
        var renewed = harness.CreateEntry(
            context.GrainId,
            version: 1,
            epoch: 2,
            fence: 3,
            lease: Start.AddSeconds(1));
        harness.Validator.Enqueue(initial);
        harness.Validator.Enqueue(renewed);

        await harness.TrackAndActivateAsync(context);
        await harness.StartMonitorAsync();
        await harness.AdvanceAndTickAsync(1);
        await context.WaitForOwnershipSetCountAsync(2);

        Assert.Equal(TimeSpan.FromMilliseconds(125), harness.Period);
        Assert.Equal(Start.AddMilliseconds(125), harness.TimeProvider.GetUtcNow());
        Assert.True(harness.TimeProvider.GetUtcNow() < initial.LeaseExpiration);
        Assert.Same(renewed, context.Ownership);
        Assert.Equal(initial.Version, renewed.Version);
        Assert.Equal(initial.FencingToken, renewed.FencingToken);
        Assert.Equal(2, harness.Validator.CallCount);
        Assert.Equal(0, context.DeactivationCount);
    }

    [Fact]
    [Trait("Phase", "3")]
    public async Task Monitor_HungRenewal_DoesNotStarveHealthyActivationAcrossSuccessiveSweeps()
    {
        await using var harness = new TestHarness();
        var hung = harness.CreateContext("hung-across-sweeps");
        var healthy = harness.CreateContext("healthy-across-sweeps");
        harness.Validator.Enqueue(harness.CreateEntry(hung.GrainId, version: 1, epoch: 1, fence: 1));
        harness.Validator.Enqueue(harness.CreateEntry(healthy.GrainId, version: 1, epoch: 1, fence: 1));
        await harness.TrackAndActivateAsync(hung);
        await harness.TrackAndActivateAsync(healthy);
        var hungRenewal = new TaskCompletionSource<ClusterDirectoryEntry>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new ConcurrentDictionary<GrainId, int>();
        harness.Validator.Handler = (grainId, _) =>
        {
            var call = calls.AddOrUpdate(grainId, 1, static (_, count) => count + 1);
            if (grainId == hung.GrainId)
            {
                return new ValueTask<ClusterDirectoryEntry>(hungRenewal.Task);
            }

            return new ValueTask<ClusterDirectoryEntry>(
                harness.CreateEntry(grainId, version: call + 1, epoch: 1, fence: 1));
        };

        await harness.StartMonitorAsync();
        await harness.AdvanceAndTickAsync(1);
        await healthy.WaitForOwnershipSetCountAsync(2);
        await harness.Timer.WaitForScheduleAsync(2);
        await harness.AdvanceAndTickAsync(2);
        await healthy.WaitForOwnershipSetCountAsync(3);
        await harness.Timer.WaitForScheduleAsync(3);

        Assert.Equal(1, calls[hung.GrainId]);
        Assert.Equal(2, calls[healthy.GrainId]);
        Assert.Equal(3, healthy.OwnershipSetCount);
        Assert.Equal(3, healthy.Ownership!.Version);
        Assert.Equal(1, hung.OwnershipSetCount);
        Assert.Equal(0, hung.DeactivationCount);
        Assert.Equal(0, healthy.DeactivationCount);

        hungRenewal.SetResult(
            harness.CreateEntry(hung.GrainId, version: 2, epoch: 1, fence: 1));
        await hung.WaitForOwnershipSetCountAsync(2);
    }

    [Fact]
    [Trait("Phase", "3")]
    public async Task Monitor_Stop_AwaitsEveryInFlightRenewalBeforeCompleting()
    {
        await using var harness = new TestHarness();
        var first = harness.CreateContext("stop-first");
        var second = harness.CreateContext("stop-second");
        harness.Validator.Enqueue(harness.CreateEntry(first.GrainId, version: 1, epoch: 1, fence: 1));
        harness.Validator.Enqueue(harness.CreateEntry(second.GrainId, version: 1, epoch: 1, fence: 1));
        await harness.TrackAndActivateAsync(first);
        await harness.TrackAndActivateAsync(second);
        var firstRenewal = new TaskCompletionSource<ClusterDirectoryEntry>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRenewal = new TaskCompletionSource<ClusterDirectoryEntry>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Validator.Handler = (grainId, _) => new ValueTask<ClusterDirectoryEntry>(
            grainId == first.GrainId ? firstRenewal.Task : secondRenewal.Task);

        await harness.StartMonitorAsync();
        await harness.AdvanceAndTickAsync(1);
        await harness.Validator.WaitForCallCountAsync(4);
        var stop = harness.StopMonitorAsync();
        Assert.True(harness.Timer.IsDisposed);
        Assert.False(stop.IsCompleted);

        firstRenewal.SetResult(
            harness.CreateEntry(first.GrainId, version: 2, epoch: 1, fence: 1));
        await first.WaitForOwnershipSetCountAsync(2);
        Assert.False(stop.IsCompleted);

        secondRenewal.SetResult(
            harness.CreateEntry(second.GrainId, version: 2, epoch: 1, fence: 1));
        await stop;

        Assert.True(stop.IsCompletedSuccessfully);
        Assert.Equal(2, first.OwnershipSetCount);
        Assert.Equal(2, second.OwnershipSetCount);
        Assert.Equal(1, harness.Timer.DisposeCount);
    }

    [Fact]
    [Trait("Phase", "3")]
    public async Task PausedPastLease_OwnershipRemainsVisibleUntilRenewalDetectsLoss()
    {
        await using var harness = new TestHarness(TimeSpan.FromMilliseconds(250));
        var context = harness.CreateContext("paused-owner");
        var expired = harness.CreateEntry(
            context.GrainId,
            version: 7,
            epoch: 8,
            fence: 9,
            lease: Start.AddMilliseconds(100));
        harness.Validator.Enqueue(expired);
        harness.Validator.Enqueue(new OwnershipLostException("lease elapsed while process was paused"));

        await harness.TrackAndActivateAsync(context);
        await harness.StartMonitorAsync();
        await harness.Timer.WaitForScheduleAsync(1);
        harness.TimeProvider.Advance(TimeSpan.FromMilliseconds(125));
        var exposedBeforeMonitorRuns = RunInContext(context, () => new ClusterOwnershipAccessor().Current);

        Assert.Same(expired, exposedBeforeMonitorRuns);
        Assert.True(exposedBeforeMonitorRuns!.LeaseExpiration <= harness.TimeProvider.GetUtcNow());
        Assert.Equal(0, context.DeactivationCount);

        harness.Timer.ReleaseNext();
        await context.WaitForDeactivationCountAsync(1);

        Assert.Equal(1, context.DeactivationCount);
        Assert.Same(expired, context.Ownership);
        Assert.IsType<OwnershipLostException>(context.DeactivationReasons.Single().Exception);
        Assert.Equal(2, harness.Validator.CallCount);
    }
}
