using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Reminders.TestKit;
using Orleans.Runtime;
using Orleans.Testing.Reminders;
using Orleans.TestingHost;
using Xunit;

namespace Orleans.Reminders.TestKit.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ReminderServiceLifecycleCollection
{
    public const string Name = "Reminder service lifecycle conformance";
}

public sealed class ReminderServiceLifecycleFixture : IAsyncLifetime
{
    private static readonly TimeSpan LoadingWindow = TimeSpan.FromSeconds(5);
    private InProcessTestCluster? _cluster;
    private ReminderTestClock? _clock;
    private readonly short _initialSilos = 1;

    public ReminderServiceLifecycleFixture()
    {
    }

    internal ReminderServiceLifecycleFixture(ReminderTestClock clock)
    {
        _clock = clock;
    }

    internal ReminderServiceLifecycleFixture(short initialSilos)
    {
        _initialSilos = initialSilos;
    }

    public ReminderServiceLifecycleHarness Harness { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        try
        {
            var builder = new InProcessTestClusterBuilder(_initialSilos);
            builder.ConfigureSilo((_, siloBuilder) =>
                siloBuilder.Configure<ConsistentRingOptions>(options => options.UseVirtualBucketsConsistentRing = false));
            builder.UseIdealizedReminderTable(
                configureReminderOptions: options => options.ReminderLoadingWindow = LoadingWindow);
            _clock = ReminderTestClock.Attach(
                builder,
                minimumReminderPeriod: TimeSpan.FromSeconds(1),
                refreshReminderListPeriod: TimeSpan.FromSeconds(1));
            _cluster = builder.Build();
            await _cluster.DeployAsync(TestContext.Current.CancellationToken);
            Harness = new ReminderServiceLifecycleHarness(
                _cluster,
                _clock,
                _clock.DiagnosticObserver,
                LoadingWindow);
        }
        catch (Exception initializationException)
        {
            try
            {
                await DisposeAsync();
            }
            catch (Exception cleanupException)
            {
                initializationException.Data["ReminderServiceLifecycleFixture.CleanupException"] = cleanupException;
            }

            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        var cluster = _cluster;
        var clock = _clock;
        _cluster = null;
        _clock = null;

        try
        {
            if (cluster is not null)
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(1));
                await cluster.StopAllSilosAsync(cancellation.Token);
            }
        }
        finally
        {
            try
            {
                if (cluster is not null)
                {
                    using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(1));
                    await cluster.DisposeAsync().AsTask().WaitAsync(cancellation.Token);
                }
            }
            finally
            {
                clock?.Dispose();
            }
        }
    }
}

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("BVT"), TestCategory("Reminders"), TestCategory("ReminderTestKit")]
[Collection(ReminderServiceLifecycleCollection.Name)]
public sealed class ReminderServiceLifecycleConformanceTests
{
    [Fact]
    public Task ReminderService_StartupReadiness()
        => RunAsync((runner, token) => runner.RunReminderService_StartupReadiness(token));

    [Fact]
    public Task ReminderService_RegistrationHasSingleOwner()
        => RunAsync((runner, token) => runner.RunReminderService_RegistrationHasSingleOwner(token));

    [Fact]
    public Task ReminderService_UpdateDoesNotRestartLocalOwner()
        => RunAsync((runner, token) => runner.RunReminderService_UpdateDoesNotRestartLocalOwner(token));

    [Fact]
    public Task ReminderService_RemovalReachesQuiescence()
        => RunAsync((runner, token) => runner.RunReminderService_RemovalReachesQuiescence(token));

    [Fact]
    public Task ReminderService_ExactDueRecovery()
        => RunAsync((runner, token) => runner.RunReminderService_ExactDueRecovery(token));

    [Fact]
    public Task ReminderService_StaleOwnerRegistrationReconciles()
        => RunAsync((runner, token) => runner.RunReminderService_StaleOwnerRegistrationReconciles(token));

    [Fact]
    public Task ReminderService_OneSiloJoinLeaveTransfersOwnership()
        => RunAsync((runner, token) => runner.RunReminderService_OneSiloJoinLeaveTransfersOwnership(token));

    [Fact]
    public Task ReminderService_CleanupIsIsolated()
        => RunAsync((runner, token) => runner.RunReminderService_CleanupIsIsolated(token));

    private static async Task RunAsync(
        Func<ReminderServiceLifecycleTestRunner, CancellationToken, Task> scenario)
    {
        var fixture = new ReminderServiceLifecycleFixture();
        await fixture.InitializeAsync();
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            cancellation.CancelAfter(TimeSpan.FromMinutes(2));
            await scenario(
                new LifecycleRunner(fixture.Harness, "IdealizedReminderTable"),
                cancellation.Token);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private sealed class LifecycleRunner(IReminderServiceLifecycleHarness harness, string providerName)
        : ReminderServiceLifecycleTestRunner(harness, providerName, seed: 42);
}

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("BVT"), TestCategory("Reminders"), TestCategory("ReminderTestKit")]
[Collection(ReminderServiceLifecycleCollection.Name)]
public sealed class FaultyReminderServiceLifecycleTests
{
    [Fact]
    public async Task PartiallyInitializedFixtureDisposesClock()
    {
        var clock = ReminderTestClock.Attach(new InProcessTestClusterBuilder(1));
        var fixture = new ReminderServiceLifecycleFixture(clock);

        await fixture.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => clock.AdvanceAsync(TimeSpan.Zero, TestContext.Current.CancellationToken));
    }

    [Fact]
    public Task DuplicateOwnerImplementationIsRejected()
        => RunFaultAsync(
            harness => new LifecycleRunner(new DuplicateOwnerHarness(harness), "DuplicateOwner"),
            async (runner, token) =>
            {
                var exception = await Assert.ThrowsAsync<ReminderConformanceException>(
                    () => runner.RunReminderService_RegistrationHasSingleOwner(token));
                Assert.Contains("exactly 1 local owner", exception.Message, StringComparison.Ordinal);
                Assert.Contains("owners=[", exception.Message, StringComparison.Ordinal);
            });

    [Fact]
    public Task RestartingUpdateImplementationIsRejected()
        => RunFaultAsync(
            harness => new LifecycleRunner(new RestartingUpdateHarness(harness), "RestartingUpdate"),
            async (runner, token) =>
            {
                var exception = await Assert.ThrowsAsync<ReminderConformanceException>(
                    () => runner.RunReminderService_UpdateDoesNotRestartLocalOwner(token));
                Assert.Contains("same single owner", exception.Message, StringComparison.Ordinal);
                Assert.Contains("starts=2", exception.Message, StringComparison.Ordinal);
            });

    [Fact]
    public async Task CanceledScenarioPreservesCancellationAndStillCleansItsRows()
    {
        var fixture = new ReminderServiceLifecycleFixture();
        await fixture.InitializeAsync();
        try
        {
            var runner = new LifecycleRunner(new BlockingScheduleHarness(fixture.Harness), "CanceledScenario");
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => runner.RunReminderService_RegistrationHasSingleOwner(cancellation.Token));

            var table = Assert.IsType<IdealizedReminderTable>(fixture.Harness.ReminderTable);
            Assert.Empty(table.Snapshot());
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public Task UpdateWaitsForRefreshBeforeExpectingScheduleReconciliation()
    {
        RefreshGatedScheduleHarness? gatedHarness = null;
        return RunFaultAsync(
            harness => new LifecycleRunner(
                gatedHarness = new RefreshGatedScheduleHarness(harness),
                "RefreshGatedUpdate"),
            async (runner, token) =>
            {
                await runner.RunReminderService_UpdateDoesNotRestartLocalOwner(token);
                Assert.NotNull(gatedHarness);
                Assert.True(gatedHarness.ReleasedByRefresh);
            });
    }

    [Fact]
    public Task StaleOwnerRegistrationTargetsOneSpecifiedNonOwner()
    {
        DirectedRegistrationHarness? directedHarness = null;
        return RunFaultAsync(
            harness => new LifecycleRunner(
                directedHarness = new DirectedRegistrationHarness(harness),
                "DirectedStaleOwner"),
            async (runner, token) =>
            {
                await runner.RunReminderService_StaleOwnerRegistrationReconciles(token);
                Assert.NotNull(directedHarness);
                Assert.Equal(1, directedHarness.RegistrationCount);
                Assert.True(directedHarness.TargetedNonOwner);
            },
            initialSilos: 2);
    }

    [Fact]
    public Task JoinLeaveAdvancesToTheExactRemainingDueTime()
    {
        RecordingAdvanceHarness? recordingHarness = null;
        return RunFaultAsync(
            harness => new LifecycleRunner(
                recordingHarness = new RecordingAdvanceHarness(harness),
                "ExactJoinLeaveDue"),
            async (runner, token) =>
            {
                await runner.RunReminderService_OneSiloJoinLeaveTransfersOwnership(token);
                Assert.NotNull(recordingHarness);
                Assert.Equal([TimeSpan.FromSeconds(3)], recordingHarness.Advances);
            });
    }

    private static async Task RunFaultAsync(
        Func<IReminderServiceLifecycleHarness, LifecycleRunner> createRunner,
        Func<LifecycleRunner, CancellationToken, Task> scenario,
        short initialSilos = 1)
    {
        var fixture = new ReminderServiceLifecycleFixture(initialSilos);
        await fixture.InitializeAsync();
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            cancellation.CancelAfter(TimeSpan.FromMinutes(2));
            await scenario(createRunner(fixture.Harness), cancellation.Token);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private sealed class LifecycleRunner(IReminderServiceLifecycleHarness harness, string providerName)
        : ReminderServiceLifecycleTestRunner(harness, providerName);

    private class DelegatingHarness(IReminderServiceLifecycleHarness inner) : IReminderServiceLifecycleHarness
    {
        protected IReminderServiceLifecycleHarness Inner { get; } = inner;

        public IGrainFactory GrainFactory => Inner.GrainFactory;
        public IReminderTable ReminderTable => Inner.ReminderTable;
        public DateTimeOffset UtcNow => Inner.UtcNow;
        public TimeSpan ReminderLoadingWindow => Inner.ReminderLoadingWindow;
        public TimeSpan ReminderRefreshPeriod => Inner.ReminderRefreshPeriod;
        public IReadOnlyList<SiloAddress> ActiveSilos => Inner.ActiveSilos;
        public Task WaitForStartupReadinessAsync(CancellationToken cancellationToken) => Inner.WaitForStartupReadinessAsync(cancellationToken);
        public virtual Task RegisterOnSiloAsync(SiloAddress siloAddress, GrainId grainId, string reminderName, TimeSpan dueTime, TimeSpan period, CancellationToken cancellationToken) => Inner.RegisterOnSiloAsync(siloAddress, grainId, reminderName, dueTime, period, cancellationToken);
        public virtual Task AdvanceAsync(TimeSpan amount, CancellationToken cancellationToken) => Inner.AdvanceAsync(amount, cancellationToken);
        public virtual Task RefreshAsync(CancellationToken cancellationToken) => Inner.RefreshAsync(cancellationToken);
        public virtual Task WaitForOwnerCountAsync(GrainId grainId, string reminderName, int count, CancellationToken cancellationToken) => Inner.WaitForOwnerCountAsync(grainId, reminderName, count, cancellationToken);
        public virtual IReadOnlyList<SiloAddress> GetOwners(GrainId grainId, string reminderName) => Inner.GetOwners(grainId, reminderName);
        public bool IsOwner(SiloAddress siloAddress, GrainId grainId) => Inner.IsOwner(siloAddress, grainId);
        public virtual Task WaitForScheduleAsync(GrainId grainId, string reminderName, CancellationToken cancellationToken) => Inner.WaitForScheduleAsync(grainId, reminderName, cancellationToken);
        public virtual int GetLocalStartCount(GrainId grainId, string reminderName) => Inner.GetLocalStartCount(grainId, reminderName);
        public int GetLocalStopCount(GrainId grainId, string reminderName) => Inner.GetLocalStopCount(grainId, reminderName);
        public int GetScheduleChangeCount(GrainId grainId, string reminderName) => Inner.GetScheduleChangeCount(grainId, reminderName);
        public virtual Task WaitForScheduleChangeCountAsync(GrainId grainId, string reminderName, int count, CancellationToken cancellationToken) => Inner.WaitForScheduleChangeCountAsync(grainId, reminderName, count, cancellationToken);
        public Task WaitForTickCountAsync(GrainId grainId, string reminderName, int count, CancellationToken cancellationToken) => Inner.WaitForTickCountAsync(grainId, reminderName, count, cancellationToken);
        public int GetTickCount(GrainId grainId, string reminderName) => Inner.GetTickCount(grainId, reminderName);
        public Task<SiloAddress> JoinOneSiloAsync(CancellationToken cancellationToken) => Inner.JoinOneSiloAsync(cancellationToken);
        public Task LeaveSiloAsync(SiloAddress siloAddress, CancellationToken cancellationToken) => Inner.LeaveSiloAsync(siloAddress, cancellationToken);
        public Task WaitForTopologyReconciliationAsync(CancellationToken cancellationToken) => Inner.WaitForTopologyReconciliationAsync(cancellationToken);
    }

    private sealed class DirectedRegistrationHarness(IReminderServiceLifecycleHarness inner) : DelegatingHarness(inner)
    {
        public int RegistrationCount { get; private set; }
        public bool TargetedNonOwner { get; private set; }

        public override async Task RegisterOnSiloAsync(
            SiloAddress siloAddress,
            GrainId grainId,
            string reminderName,
            TimeSpan dueTime,
            TimeSpan period,
            CancellationToken cancellationToken)
        {
            RegistrationCount++;
            TargetedNonOwner = !Inner.IsOwner(siloAddress, grainId);
            await base.RegisterOnSiloAsync(
                siloAddress,
                grainId,
                reminderName,
                dueTime,
                period,
                cancellationToken);
        }
    }

    private sealed class DuplicateOwnerHarness(IReminderServiceLifecycleHarness inner) : DelegatingHarness(inner)
    {
        private object? _duplicateIdentity;

        public override async Task WaitForOwnerCountAsync(
            GrainId grainId,
            string reminderName,
            int count,
            CancellationToken cancellationToken)
        {
            var harness = Assert.IsType<ReminderServiceLifecycleHarness>(Inner);
            if (count == 0 && _duplicateIdentity is { } identity)
            {
                harness.RemoveDuplicateOwnerForTesting(grainId, reminderName, identity);
                _duplicateIdentity = null;
            }

            await base.WaitForOwnerCountAsync(grainId, reminderName, count, cancellationToken);
            if (count == 1 && _duplicateIdentity is null)
            {
                _duplicateIdentity = harness.AddDuplicateOwnerForTesting(grainId, reminderName);
            }
        }
    }

    private sealed class RestartingUpdateHarness(IReminderServiceLifecycleHarness inner) : DelegatingHarness(inner)
    {
        private bool _updated;

        public override int GetLocalStartCount(GrainId grainId, string reminderName)
            => base.GetLocalStartCount(grainId, reminderName) + (_updated ? 1 : 0);

        public override async Task WaitForScheduleChangeCountAsync(
            GrainId grainId,
            string reminderName,
            int count,
            CancellationToken cancellationToken)
        {
            await base.WaitForScheduleChangeCountAsync(grainId, reminderName, count, cancellationToken);
            _updated = true;
        }
    }

    private sealed class BlockingScheduleHarness(IReminderServiceLifecycleHarness inner) : DelegatingHarness(inner)
    {
        public override Task WaitForScheduleAsync(
            GrainId grainId,
            string reminderName,
            CancellationToken cancellationToken)
            => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private sealed class RefreshGatedScheduleHarness(IReminderServiceLifecycleHarness inner) : DelegatingHarness(inner)
    {
        private TaskCompletionSource? _refreshGate;

        public bool ReleasedByRefresh { get; private set; }

        public override async Task WaitForScheduleChangeCountAsync(
            GrainId grainId,
            string reminderName,
            int count,
            CancellationToken cancellationToken)
        {
            var innerWait = base.WaitForScheduleChangeCountAsync(
                grainId,
                reminderName,
                count,
                cancellationToken);
            var refreshGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _refreshGate = refreshGate;
            await Task.WhenAll(innerWait, refreshGate.Task.WaitAsync(cancellationToken));
            ReleasedByRefresh = true;
        }

        public override async Task RefreshAsync(CancellationToken cancellationToken)
        {
            await base.RefreshAsync(cancellationToken);
            Interlocked.Exchange(ref _refreshGate, null)?.TrySetResult();
        }
    }

    private sealed class RecordingAdvanceHarness(IReminderServiceLifecycleHarness inner) : DelegatingHarness(inner)
    {
        public List<TimeSpan> Advances { get; } = [];

        public override async Task AdvanceAsync(TimeSpan amount, CancellationToken cancellationToken)
        {
            Advances.Add(amount);
            await base.AdvanceAsync(amount, cancellationToken);
        }
    }
}
