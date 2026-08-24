using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Concurrency;
using Orleans.Configuration;
using Orleans.DurableJobs;
using Orleans.Runtime;
using Xunit;

namespace Tester.DurableJobs;

[TestCategory("BVT"), TestCategory("DurableJobs")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableJobs")]
public class DurableJobFeatureHandlerTests
{
    [Fact]
    public void Registry_SelectsHandlerUsingHandlerOwnedMatching()
    {
        var registry = new DurableJobHandlerRegistry();
        var lower = new TestHandler(static jobName => jobName.StartsWith("feature.", StringComparison.Ordinal));
        var upper = new TestHandler(static jobName => jobName.StartsWith("Feature.", StringComparison.Ordinal));

        registry.Register(lower);
        registry.Register(upper);

        Assert.True(registry.TryGetHandler("feature.cleanup", out var resolvedLower));
        Assert.True(registry.TryGetHandler("Feature.cleanup", out var resolvedUpper));
        Assert.False(registry.TryGetHandler("other.cleanup", out var unresolved));
        Assert.Same(lower, resolvedLower);
        Assert.Same(upper, resolvedUpper);
        Assert.Null(unresolved);
    }

    [Fact]
    public void Registry_RejectsDuplicateHandlerRegistration()
    {
        var registry = new DurableJobHandlerRegistry();
        var handler = new TestHandler(static _ => true);

        registry.Register(handler);

        Assert.Throws<InvalidOperationException>(() => registry.Register(handler));
    }

    [Fact]
    public void Registry_RejectsAmbiguousMatchesAcrossIsolationModes()
    {
        var registry = new DurableJobHandlerRegistry();
        registry.Register(new TestHandler(static jobName => jobName.StartsWith("feature.", StringComparison.Ordinal)));
        registry.Register(
            new TestHandler(static jobName => jobName.EndsWith(".cleanup", StringComparison.Ordinal)),
            requiresTurnIsolation: true);

        var exception = Assert.Throws<InvalidOperationException>(
            () => registry.TryGetIsolatedHandler("feature.cleanup", out _));

        Assert.Contains("Multiple durable job feature handlers match job 'feature.cleanup'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FeatureReceiverMethod_IsNotAlwaysInterleavable()
    {
        var method = typeof(IDurableJobFeatureReceiverExtension).GetMethod(
            nameof(IDurableJobFeatureReceiverExtension.TryHandleFeatureJobAsync));

        Assert.NotNull(method);
        Assert.Null(method.GetCustomAttributes(typeof(AlwaysInterleaveAttribute), inherit: false).SingleOrDefault());
    }

    [Fact]
    public async Task FeatureReceiver_ExecutesRegisteredIsolatedHandlerToCompletion()
    {
        var registry = new DurableJobHandlerRegistry();
        var handler = new TestHandler(static jobName => jobName == "feature");
        registry.Register(handler, requiresTurnIsolation: true);
        var extension = new DurableJobFeatureReceiverExtension(registry, CreateShared());

        var result = await extension.TryHandleFeatureJobAsync(
            new TestJobRunContext("feature"),
            CancellationToken.None);

        Assert.Same(DurableJobRunResult.Completed, result);
        Assert.Equal(1, handler.ExecutionCount);
    }

    [Fact]
    public async Task FeatureReceiver_ReturnsNullForNonIsolatedHandler()
    {
        var registry = new DurableJobHandlerRegistry();
        registry.Register(new TestHandler(static jobName => jobName == "feature"));
        var extension = new DurableJobFeatureReceiverExtension(registry, CreateShared());

        var result = await extension.TryHandleFeatureJobAsync(
            new TestJobRunContext("feature"),
            CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task FeatureReceiver_ReentersTurnIsolatedHandlerUntilTerminal()
    {
        var registry = new DurableJobHandlerRegistry();
        var calls = 0;
        registry.Register(
            new TestHandler(
                static jobName => jobName == "feature",
                () => ++calls < 3
                ? DurableJobRunResult.InProgress(TimeSpan.FromMilliseconds(1))
                : DurableJobRunResult.Completed),
            requiresTurnIsolation: true);
        var extension = new DurableJobFeatureReceiverExtension(registry, CreateShared());
        var context = new TestJobRunContext("feature");

        var first = await extension.TryHandleFeatureJobAsync(context, CancellationToken.None);
        var second = await extension.TryHandleFeatureJobAsync(context, CancellationToken.None);
        var result = await extension.TryHandleFeatureJobAsync(context, CancellationToken.None);
        var terminalPoll = await extension.TryHandleFeatureJobAsync(context, CancellationToken.None);

        Assert.True(first!.IsInProgress);
        Assert.True(second!.IsInProgress);
        Assert.Same(DurableJobRunResult.Completed, result);
        Assert.Same(result, terminalPoll);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task TurnIsolation_NestedCallChainRetainsLeaseUntilDurableWorkCompletes()
    {
        var isolation = new DurableJobTurnIsolation();
        isolation.Enable();
        var ordinary = await isolation.EnterOrdinaryAsync();
        ordinary.Activate();
        var durableWork = await isolation.EnterIsolatedAsync(CancellationToken.None);
        durableWork.Activate();
        ordinary.Dispose();

        var concurrent = isolation.EnterOrdinaryAsync();
        Assert.False(concurrent.IsCompleted);

        durableWork.Dispose();
        using var concurrentLease = await concurrent.AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task TurnIsolation_CanceledWaitDoesNotPoisonGate()
    {
        var isolation = new DurableJobTurnIsolation();
        isolation.Enable();
        var ordinary = await isolation.EnterOrdinaryAsync();
        ordinary.Activate();
        RequestContext.Remove(DurableJobTurnIsolation.RequestContextKey);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => isolation.EnterIsolatedAsync(cancellation.Token).AsTask());

        ordinary.Dispose();
        using var recovered = await isolation.EnterIsolatedAsync(CancellationToken.None);
        recovered.Activate();
    }

    [Fact]
    public async Task FeatureReceiver_CanceledPollStopsGateWaitWithoutStartingExecution()
    {
        var isolation = new DurableJobTurnIsolation();
        isolation.Enable();
        var registry = new DurableJobHandlerRegistry(isolation);
        var calls = 0;
        registry.Register(
            new TestHandler(
                static jobName => jobName == "feature",
                () =>
            {
                calls++;
                return DurableJobRunResult.Completed;
            }),
            requiresTurnIsolation: true);
        var extension = new DurableJobFeatureReceiverExtension(registry, CreateShared(), isolation);
        using var active = await isolation.EnterOrdinaryAsync();
        active.Activate();
        RequestContext.Remove(DurableJobTurnIsolation.RequestContextKey);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => extension.TryHandleFeatureJobAsync(new TestJobRunContext("feature"), cancellation.Token).AsTask());

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task TurnIsolation_StaleOwnerFromPriorTurnCannotEnterCurrentTurn()
    {
        var isolation = new DurableJobTurnIsolation();
        isolation.Enable();
        object priorOwners;
        using (var prior = await isolation.EnterOrdinaryAsync())
        {
            prior.Activate();
            priorOwners = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(
                RequestContext.Get(DurableJobTurnIsolation.RequestContextKey));
        }

        using var current = await isolation.EnterOrdinaryAsync();
        current.Activate();
        var currentOwners = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(
            RequestContext.Get(DurableJobTurnIsolation.RequestContextKey));
        Assert.NotEqual(
            Assert.Single((IReadOnlyDictionary<string, string>)priorOwners).Value,
            Assert.Single(currentOwners).Value);
        RequestContext.Set(DurableJobTurnIsolation.RequestContextKey, priorOwners);

        var staleEntry = isolation.EnterOrdinaryAsync();
        Assert.False(staleEntry.IsCompleted);

        RequestContext.Set(DurableJobTurnIsolation.RequestContextKey, currentOwners);
        current.Dispose();
        using var admitted = await staleEntry.AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        admitted.Activate();
    }

    [Fact]
    public async Task TurnIsolation_NestedActivationCallChainRetainsEveryOwner()
    {
        var first = new DurableJobTurnIsolation();
        var second = new DurableJobTurnIsolation();
        first.Enable();
        second.Enable();

        using var firstLease = await first.EnterOrdinaryAsync();
        firstLease.Activate();
        using var secondLease = await second.EnterOrdinaryAsync();
        secondLease.Activate();

        var reentrantFirst = first.EnterOrdinaryAsync();
        Assert.True(reentrantFirst.IsCompleted);
        using var nested = await reentrantFirst;
        Assert.True(nested.IsReentrant);
    }

    [Fact]
    public async Task ExecutionLifetime_OnStopDrainsNonCooperativeHandlerAndStopsAdmission()
    {
        var lifetime = new DurableJobExecutionLifetime();
        var release = new TaskCompletionSource<DurableJobRunResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var execution = lifetime.Start(_ => release.Task);

        var stop = lifetime.OnStop();
        await Task.Yield();
        Assert.False(stop.IsCompleted);
        Assert.Throws<OperationCanceledException>(
            () => { _ = lifetime.Start(_ => Task.FromResult(DurableJobRunResult.Completed)); });

        release.SetResult(DurableJobRunResult.Completed);
        Assert.Same(DurableJobRunResult.Completed, await execution);
        await stop.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData(DurableJobRunStatus.Completed)]
    [InlineData(DurableJobRunStatus.InProgress)]
    [InlineData(DurableJobRunStatus.Failed)]
    [InlineData(DurableJobRunStatus.RescheduleRequested)]
    public void StatusFlags_AreMutuallyExclusive_AcrossAllStatuses(DurableJobRunStatus status)
    {
        var result = status switch
        {
            DurableJobRunStatus.Completed => DurableJobRunResult.Completed,
            DurableJobRunStatus.InProgress => DurableJobRunResult.InProgress(TimeSpan.FromSeconds(30)),
            DurableJobRunStatus.Failed => DurableJobRunResult.Failed(new InvalidOperationException("boom")),
            DurableJobRunStatus.RescheduleRequested => DurableJobRunResult.RescheduleAt(DateTimeOffset.UtcNow.AddMinutes(5)),
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unexpected status."),
        };

        Assert.Equal(status, result.Status);

        var trueFlagCount = (result.IsFailed ? 1 : 0) + (result.IsInProgress ? 1 : 0) + (result.IsRescheduleRequested ? 1 : 0);

        switch (status)
        {
            case DurableJobRunStatus.Completed:
                Assert.Equal(0, trueFlagCount);
                Assert.False(result.IsFailed);
                Assert.False(result.IsInProgress);
                Assert.False(result.IsRescheduleRequested);
                break;
            case DurableJobRunStatus.InProgress:
                Assert.Equal(1, trueFlagCount);
                Assert.True(result.IsInProgress);
                Assert.False(result.IsFailed);
                Assert.False(result.IsRescheduleRequested);
                break;
            case DurableJobRunStatus.Failed:
                Assert.Equal(1, trueFlagCount);
                Assert.True(result.IsFailed);
                Assert.False(result.IsInProgress);
                Assert.False(result.IsRescheduleRequested);
                break;
            case DurableJobRunStatus.RescheduleRequested:
                Assert.Equal(1, trueFlagCount);
                Assert.True(result.IsRescheduleRequested);
                Assert.False(result.IsFailed);
                Assert.False(result.IsInProgress);
                break;
        }
    }

    private static DurableJobReceiverExtensionShared CreateShared() =>
        new(
            NullLogger<DurableJobReceiverExtension>.Instance,
            Options.Create(new DurableJobsOptions()),
            Options.Create(new SiloMessagingOptions()),
            TimeProvider.System);

    private sealed class TestHandler(
        Func<string, bool> canHandle,
        Func<DurableJobRunResult>? resultFactory = null) : IDurableJobFeatureHandler
    {
        public int ExecutionCount { get; private set; }

        public bool CanHandle(string jobName) => canHandle(jobName);

        public ValueTask<DurableJobRunResult> ExecuteJobAsync(IJobRunContext context, CancellationToken cancellationToken)
        {
            ExecutionCount++;
            return ValueTask.FromResult(resultFactory?.Invoke() ?? DurableJobRunResult.Completed);
        }
    }

    private sealed class TestJobRunContext(string jobName) : IJobRunContext
    {
        public DurableJob Job { get; } = new()
        {
            Id = Guid.NewGuid().ToString(),
            Name = jobName,
            DueTime = DateTimeOffset.UtcNow,
            TargetGrainId = GrainId.Create("test", Guid.NewGuid().ToString()),
            ShardId = "test"
        };

        public string RunId { get; } = Guid.NewGuid().ToString();

        public int DequeueCount => 0;
    }
}
