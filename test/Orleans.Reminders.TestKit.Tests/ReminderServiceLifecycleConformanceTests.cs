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
    private ReminderDiagnosticObserver? _observer;

    public ReminderServiceLifecycleHarness Harness { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = new InProcessTestClusterBuilder(1);
        builder.ConfigureSilo((_, siloBuilder) =>
            siloBuilder.Configure<ConsistentRingOptions>(options => options.UseVirtualBucketsConsistentRing = false));
        builder.UseIdealizedReminderTable(
            configureReminderOptions: options => options.ReminderLoadingWindow = LoadingWindow);
        _clock = ReminderTestClock.Attach(
            builder,
            minimumReminderPeriod: TimeSpan.FromSeconds(1),
            refreshReminderListPeriod: TimeSpan.FromSeconds(1));
        _observer = ReminderDiagnosticObserver.Create();
        _cluster = builder.Build();
        await _cluster.DeployAsync(TestContext.Current.CancellationToken);
        Harness = new ReminderServiceLifecycleHarness(_cluster, _clock, _observer, LoadingWindow);
    }

    public async ValueTask DisposeAsync()
    {
        if (_cluster is not { } cluster)
        {
            return;
        }

        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            await cluster.StopAllSilosAsync(cancellation.Token);
        }
        finally
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            await cluster.DisposeAsync().AsTask().WaitAsync(cancellation.Token);
            _observer?.Dispose();
            _clock?.Dispose();
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

    private static async Task RunFaultAsync(
        Func<IReminderServiceLifecycleHarness, LifecycleRunner> createRunner,
        Func<LifecycleRunner, CancellationToken, Task> scenario)
    {
        var fixture = new ReminderServiceLifecycleFixture();
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
        public Task AdvanceAsync(TimeSpan amount, CancellationToken cancellationToken) => Inner.AdvanceAsync(amount, cancellationToken);
        public Task WaitForOwnerCountAsync(GrainId grainId, string reminderName, int count, CancellationToken cancellationToken) => Inner.WaitForOwnerCountAsync(grainId, reminderName, count, cancellationToken);
        public virtual IReadOnlyList<SiloAddress> GetOwners(GrainId grainId, string reminderName) => Inner.GetOwners(grainId, reminderName);
        public Task WaitForScheduleAsync(GrainId grainId, string reminderName, CancellationToken cancellationToken) => Inner.WaitForScheduleAsync(grainId, reminderName, cancellationToken);
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

    private sealed class DuplicateOwnerHarness(IReminderServiceLifecycleHarness inner) : DelegatingHarness(inner)
    {
        public override IReadOnlyList<SiloAddress> GetOwners(GrainId grainId, string reminderName)
        {
            var owners = base.GetOwners(grainId, reminderName);
            return owners.Count == 1 ? [owners[0], owners[0]] : owners;
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
}
